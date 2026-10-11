using DiskApp.UI.Services;

namespace DiskApp.UI.Tests.Services;

/// <summary>
/// Разбор аргументов командной строки оболочки.
/// </summary>
public sealed class StartupArgumentsTests
{
    [Fact]
    public void Parse_NoArguments_ReturnsDefaults()
    {
        // Act
        var parsed = StartupArguments.Parse([]);

        // Assert
        parsed.ShouldBe(StartupArguments.Default);
        parsed.ShowSearch.ShouldBeFalse();
    }

    [Fact]
    public void Parse_Flags_AreCaseInsensitiveAndIndependent()
    {
        // Act
        var parsed = StartupArguments.Parse(["--Minimized", "--SETTINGS", "--portable", "--unknown"]);

        // Assert
        parsed.Minimized.ShouldBeTrue();
        parsed.OpenSettings.ShouldBeTrue();
        parsed.Portable.ShouldBeTrue();
        parsed.Show.ShouldBeFalse();
        parsed.SearchQuery.ShouldBeNull();
        parsed.Exit.ShouldBeFalse();
    }

    [Fact]
    public void Parse_SafeMode_SetsFlag()
    {
        // Act
        var parsed = StartupArguments.Parse(["--safe-mode", "--show"]);

        // Assert
        parsed.SafeMode.ShouldBeTrue();
        parsed.Show.ShouldBeTrue();
        StartupArguments.Parse([]).SafeMode.ShouldBeFalse();
    }

    [Fact]
    public void Parse_Exit_SetsExitFlag()
    {
        // Act
        var parsed = StartupArguments.Parse(["--EXIT"]);

        // Assert
        parsed.Exit.ShouldBeTrue();
        parsed.ShowSearch.ShouldBeFalse();
    }

    [Theory]
    [InlineData(new[] { "--search", "quarterly report" }, "quarterly report")]
    [InlineData(new[] { "--search=invoice" }, "invoice")]
    [InlineData(new[] { "--search" }, "")]
    [InlineData(new[] { "--search", "--show" }, "")]
    public void Parse_Search_TakesInlineOrNextValueOrEmpty(string[] args, string expectedQuery)
    {
        // Act
        var parsed = StartupArguments.Parse(args);

        // Assert
        parsed.ShowSearch.ShouldBeTrue();
        parsed.SearchQuery.ShouldBe(expectedQuery);
    }

    [Fact]
    public void Parse_Add_CollectsPathsUntilNextFlag()
    {
        // Act
        var parsed = StartupArguments.Parse(["--add", @"D:\Работа", @"E:\Фото", "--show", "--add=F:\\Архив"]);

        // Assert
        parsed.AddPaths.ShouldBe([@"D:\Работа", @"E:\Фото", @"F:\Архив"]);
        parsed.Show.ShouldBeTrue();
        StartupArguments.Parse(["--show"]).AddPaths.ShouldBeNull();
    }

    [Theory]
    [InlineData("diskapp://search?q=%D0%BE%D1%82%D1%87%D0%B5%D1%82+ext%3Apdf", "отчет ext:pdf")]
    [InlineData("diskapp://search", "")]
    [InlineData("DISKAPP://open?path=D%3A%5CWork%5Creport.pdf", "report.pdf")]
    public void Parse_AppUri_OpensQuickSearch(string uri, string expectedQuery)
    {
        // Act
        var parsed = StartupArguments.Parse([uri]);

        // Assert
        parsed.ShowSearch.ShouldBeTrue();
        parsed.SearchQuery.ShouldBe(expectedQuery);
    }

    [Fact]
    public void Parse_UnknownAppUri_IsIgnored()
        => StartupArguments.Parse(["diskapp://unknown?x=1"]).ShouldBe(StartupArguments.Default);

    [Fact]
    public void Parse_SearchFollowedByFlag_DoesNotSwallowFlag()
    {
        // Act
        var parsed = StartupArguments.Parse(["--search", "--show"]);

        // Assert
        parsed.Show.ShouldBeTrue();
        parsed.SearchQuery.ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData(new[] { "--page", "Browse" }, "browse")]
    [InlineData(new[] { "--page=operations" }, "operations")]
    [InlineData(new[] { "--page", "--show" }, null)]
    public void Parse_Page_OpensSectionByKey(string[] args, string? expected)
    {
        // Act
        var parsed = StartupArguments.Parse(args);

        // Assert
        parsed.Page.ShouldBe(expected);
    }
}
