using System.IO.Pipes;

namespace DiskApp.Desktop.SingleInstance;

/// <summary>
/// Клиент именованного канала: второй экземпляр отдаёт свои аргументы первому и завершается.
/// </summary>
internal static class InstanceClient
{
    /// <summary>
    /// Передаёт аргументы первому экземпляру.
    /// </summary>
    /// <returns><c>true</c>, если первый экземпляр принял сообщение; <c>false</c>, если канал недоступен в отведённое время.</returns>
    public static async Task<bool> TrySendAsync(string pipeName, IReadOnlyList<string> args, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cancellation = new CancellationTokenSource(timeout);
            await pipe.ConnectAsync(cancellation.Token);
            var message = InstanceProtocol.Encode(args);
            await pipe.WriteAsync(message, cancellation.Token);
            await pipe.FlushAsync(cancellation.Token);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
