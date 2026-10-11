namespace DiskApp.UI.Services;

/// <summary>
/// Настройки сценария «Добавить» (PLAN.md, этап 8.5): папка «Входящие», недавние места назначения, шаблон имени
/// файла из буфера обмена. Десктоп хранит их в настройках приложения.
/// </summary>
public interface IAddPreferences
{
    /// <summary>Папка «Входящие»; <c>null</c> — не задана.</summary>
    string? InboxPath { get; }

    /// <summary>Недавние места назначения, самые свежие первыми.</summary>
    IReadOnlyList<string> RecentTargets { get; }

    /// <summary>Шаблон имени файла из буфера обмена.</summary>
    string ClipboardNameTemplate { get; }

    /// <summary>Запоминает место назначения как самое свежее.</summary>
    Task RememberTargetAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Настройки «Добавить» в памяти: для превью и тестов.
/// </summary>
public sealed class SampleAddPreferences : IAddPreferences
{
    private readonly List<string> _recent = [];

    /// <inheritdoc />
    public string? InboxPath { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<string> RecentTargets => _recent;

    /// <inheritdoc />
    public string ClipboardNameTemplate { get; set; } = "{date}_{time}_{title}";

    /// <inheritdoc />
    public Task RememberTargetAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _recent.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        _recent.Insert(0, path);
        if (_recent.Count > 8)
        {
            _recent.RemoveRange(8, _recent.Count - 8);
        }

        return Task.CompletedTask;
    }
}
