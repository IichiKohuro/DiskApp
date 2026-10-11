using System.Threading.Channels;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Localization;

using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Sqlite;

/// <summary>
/// Единственный писатель базы (docs/ARCHITECTURE.md, раздел 4): все изменения выполняются по очереди
/// на выделенном потоке с одним постоянным соединением, поэтому писатели не конкурируют за блокировку,
/// а читатели в WAL работают параллельно. Задание — синхронный делегат; его исключение возвращается
/// вызывающему и не останавливает очередь.
/// </summary>
public sealed class SqliteWriter : IDisposable, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly Channel<IWriteJob> _channel = Channel.CreateUnbounded<IWriteJob>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });

    private readonly Thread _thread;
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pending;
    private int _disposed;
    private long _lastActivity = Environment.TickCount64;
    private long _completedCount;

    /// <summary>
    /// Запускает поток писателя; соединение открывается на нём при старте.
    /// </summary>
    public SqliteWriter(SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory;
        _thread = new Thread(Run)
        {
            Name = "DiskApp SQLite writer",
            IsBackground = true,
        };
        _thread.Start();
    }

    /// <summary>Сколько заданий ждут выполнения или выполняются.</summary>
    public int PendingCount => Volatile.Read(ref _pending);

    /// <summary>Очередь остановлена: новые задания отклоняются.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Сколько прошло с последней записи; пока задания в очереди — ноль (обслуживание при простое, этап 10.2).</summary>
    public TimeSpan SinceLastActivity => PendingCount > 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastActivity));

    /// <summary>Сколько заданий выполнено за всё время.</summary>
    public long CompletedCount => Interlocked.Read(ref _completedCount);

    /// <summary>
    /// Ставит задание в очередь и возвращает его результат. Транзакцию задание открывает само, если нужна.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Очередь остановлена.</exception>
    public Task<T> EnqueueAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var job = new WriteJob<T>(work, cancellationToken);
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(job))
        {
            Interlocked.Decrement(ref _pending);
            throw new ObjectDisposedException(nameof(SqliteWriter));
        }

        return job.Task;
    }

    /// <summary>
    /// Ставит задание без результата.
    /// </summary>
    public Task EnqueueAsync(Action<SqliteConnection> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return EnqueueAsync(connection =>
        {
            work(connection);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Запрещает новые задания, дожидается уже поставленных и закрывает соединение.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }
    }

    /// <summary>
    /// Асинхронный вариант <see cref="Dispose"/>: ожидание потока писателя без блокировки вызывающего.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _completed.Task.ConfigureAwait(false);
            return;
        }

        _channel.Writer.TryComplete();
        await _completed.Task.ConfigureAwait(false);
    }

    private void Run()
    {
        SqliteConnection? connection = null;
        Exception? fault = null;

        try
        {
            try
            {
                connection = _factory.Open();
            }
            catch (Exception exception)
            {
                fault = exception;
            }

            while (true)
            {
                IWriteJob job;
                try
                {
                    job = _channel.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                try
                {
                    if (fault is not null)
                    {
                        job.Fail(new InvalidOperationException("Соединение писателя не открылось.", fault));
                    }
                    else
                    {
                        job.Execute(connection!);
                    }
                }
                finally
                {
                    Volatile.Write(ref _lastActivity, Environment.TickCount64);
                    Interlocked.Increment(ref _completedCount);
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        finally
        {
            connection?.Dispose();
            _completed.TrySetResult();
        }
    }

    private interface IWriteJob
    {
        void Execute(SqliteConnection connection);

        void Fail(Exception exception);
    }

    private sealed class WriteJob<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken) : IWriteJob
    {
        // Код ошибки SQLite «база или диск заполнены».
        private const int SqliteFull = 13;

        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public void Execute(SqliteConnection connection)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                _completion.TrySetResult(work(connection));
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
            {
                _completion.TrySetCanceled(cancellationToken);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteFull)
            {
                // Нет места на диске с базой (этап 10.3): понятная причина вместо «database or disk is full».
                _completion.TrySetException(new DiskFullException(L.T("Sqlite.DiskFull"), exception));
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }
}
