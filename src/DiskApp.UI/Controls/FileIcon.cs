using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using DiskApp.UI.Models;
using DiskApp.UI.Services;

using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace DiskApp.UI.Controls;

/// <summary>
/// Иконка записи в списках (PLAN.md, этап 7.4): настоящая иконка Windows или миниатюра с сервера, а пока она
/// загружается или если её нет — монохромная заглушка FluentIcons по виду файла на подложке. Загрузка асинхронная,
/// с отменой: при прокрутке виртуализованного списка элемент переиспользуется для другой записи.
/// </summary>
/// <remarks>
/// Кэш изображений задаёт оболочка один раз при запуске (<see cref="Cache"/>): элемент живёт в шаблонах данных,
/// куда внедрение зависимостей не дотягивается. Без кэша (превью, тесты) показывается только заглушка.
/// </remarks>
public sealed class FileIcon : Border
{
    /// <summary>Свойство <see cref="Entry"/>.</summary>
    public static readonly StyledProperty<FileEntry?> EntryProperty = AvaloniaProperty.Register<FileIcon, FileEntry?>(nameof(Entry));

    /// <summary>Свойство <see cref="Symbol"/>.</summary>
    public static readonly StyledProperty<Symbol> SymbolProperty = AvaloniaProperty.Register<FileIcon, Symbol>(nameof(Symbol), Symbol.Document);

    /// <summary>Свойство <see cref="IconSize"/>.</summary>
    public static readonly StyledProperty<double> IconSizeProperty = AvaloniaProperty.Register<FileIcon, double>(nameof(IconSize), 24);

    /// <summary>Свойство <see cref="PreferThumbnail"/>.</summary>
    public static readonly StyledProperty<bool> PreferThumbnailProperty = AvaloniaProperty.Register<FileIcon, bool>(nameof(PreferThumbnail));

    /// <summary>Свойство <see cref="FallbackBackground"/>.</summary>
    public static readonly StyledProperty<IBrush?> FallbackBackgroundProperty = AvaloniaProperty.Register<FileIcon, IBrush?>(nameof(FallbackBackground));

    /// <summary>Цвет заглушки; наследуется, как у текста.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<FileIcon>();

    private static readonly int[] PixelSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    private readonly Image _image = new() { Stretch = Stretch.Uniform, IsVisible = false };
    private readonly SymbolIcon _fallback = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private int _version;

    /// <summary>
    /// Создаёт элемент с заглушкой.
    /// </summary>
    public FileIcon()
    {
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);
        _image.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        _image.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Child = new Panel { Children = { _fallback, _image } };
        ApplySize();
        ShowFallback();
    }

    /// <summary>Кэш изображений приложения; задаёт оболочка при запуске.</summary>
    public static IIconCache? Cache { get; set; }

    /// <summary>Запись, чью иконку показать.</summary>
    public FileEntry? Entry
    {
        get => GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    /// <summary>Заглушка по виду файла.</summary>
    public Symbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    /// <summary>Размер иконки в единицах интерфейса; пиксели выбираются с учётом масштаба экрана.</summary>
    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>Для изображений и видео показывать миниатюру, а не иконку типа.</summary>
    public bool PreferThumbnail
    {
        get => GetValue(PreferThumbnailProperty);
        set => SetValue(PreferThumbnailProperty, value);
    }

    /// <summary>Подложка под заглушкой; у настоящей иконки подложки нет.</summary>
    public IBrush? FallbackBackground
    {
        get => GetValue(FallbackBackgroundProperty);
        set => SetValue(FallbackBackgroundProperty, value);
    }

    /// <summary>Цвет заглушки.</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>
    /// Ближайший размер растра, который отдаёт сервер, не меньше нужного числа пикселей.
    /// </summary>
    public static int PixelSizeFor(double size, double scaling)
    {
        var pixels = (int)Math.Ceiling(size * Math.Max(scaling, 1));
        return PixelSizes.FirstOrDefault(candidate => candidate >= pixels, PixelSizes[^1]);
    }

    /// <summary>
    /// Брать ли миниатюру для записи: изображения и видео, кроме облачных заглушек (миниатюра скачала бы файл).
    /// </summary>
    public static bool WantsThumbnail(FileEntry entry, bool preferThumbnail)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return preferThumbnail && entry.Kind is FileKind.Image or FileKind.Video && !entry.IsCloudOnly && !entry.IsOffline;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SymbolProperty)
        {
            _fallback.Symbol = Symbol;
        }
        else if (change.Property == IconSizeProperty)
        {
            ApplySize();
            Reload();
        }
        else if (change.Property == EntryProperty || change.Property == PreferThumbnailProperty)
        {
            Reload();
        }
        else if (change.Property == FallbackBackgroundProperty && !_image.IsVisible)
        {
            Background = FallbackBackground;
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Reload();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // Ответ, пришедший после отсоединения, не нужен: версия сменится.
        _version++;
    }

    private void ApplySize()
    {
        _image.Width = IconSize;
        _image.Height = IconSize;
        _fallback.FontSize = Math.Round(IconSize * 5 / 6);
    }

    private void Reload()
    {
        var version = ++_version;

        if (Cache is not { } cache || Entry is not { } entry || VisualRoot is null)
        {
            ShowFallback();
            return;
        }

        var size = PixelSizeFor(IconSize, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        var thumbnail = WantsThumbnail(entry, PreferThumbnail);
        if (thumbnail && cache.TryGet(entry, Math.Max(size, 32), thumbnail: true, out var preview))
        {
            if (preview is not null)
            {
                Show(preview);
                return;
            }

            // Миниатюры заведомо нет — сразу иконка типа.
            thumbnail = false;
        }

        if (!thumbnail && cache.TryGet(entry, size, thumbnail: false, out var icon))
        {
            Show(icon);
            return;
        }

        ShowFallback();
        _ = LoadAsync(cache, entry, size, thumbnail, version);
    }

    private async Task LoadAsync(IIconCache cache, FileEntry entry, int size, bool thumbnail, int version)
    {
        // Миниатюры у сервера от 32 пикселей; если её нет — обычная иконка типа.
        var image = thumbnail ? await cache.GetAsync(entry, Math.Max(size, 32), thumbnail: true) : null;
        image ??= await cache.GetAsync(entry, size, thumbnail: false);
        if (version == _version)
        {
            Show(image);
        }
    }

    private void Show(IImage? image)
    {
        if (image is null)
        {
            ShowFallback();
            return;
        }

        _image.Source = image;
        _image.IsVisible = true;
        _fallback.IsVisible = false;
        Background = null;
    }

    private void ShowFallback()
    {
        _image.Source = null;
        _image.IsVisible = false;
        _fallback.IsVisible = true;
        _fallback.Symbol = Symbol;
        Background = FallbackBackground;
    }

}
