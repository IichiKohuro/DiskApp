using DiskApp.Core.Abstractions;
using DiskApp.Core.Settings;
using DiskApp.UI.Services;

namespace DiskApp.Desktop.Shell;

/// <summary>
/// Настройки «Добавить» в файле настроек приложения (раздел <c>add</c>).
/// </summary>
internal sealed class SettingsAddPreferences(ISettingsStore<AppSettings> store) : IAddPreferences
{
    /// <inheritdoc />
    public string? InboxPath => store.Current.Add.InboxPath;

    /// <inheritdoc />
    public IReadOnlyList<string> RecentTargets => store.Current.Add.RecentTargets;

    /// <inheritdoc />
    public string ClipboardNameTemplate => store.Current.Add.ClipboardNameTemplate;

    /// <inheritdoc />
    public Task RememberTargetAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var current = store.Current;
        var recent = new List<string> { path };
        recent.AddRange(current.Add.RecentTargets.Where(item => !string.Equals(item, path, StringComparison.OrdinalIgnoreCase)));
        var updated = current with { Add = current.Add with { RecentTargets = [.. recent.Take(AddSettings.RecentLimit)] } };
        return store.SaveAsync(updated, cancellationToken);
    }
}
