using System.IO.Pipes;

using Serilog;

namespace DiskApp.Desktop.SingleInstance;

/// <summary>
/// Сервер именованного канала первого экземпляра: принимает аргументы от вторых экземпляров
/// и передаёт их обработчику. Один клиент за раз, канал доступен только текущему пользователю.
/// </summary>
internal sealed class InstanceServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Action<IReadOnlyList<string>> _onArguments;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    /// <summary>
    /// Создаёт сервер; приём начинается после <see cref="Start"/>.
    /// </summary>
    /// <param name="pipeName">Имя канала из <see cref="InstanceNames"/>.</param>
    /// <param name="onArguments">Обработчик аргументов; вызывается в фоновом потоке.</param>
    /// <param name="log">Журнал.</param>
    public InstanceServer(string pipeName, Action<IReadOnlyList<string>> onArguments, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(onArguments);
        ArgumentNullException.ThrowIfNull(log);

        _pipeName = pipeName;
        _onArguments = onArguments;
        _log = log.ForContext<InstanceServer>();
    }

    /// <summary>
    /// Запускает цикл приёма в фоне.
    /// </summary>
    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("Сервер одиночного экземпляра уже запущен.");
        }

        _loop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Цикл завершился отменой: это ожидаемо.
        }

        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken);
                var args = await InstanceProtocol.DecodeAsync(pipe, cancellationToken);
                if (args is not null)
                {
                    _log.Information("Второй экземпляр передал аргументы: {Arguments}", args);
                    _onArguments(args);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException exception)
            {
                _log.Warning(exception, "Ошибка канала одиночного экземпляра; приём продолжается");
            }
        }
    }
}
