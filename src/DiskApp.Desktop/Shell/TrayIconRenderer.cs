using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DiskApp.Desktop.Shell;

/// <summary>
/// Иконка трея по состоянию (PLAN.md, этап 6.3): основная иконка и цветной значок в правом нижнем углу —
/// синий при индексации, жёлтый на паузе, серый при недоступных корнях, красный при ошибке. Значок обведён
/// белым и тёмным контуром, поэтому читается и на светлой, и на тёмной панели задач. Рисуется один раз на состояние.
/// </summary>
internal sealed class TrayIconRenderer(Uri baseIcon)
{
    private const int Size = 32;
    private readonly Dictionary<TrayState, WindowIcon> _cache = [];
    private Bitmap? _base;

    /// <summary>Иконка для состояния.</summary>
    public WindowIcon For(TrayState state)
    {
        if (_cache.TryGetValue(state, out var cached))
        {
            return cached;
        }

        WindowIcon icon;
        if (Badge(state) is { } color)
        {
            _base ??= LoadBase();
            icon = Render(_base, color);
        }
        else
        {
            // Без значка — сама иконка из ресурса: так на старте не нужна отрисовка и трей появляется быстрее.
            using var stream = AssetLoader.Open(baseIcon);
            icon = new WindowIcon(stream);
        }

        _cache[state] = icon;
        return icon;
    }

    private static Color? Badge(TrayState state) => state switch
    {
        TrayState.Indexing => Color.FromRgb(0x00, 0x78, 0xD4),
        TrayState.Paused => Color.FromRgb(0xF7, 0xB5, 0x00),
        TrayState.Offline => Color.FromRgb(0x8A, 0x8A, 0x8A),
        TrayState.Error => Color.FromRgb(0xD1, 0x34, 0x38),
        _ => null,
    };

    private static WindowIcon Render(Bitmap source, Color badge)
    {
        using var target = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(source, new Rect(0, 0, source.Size.Width, source.Size.Height), new Rect(0, 0, Size, Size));
            var center = new Point(Size - 8, Size - 8);
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x20, 0x20)), null, center, 8, 8);
            context.DrawEllipse(Brushes.White, null, center, 7, 7);
            context.DrawEllipse(new SolidColorBrush(badge), null, center, 5.5, 5.5);
        }

        using var stream = new MemoryStream();
        target.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    private Bitmap LoadBase()
    {
        using var stream = AssetLoader.Open(baseIcon);
        return Bitmap.DecodeToWidth(stream, Size, BitmapInterpolationMode.HighQuality);
    }
}
