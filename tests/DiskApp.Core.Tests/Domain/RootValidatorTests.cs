using DiskApp.Core.Domain;
using DiskApp.Core.Paths;

namespace DiskApp.Core.Tests.Domain;

/// <summary>
/// Проверка нового корня: пустой и относительный путь — ошибка, дубликат — ошибка, вложенность — предупреждение, подпись по умолчанию.
/// </summary>
public sealed class RootValidatorTests
{
    private static readonly Root Projects = new() { Id = 1, Path = @"D:\Projects", Label = "Projects" };
    private static readonly Root Photos = new() { Id = 2, Path = @"E:\Photos", Label = "Photos" };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyPath_FailsWithPathEmpty(string? path)
    {
        // Act
        var result = RootValidator.Validate(path, [Projects], PathNormalizer.Windows);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(RootValidator.PathEmptyCode);
    }

    [Theory]
    [InlineData(@"Projects\DiskApp")]
    [InlineData("C:")]
    [InlineData(@"C:\Projects\bad|name")]
    public void Validate_RelativeOrInvalidPath_FailsWithPathNotAbsolute(string path)
    {
        // Act
        var result = RootValidator.Validate(path, [Projects], PathNormalizer.Windows);

        // Assert
        result.Error!.Code.ShouldBe(RootValidator.PathNotAbsoluteCode);
    }

    [Fact]
    public void Validate_SameRootDifferentCaseAndSeparators_FailsWithDuplicate()
    {
        // Act
        var result = RootValidator.Validate("d:/projects/", [Projects, Photos], PathNormalizer.Windows);

        // Assert
        result.Error!.Code.ShouldBe(RootValidator.DuplicateCode);
        result.Error.Message.ShouldContain("Projects");
    }

    [Fact]
    public void Validate_NewRootInsideExisting_SucceedsWithWarning()
    {
        // Act
        var result = RootValidator.Validate(@"D:\Projects\DiskApp", [Projects, Photos], PathNormalizer.Windows);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var candidate = result.Value;
        candidate.Path.ShouldBe(@"D:\Projects\DiskApp");
        candidate.Label.ShouldBe("DiskApp");
        candidate.HasWarnings.ShouldBeTrue();
        candidate.Warnings.ShouldHaveSingleItem().ShouldBe(new RootWarning(RootWarningKind.InsideExistingRoot, Projects));
    }

    [Fact]
    public void Validate_NewRootContainingExisting_SucceedsWithWarning()
    {
        // Act
        var result = RootValidator.Validate(@"E:\", [Projects, Photos], PathNormalizer.Windows);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Label.ShouldBe(@"E:\");
        result.Value.Warnings.ShouldHaveSingleItem().ShouldBe(new RootWarning(RootWarningKind.ContainsExistingRoot, Photos));
    }

    [Fact]
    public void Validate_IndependentRoot_SucceedsWithoutWarnings()
    {
        // Act
        var result = RootValidator.Validate(@"\\nas\media\Movies", [Projects, Photos], PathNormalizer.Windows);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Path.ShouldBe(@"\\nas\media\Movies");
        result.Value.Label.ShouldBe("Movies");
        result.Value.HasWarnings.ShouldBeFalse();
    }
}
