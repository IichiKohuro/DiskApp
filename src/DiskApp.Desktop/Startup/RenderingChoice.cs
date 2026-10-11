using System.Text.Json;

namespace DiskApp.Desktop.Startup;

/// <summary>
/// Выбор рендеринга до запуска Avalonia (PLAN.md, этап 10.2; ADR-0009). По умолчанию окна рисуются программно: GPU-путь
/// (ANGLE, Direct3D 11) добавляет около 90 МБ частной памяти и полсотни потоков драйвера и не укладывается в бюджет памяти
/// в простое. Аппаратное ускорение включается настройкой <c>appearance.hardwareAcceleration</c>; контейнер настроек в
/// этот момент ещё не собран, поэтому поле читается прямо из файла. Переменная <c>DISKAPP_RENDERING</c> (<c>gpu</c> или
/// <c>software</c>) переопределяет настройку для проверки.
/// </summary>
internal static class RenderingChoice
{
    /// <summary>Переменная окружения для проверки: <c>gpu</c> или <c>software</c>.</summary>
    public const string Variable = "DISKAPP_RENDERING";

    /// <summary>Включить аппаратное ускорение.</summary>
    /// <param name="settingsPath">Файл настроек; отсутствующий или нечитаемый — значение по умолчанию.</param>
    /// <param name="overrideValue">Значение переменной окружения; <c>null</c> — не задана.</param>
    public static bool UseHardwareAcceleration(string settingsPath, string? overrideValue)
    {
        if (string.Equals(overrideValue, "gpu", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(overrideValue, "software", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (!File.Exists(settingsPath))
            {
                return false;
            }

            using var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("appearance", out var appearance)
                && appearance.ValueKind == JsonValueKind.Object
                && appearance.TryGetProperty("hardwareAcceleration", out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
