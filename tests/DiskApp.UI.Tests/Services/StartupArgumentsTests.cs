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
    public void Parse_SearchFollowedByFlag_DoesNotSwallowFlag()
    {
        // Act
        var parsed = StartupArguments.Parse(["--search", "--show"]);

        // Assert
        parsed.Show.ShouldBeTrue();
        parsed.SearchQuery.ShouldBe(string.Empty);
    }
}
