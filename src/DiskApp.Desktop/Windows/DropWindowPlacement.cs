using Avalonia;

namespace DiskApp.Desktop.Windows;

/// <summary>
/// Где показать окно Drag&amp;Drop (PLAN.md, этап 18.1, ADR-0011): у панели задач того монитора, где курсор, как
/// всплывающие окна области уведомлений Windows. Сторона панели задач определяется по разнице границ монитора и рабочей
/// области; скрытая панель — как нижняя. Если курсор на самой панели (щелчок по значку), окно встаёт напротив курсора,
/// иначе (хоткей) — в углу у области уведомлений.
/// </summary>
internal static class DropWindowPlacement
{
    private enum Edge
    {
        Bottom,
        Top,
        Left,
        Right,
    }

    /// <summary>
    /// Левый верхний угол окна в пикселях экрана.
    /// </summary>
    /// <param name="bounds">Границы монитора.</param>
    /// <param name="workingArea">Рабочая область монитора (без панели задач).</param>
    /// <param name="cursor">Положение курсора.</param>
    /// <param name="size">Размер окна в пикселях.</param>
    /// <param name="margin">Отступ от краёв рабочей области в пикселях.</param>
    public static PixelPoint Compute(PixelRect bounds, PixelRect workingArea, PixelPoint cursor, PixelSize size, int margin)
    {
        var area = workingArea;
        var bottom = bounds.Bottom - area.Bottom;
        var top = area.Y - bounds.Y;
        var left = area.X - bounds.X;
        var right = bounds.Right - area.Right;
        var widest = Math.Max(Math.Max(bottom, top), Math.Max(left, right));
        var edge = widest <= 0 || widest == bottom ? Edge.Bottom
            : widest == top ? Edge.Top
            : widest == left ? Edge.Left
            : Edge.Right;

        var nearX = area.Right - size.Width - margin;
        var nearY = area.Bottom - size.Height - margin;
        var (x, y) = edge switch
        {
            Edge.Top => (cursor.Y < area.Y ? cursor.X - (size.Width / 2) : nearX, area.Y + margin),
            Edge.Left => (area.X + margin, cursor.X < area.X ? cursor.Y - (size.Height / 2) : nearY),
            Edge.Right => (nearX, cursor.X >= area.Right ? cursor.Y - (size.Height / 2) : nearY),
            _ => (cursor.Y >= area.Bottom ? cursor.X - (size.Width / 2) : nearX, nearY),
        };

        x = Math.Clamp(x, area.X + margin, Math.Max(area.X + margin, nearX));
        y = Math.Clamp(y, area.Y + margin, Math.Max(area.Y + margin, nearY));
        return new PixelPoint(x, y);
    }
}
