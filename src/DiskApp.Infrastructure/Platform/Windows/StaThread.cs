using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DiskApp.Infrastructure.Platform.Windows;

/// <summary>
/// Отдельный поток в однопоточном апартаменте COM для вызовов оболочки Windows: расширения Проводника, диалоги
/// и извлечение иконок ждут STA. Задания выполняются по одному в порядке постановки.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class StaThread : IDisposable
{
    private const uint CoinitApartmentThreaded = 0x2;

    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public StaThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Ставит работу в очередь потока; исключение работы передаётся в задачу.</summary>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }
        catch (InvalidOperationException)
        {
            completion.TrySetException(new ObjectDisposedException(_thread.Name));
        }

        return completion.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    private void Run()
    {
        // Среда уже открыла STA для потока с ApartmentState.STA; повторный вызов безвреден (S_FALSE) и страхует.
        _ = CoInitializeEx(0, CoinitApartmentThreaded);
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work();
        }
    }
}
