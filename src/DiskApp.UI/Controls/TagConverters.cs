using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DiskApp.UI.Controls;

/// <summary>
/// Конвертеры для чипов тегов (PLAN.md, этап 8.7).
/// </summary>
public static class TagConverters
{
    private static readonly IBrush Neutral = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

    /// <summary>Цвет тега <c>#RRGGBB</c> → кисть; без цвета или с неверным — нейтральный серый.</summary>
    public static FuncValueConverter<string?, IBrush> Brush { get; } = new(static color
        => color is not null && Color.TryParse(color, out var parsed) ? new SolidColorBrush(parsed) : Neutral);
}
