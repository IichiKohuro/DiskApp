using System.Collections.Concurrent;

namespace DiskApp.UI.Services;

/// <summary>
/// Раскладка страниц, которую пользователь подогнал под себя: ширина панелей за разделителями. Хранится рядом с
/// положением окна, а не в настройках — это не настройка, а «как оставил».
/// </summary>
public interface ILayoutStore
{
    /// <summary>Ширина панели; <c>null</c> — ещё не меняли.</summary>
    /// <param name="key">Панель: «sorting.destinations», «notes.list».</param>
    double? GetWidth(string key);

    /// <summary>Запоминает ширину панели; запись на диск — с задержкой, пока тянут разделитель.</summary>
    /// <param name="key">Панель.</param>
    /// <param name="width">Ширина в пикселях интерфейса.</param>
    void SetWidth(string key, double width);
}

/// <summary>
/// Раскладка в памяти — для превью и тестов.
/// </summary>
public sealed class MemoryLayoutStore : ILayoutStore
{
    private readonly ConcurrentDictionary<string, double> _widths = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public double? GetWidth(string key) => _widths.TryGetValue(key, out var width) ? width : null;

    /// <inheritdoc />
    public void SetWidth(string key, double width) => _widths[key] = width;
}
