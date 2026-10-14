using DiskApp.Core.Domain;
using DiskApp.Core.Paths;

namespace DiskApp.Core.Tests.Domain;

/// <summary>
/// Правило вложенных корней (ADR-0015): владелец пути — самый глубокий корень, корни внутри папки, относительные пути вложенных.
/// </summary>
public sealed class RootNestingTests
{
    private static readonly Root Projects = new() { Id = 1, Path = @"D:\Projects", Label = "Projects" };
    private static readonly Root Tci2 = new() { Id = 2, Path = @"D:\Projects\10S-417\TCI2", Label = "TCI2" };
    private static readonly Root Order = new() { Id = 3, Path = @"D:\Projects\10S-417", Label = "10S-417" };
    private static readonly Root Photos = new() { Id = 4, Path = @"E:\Photos", Label = "Photos" };
    private static readonly Root[] All = [Projects, Tci2, Order, Photos];

    [Theory]
    [InlineData(@"D:\Projects\10S-417\TCI2\yml\a.yml", 2L)]
    [InlineData(@"D:\Projects\10S-417\TCI2", 2L)]
    [InlineData(@"D:\Projects\10S-417\TCI2 docs\b.txt", 3L)]
    [InlineData(@"D:\Projects\10S-417", 3L)]
    [InlineData(@"D:\Projects\readme.md", 1L)]
    [InlineData(@"d:\projects\10s-417\tci2\x", 2L)]
    public void OwnerOf_PathInsideNestedRoots_PicksDeepest(string path, long expected)
    {
        // Act & Assert
        RootNesting.OwnerOf(path, All, PathNormalizer.Windows)!.Id.ShouldBe(expected);
    }

    [Fact]
    public void OwnerOf_PathOutsideRoots_ReturnsNull()
    {
        // Act & Assert
        RootNesting.OwnerOf(@"C:\Windows", All, PathNormalizer.Windows).ShouldBeNull();
        RootNesting.OwnerOf(@"D:\Projects2\x", All, PathNormalizer.Windows).ShouldBeNull();
    }

    [Fact]
    public void NestedIn_Folder_ReturnsRootsStrictlyInside()
    {
        // Act & Assert
        RootNesting.NestedIn(@"D:\Projects", All, PathNormalizer.Windows).Select(static root => root.Id).ShouldBe([2L, 3L]);
        RootNesting.NestedIn(@"D:\Projects\10S-417", All, PathNormalizer.Windows).Select(static root => root.Id).ShouldBe([2L]);
        RootNesting.NestedIn(@"D:\Projects\10S-417\TCI2", All, PathNormalizer.Windows).ShouldBeEmpty();
        RootNesting.NestedIn(@"E:\", All, PathNormalizer.Windows).Select(static root => root.Id).ShouldBe([4L]);
    }

    [Fact]
    public void NestedRelPaths_OuterRoot_ListsRelativePathsOfInnerRoots()
    {
        // Act & Assert
        RootNesting.NestedRelPaths(Projects, All, PathNormalizer.Windows).ShouldBe([@"10S-417\TCI2", "10S-417"]);
        RootNesting.NestedRelPaths(Order, All, PathNormalizer.Windows).ShouldBe(["TCI2"]);
        RootNesting.NestedRelPaths(Tci2, All, PathNormalizer.Windows).ShouldBeEmpty();
        RootNesting.NestedRelPaths(Photos, All, PathNormalizer.Windows).ShouldBeEmpty();
    }
}
