using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiskApp.Infrastructure.Diagnostics;

/// <summary>Что положить в диагностический архив.</summary>
/// <param name="OutputPath">Путь к создаваемому <c>.zip</c>.</param>
/// <param name="LogsDirectory">Папка журнала.</param>
/// <param name="SettingsPath">Файл настроек; <c>null</c> или отсутствующий файл — без настроек.</param>
/// <param name="Environment">Версия, ОС, среда выполнения и прочие сведения «имя → значение».</param>
public sealed record DiagnosticArchiveRequest(string OutputPath, string LogsDirectory, string? SettingsPath, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>Сколько последних файлов журнала взять.</summary>
    public int MaxLogFiles { get; init; } = 5;

    /// <summary>Предел размера одного файла журнала: от большего берётся конец.</summary>
    public long MaxLogBytes { get; init; } = 10 * 1024 * 1024;
}

/// <summary>
/// Диагностический архив (PLAN.md, этап 9.5): последние файлы журнала, настройки без секретов, версия и окружение — одним
/// <c>.zip</c>, который пользователь сам прикладывает к сообщению о проблеме. Журнал читается, пока Serilog держит его
/// открытым на запись.
/// </summary>
public static class DiagnosticArchive
{
    /// <summary>Чем заменяется значение секрета в настройках.</summary>
    public const string Mask = "***";

    private static readonly string[] SecretWords = ["token", "password", "secret", "apikey", "credential"];

    /// <summary>Создаёт архив; существующий файл заменяется.</summary>
    /// <returns>Путь к архиву.</returns>
    public static async Task<string> CreateAsync(DiagnosticArchiveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Path.GetDirectoryName(Path.GetFullPath(request.OutputPath)) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = request.OutputPath + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            await WriteTextAsync(zip, "environment.txt", DescribeEnvironment(request.Environment), cancellationToken).ConfigureAwait(false);

            if (request.SettingsPath is { } settingsPath && File.Exists(settingsPath))
            {
                var settings = await ReadSharedAsync(settingsPath, long.MaxValue, cancellationToken).ConfigureAwait(false);
                await WriteTextAsync(zip, "settings.json", RedactSettings(Encoding.UTF8.GetString(settings)), cancellationToken).ConfigureAwait(false);
            }

            foreach (var log in LatestLogs(request.LogsDirectory, request.MaxLogFiles))
            {
                var content = await ReadSharedAsync(log.FullName, request.MaxLogBytes, cancellationToken).ConfigureAwait(false);
                var entry = zip.CreateEntry("logs/" + log.Name, CompressionLevel.Optimal);
                await using var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }
        }

        File.Move(temporary, request.OutputPath, overwrite: true);
        return request.OutputPath;
    }

    /// <summary>
    /// Настройки без секретов: значения свойств, в имени которых есть «token», «password», «secret», «apikey»,
    /// «credential», заменяются на <see cref="Mask"/>. Нечитаемый JSON не попадает в архив как есть.
    /// </summary>
    public static string RedactSettings(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException exception)
        {
            return $"settings.json не читается: {exception.Message}";
        }

        Redact(root);
        return root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
    }

    /// <summary>Последние по времени изменения файлы журнала, новые первыми.</summary>
    public static IReadOnlyList<FileInfo> LatestLogs(string logsDirectory, int count)
    {
        var directory = new DirectoryInfo(logsDirectory);
        return directory.Exists
            ? [.. directory.EnumerateFiles("*.log").OrderByDescending(static file => file.LastWriteTimeUtc).Take(count)]
            : [];
    }

    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToList())
                {
                    if (value is JsonValue && SecretWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                    {
                        obj[name] = Mask;
                    }
                    else
                    {
                        Redact(value);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Redact(item);
                }

                break;
        }
    }

    private static string DescribeEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in environment)
        {
            text.Append(name).Append(": ").AppendLine(value);
        }

        return text.ToString();
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    // Журнал открыт Serilog на запись: читаем с разделением записи и удаления; от большого файла — только конец.
    private static async Task<byte[]> ReadSharedAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length > maxBytes)
        {
            stream.Seek(length - maxBytes, SeekOrigin.Begin);
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
