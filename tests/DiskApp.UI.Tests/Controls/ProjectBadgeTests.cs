using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;

using DiskApp.UI.Controls;
using DiskApp.UI.Models;

using FluentAvalonia.Styling;

namespace DiskApp.UI.Tests.Controls;

/// <summary>
/// Иконка проекта (PLAN.md, этап 24.2): номер любой длины помещается в квадрат — крупно, мельче, в две-три строки,
/// в крайнем случае с «…»; цвет номера читается на любом цвете палитры; у одного номера всегда один цвет; иконка
/// рисуется в светлой, тёмной теме и при высокой контрастности.
/// </summary>
public sealed class ProjectBadgeTests
{
    // Ширина строки — как у широкого шрифта: 0,62 размера на символ (Segoe UI Semibold — около 0,55).
    private static readonly Func<string, double, double> Measure = static (text, size) => text.Length * size * 0.62;

    private static readonly string[] RenderedNumbers = ["4", "10S-417", "ABCDEFGHIJKLMNOPQRST"];

    [Theory]
    [InlineData("4")]
    [InlineData("17")]
    [InlineData("417")]
    [InlineData("10S-417")]
    [InlineData("2026-ДОГ-0417")]
    [InlineData("Школа на Ленина 12")]
    [InlineData("ABCDEFGHIJKLMNOPQRST")]
    public void Fit_AnyLength_StaysInsideSquare(string number)
    {
        foreach (var size in new[] { 18.0, 36, 72 })
        {
            // Act
            var layout = ProjectBadge.Fit(number, size, Measure);

            // Assert
            var inner = ProjectBadge.InnerSize(size);
            layout.Lines.Count.ShouldBeInRange(1, ProjectBadge.MaxLines);
            layout.Lines.ShouldAllBe(line => Measure(line, layout.FontSize) <= inner + 0.01);
            (layout.Lines.Count * layout.FontSize * ProjectBadge.LineHeight).ShouldBeLessThanOrEqualTo(inner + 0.01);
        }
    }

    [Fact]
    public void Fit_ShortLarge_LongSmallerThenWrapped_BreaksAtDash()
    {
        // Act
        var short3 = ProjectBadge.Fit("417", 72, Measure);
        var dashed = ProjectBadge.Fit("2026-ДОГ-0417", 72, Measure);
        var longest = ProjectBadge.Fit("ABCDEFGHIJKLMNOPQRST", 72, Measure);
        var tiny = ProjectBadge.Fit("ABCDEFGHIJKLMNOPQRST", 18, Measure);

        // Assert: короткий — одной строкой и крупнее длинного; длинный — по «-», целиком; самый длинный в 72 — целиком,
        // в 18 — с «…» (полный номер — в подсказке)
        short3.Lines.ShouldBe(["417"]);
        short3.FontSize.ShouldBeGreaterThan(dashed.FontSize);
        dashed.Lines.Count.ShouldBeGreaterThan(1);
        string.Concat(dashed.Lines).ShouldBe("2026-ДОГ-0417");
        dashed.Lines[0].ShouldEndWith("-");
        string.Concat(longest.Lines).ShouldBe("ABCDEFGHIJKLMNOPQRST");
        tiny.Lines[^1].ShouldEndWith("…");
    }

    [Fact]
    public void Colors_TextReadableOnPalette_SameNumberSameColor_OwnColorWins()
    {
        // Act
        var contrasts = TagPalette.Colors.Select(Color.Parse).Select(static background => ProjectBadge.ContrastRatio(background, ProjectBadge.ForegroundFor(background))).ToList();

        // Assert: крупный полужирный номер — не ниже 3:1 по WCAG на каждом цвете палитры
        contrasts.ShouldAllBe(ratio => ratio >= 3.0);
        ProjectBadge.BackgroundFor(null, "417").ShouldBe(ProjectBadge.BackgroundFor(null, " 417 "));
        ProjectBadge.BackgroundFor("#0078D4", "417").ShouldBe(Color.Parse("#0078D4"));
        ProjectBadge.ForegroundFor(Color.Parse("#FFB900")).ShouldNotBe(Colors.White);
        ProjectBadge.ForegroundFor(Color.Parse("#0078D4")).ShouldBe(Colors.White);
    }

    [Fact]
    public Task Render_LightDarkHighContrast_DrawsWithoutErrors() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange
        var badges = RenderedNumbers.Select(static number => new ProjectBadge { Number = number, BadgeSize = 36 }).ToList();
        var panel = new StackPanel();
        foreach (var badge in badges)
        {
            panel.Children.Add(badge);
        }

        var window = new Window { Width = 200, Height = 200, Content = panel };
        window.Show();

        // Act
        var sizes = new List<double>();
        foreach (var variant in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark, FluentAvaloniaTheme.HighContrastTheme })
        {
            window.RequestedThemeVariant = variant;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            sizes.Add(badges[2].Bounds.Width);
        }

        // Assert
        sizes.ShouldAllBe(width => width == 36);
        window.Close();
        return Task.CompletedTask;
    });
}
