using System.Text.Json;
using System.Text.Json.Serialization;

using Avalonia;

namespace DiskApp.Desktop.Windows;

/// <summary>
/// Положение окна: верхний левый угол в пикселях экрана, размер в независимых единицах, развёрнуто ли.
/// </summary>
/// <param name="X">Левый край, пиксели.</param>
/// <param name="Y">Верхний край, пиксели.</param>
/// <param name="Width">Ширина, DIP.</param>
/// <param name="Height">Высота, DIP.</param>
/// <param name="Maximized">Окно развёрнуто на весь экран.</param>
internal sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized);

/// <summary>
/// Файл состояния окон.
/// </summary>
internal sealed record WindowStateFile
{
    /// <summary>Главное окно.</summary>
    public WindowPlacement? Main { get; set; }

    /// <summary>Ширина панелей за разделителями: «sorting.destinations» → 420.</summary>
    public Dictionary<string, double>? Panels { get; set; }
}

/// <summary>
/// Запоминает положение главного окна между запусками (PLAN.md, этап 6.7) и ширину панелей страниц в
/// <c>window-state.json</c> рядом с настройками. Испорченный файл не мешает запуску: окно откроется по центру.
/// </summary>
internal sealed class WindowPlacementStore(string path)
{
    /// <summary>Сколько пикселей заголовка должно попадать на экран, чтобы окно можно было схватить мышью.</summary>
    public const int MinimumVisible = 80;

    // Окно и панели пишут один файл из разных мест: чтение и запись — по очереди.
    private static readonly Lock Gate = new();

    /// <summary>Сохранённое положение главного окна или <c>null</c>.</summary>
    public WindowPlacement? LoadMain() => Read()?.Main is { Width: > 0, Height: > 0 } placement ? placement : null;

    /// <summary>Сохраняет положение главного окна; ошибка записи не мешает выходу.</summary>
    public void SaveMain(WindowPlacement placement) => Update(state => state.Main = placement);

    /// <summary>Сохранённая ширина панелей; пусто — не меняли.</summary>
    public IReadOnlyDictionary<string, double> LoadPanels()
        => Read()?.Panels is { } panels
            ? panels.Where(static panel => double.IsFinite(panel.Value) && panel.Value > 0).ToDictionary(StringComparer.Ordinal)
            : new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Сохраняет ширину панелей, не трогая положение окна.</summary>
    public void SavePanels(IReadOnlyDictionary<string, double> panels)
    {
        ArgumentNullException.ThrowIfNull(panels);
        Update(state => state.Panels = new Dictionary<string, double>(panels, StringComparer.Ordinal));
    }

    private WindowStateFile? Read()
    {
        lock (Gate)
        {
            return ReadFile();
        }
    }

    private WindowStateFile? ReadFile()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, WindowStateJsonContext.Default.WindowStateFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Update(Action<WindowStateFile> change)
    {
        lock (Gate)
        {
            try
            {
                var state = ReadFile() ?? new WindowStateFile();
                change(state);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(state, WindowStateJsonContext.Default.WindowStateFile));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Warning(exception, "Состояние окна не сохранено в {Path}", path);
            }
        }
    }

    /// <summary>
    /// Попадает ли заголовок окна на один из экранов (рабочие области в пикселях): после отключения монитора
    /// сохранённое положение может оказаться за пределами — тогда окно откроется по центру.
    /// </summary>
    public static bool IsReachable(WindowPlacement placement, IEnumerable<PixelRect> workingAreas)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(workingAreas);

        var titleBar = new PixelRect(placement.X, placement.Y, Math.Max(MinimumVisible, (int)placement.Width), 32);
        return workingAreas.Any(area =>
        {
            var overlap = area.Intersect(titleBar);
            return overlap.Width >= MinimumVisible && overlap.Height >= 16;
        });
    }
}

/// <summary>
/// Контекст сериализации состояния окон.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(WindowStateFile))]
internal sealed partial class WindowStateJsonContext : JsonSerializerContext
{
}
