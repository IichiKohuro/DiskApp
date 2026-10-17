using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using DiskApp.UI.Controls;
using DiskApp.UI.Models;
using DiskApp.UI.ViewModels;

using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;

using FluentIcons.Avalonia;

using Symbol = FluentIcons.Common.Symbol;

namespace DiskApp.UI.Tests.Controls;

/// <summary>
/// Цвета иконок (замечания пользователя, 2026-10-17): папки — жёлтые, а не акцентные; у каждого раздела навигации
/// свой цвет в светлой и тёмной теме, при высокой контрастности — системный.
/// </summary>
public sealed class IconColorsTests
{
    private static readonly string[] NavigationPages =
    [
        PageKeys.Home, PageKeys.Search, PageKeys.Browse, PageKeys.Collections, PageKeys.Tags, PageKeys.Inbox,
        PageKeys.Projects, PageKeys.Notes, PageKeys.Operations, PageKeys.Settings,
    ];

    [Theory]
    [InlineData(Symbol.Folder, true)]
    [InlineData(Symbol.FolderOpen, true)]
    [InlineData(Symbol.FolderAdd, true)]
    [InlineData(Symbol.FolderZip, false)]
    [InlineData(Symbol.Document, false)]
    [InlineData(Symbol.HardDrive, false)]
    public void IsFolder_FolderSymbolsButNotArchives(Symbol symbol, bool expected)
        => FileKindSymbols.IsFolder(symbol).ShouldBe(expected);

    [Fact]
    public Task NavIconBrushes_EveryPageHasItsOwnColorInLightAndDark() => HeadlessTest.RunAsync(static () =>
    {
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            // Act
            var colors = NavigationPages.Select(page => BrushColor($"NavIconBrush.{page}", variant)).ToList();

            // Assert
            colors.Distinct().Count().ShouldBe(NavigationPages.Length, $"цвета разделов в теме {variant} повторяются");
            BrushColor($"NavIconBrush.{PageKeys.Browse}", variant).ShouldBe(BrushColor("FolderIconBrush", variant), "«Обзор» — цветом папки");
        }

        Application.Current!.TryGetResource("FolderIconBrush", FluentAvaloniaTheme.HighContrastTheme, out var highContrast).ShouldBeTrue();
        highContrast.ShouldBeOfType<SolidColorBrush>();
        return Task.CompletedTask;
    });

    [Fact]
    public Task NavIconColor_FollowsThemeAndLeavesHighContrastToSystem() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange
        var icon = new FASymbolIconSource { Symbol = FASymbol.Folder };
        var item = new FANavigationViewItem { Content = "Обзор", IconSource = icon };
        NavIconColor.SetResourceKey(item, $"NavIconBrush.{PageKeys.Browse}");
        var window = new Window { Width = 300, Height = 100, Content = item, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Act
        var dark = (icon.Foreground as SolidColorBrush)?.Color;
        window.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();
        var light = (icon.Foreground as SolidColorBrush)?.Color;
        window.RequestedThemeVariant = FluentAvaloniaTheme.HighContrastTheme;
        Dispatcher.UIThread.RunJobs();
        var highContrast = icon.Foreground;

        // Assert
        dark.ShouldBe(BrushColor($"NavIconBrush.{PageKeys.Browse}", ThemeVariant.Dark));
        light.ShouldBe(BrushColor($"NavIconBrush.{PageKeys.Browse}", ThemeVariant.Light));
        highContrast.ShouldBeNull();
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task FileIcon_FolderFallback_IsFolderColorOtherKindsKeepForeground() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange
        var folder = new FileIcon { Symbol = Symbol.Folder, Foreground = Brushes.Red };
        var document = new FileIcon { Symbol = Symbol.DocumentText, Foreground = Brushes.Red };
        var window = new Window
        {
            Width = 200,
            Height = 100,
            RequestedThemeVariant = ThemeVariant.Dark,
            Content = new StackPanel { Children = { folder, document } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Act
        var folderColor = (Fallback(folder).Foreground as SolidColorBrush)?.Color;
        var documentColor = (Fallback(document).Foreground as ISolidColorBrush)?.Color;
        folder.Symbol = Symbol.DocumentText;
        Dispatcher.UIThread.RunJobs();
        var changedColor = (Fallback(folder).Foreground as ISolidColorBrush)?.Color;

        // Assert
        folderColor.ShouldBe(BrushColor("FolderIconBrush", ThemeVariant.Dark));
        documentColor.ShouldBe(Colors.Red);
        changedColor.ShouldBe(Colors.Red, "без папки заглушка снова цветом Foreground");
        window.Close();
        return Task.CompletedTask;
    });

    private static SymbolIcon Fallback(FileIcon icon) => icon.GetVisualDescendants().OfType<SymbolIcon>().First();

    private static Color BrushColor(string key, ThemeVariant variant)
    {
        Application.Current!.TryGetResource(key, variant, out var value).ShouldBeTrue($"нет ресурса {key} в теме {variant}");
        return value.ShouldBeOfType<SolidColorBrush>().Color;
    }
}
