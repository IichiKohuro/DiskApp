using System.Text;
using System.Text.Json;

namespace DiskApp.Desktop.SingleInstance;

/// <summary>
/// Сообщение второго экземпляра первому: аргументы командной строки одной строкой JSON (массив строк)
/// с переводом строки в конце. Кодировка UTF-8, поэтому кириллица и пробелы в путях проходят без потерь.
/// </summary>
internal static class InstanceProtocol
{
    private const int MaxMessageBytes = 1024 * 1024;

    /// <summary>
    /// Кодирует аргументы в байты сообщения.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var json = JsonSerializer.Serialize(args, InstanceJsonContext.Default.IReadOnlyListString);
        return Encoding.UTF8.GetBytes(json + "\n");
    }

    /// <summary>
    /// Читает одно сообщение из потока до перевода строки или конца потока.
    /// </summary>
    /// <returns>Аргументы или <c>null</c>, если поток пуст или сообщение повреждено.</returns>
    public static async Task<IReadOnlyList<string>?> DecodeAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (buffer.Length < MaxMessageBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                buffer.Write(chunk, 0, newline);
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), InstanceJsonContext.Default.IReadOnlyListString);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
