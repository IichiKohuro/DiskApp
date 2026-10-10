using Avalonia.Controls;
using Avalonia.Platform;

using DiskApp.Desktop.Platform.Windows;

using FluentAvalonia.UI.Windowing;

using Serilog;

namespace DiskApp.Desktop.Windows;

/// <summary>
/// Главное окно десктопа: стиль Windows 11 (Mica, собственный заголовок), закрытие прячет окно в трей.
/// </summary>
public partial class MainWindow : FAAppWindow
{
    private const string IconUri = "avares://DiskApp/Assets/DiskApp.ico";

    /// <summary>
    /// Создаёт окно: расширяет содержимое в область заголовка, задаёт иконку и включает Mica.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.Height = 32;

        if (LoadIcon() is { } icon)
        {
            ((Window)this).Icon = icon;
        }

        Backdrop.ApplyMica(this);
    }

    /// <summary>
    /// Разрешить настоящее закрытие. Пока <c>false</c>, закрытие окна лишь прячет его (выход — из трея).
    /// </summary>
    public bool AllowClose { get; set; }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (!AllowClose && !e.Cancel)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(IconUri));
            return new WindowIcon(stream);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Иконка окна не загружена из {Uri}", IconUri);
            return null;
        }
    }
}
