using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Core.Search;

namespace DiskApp.Core.Tests.Search;

/// <summary>
/// План запроса: раскладка термов по стратегиям (ADR-0005), типизированные фильтры, папки и корни, подпись условий.
/// </summary>
public sealed class SearchPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Root Projects = new() { Id = 1, Path = @"D:\Projects", Label = "Проекты" };
    private static readonly Root Photos = new() { Id = 2, Path = @"E:\Photos", Label = "Photos" };

    [Fact]
    public void Build_Terms_SplitByLengthAndNegation()
    {
        // Act
        var plan = Plan("Отчёт ab x -черновик \"годовой план\"");

        // Assert
        plan.FtsTerms.ShouldBe(["отчет", "годовой план"]);
        plan.ShortTerms.ShouldBe(["ab", "x"], "короткие термы — по убыванию длины, первый станет префиксом");
        plan.ExcludedTerms.ShouldBe(["черновик"]);
        plan.Source.ShouldBe(CandidateSource.FullText);
        plan.PositiveTerms.ShouldBe(["отчет", "годовой план", "ab", "x"]);
    }

    [Theory]
    [InlineData("re", CandidateSource.NamePrefix)]
    [InlineData("ext:pdf", CandidateSource.Filters)]
    [InlineData("report", CandidateSource.FullText)]
    public void Source_DependsOnTerms(string text, CandidateSource expected)
    {
        // Act & Assert
        Plan(text).Source.ShouldBe(expected);
    }

    [Fact]
    public void Build_Empty_IsEmptyPlan()
    {
        // Act & Assert
        Plan(string.Empty).IsEmpty.ShouldBeTrue();
        Plan("ext:").IsEmpty.ShouldBeTrue();
        Plan("-draft").IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void Build_Filters_BecomeTypedConditions()
    {
        // Act
        var plan = Plan("ext:pdf,docx -ext:tmp type:картинки -type:video size:>1mb modified:week is:file -is:hidden tag:Работа fav:yes");

        // Assert
        plan.IncludeExtensions.ShouldBe(["pdf", "docx"]);
        plan.ExcludeExtensions.ShouldBe(["tmp"]);
        plan.IncludeKinds.ShouldBe([FileKind.Image]);
        plan.ExcludeKinds.ShouldBe([FileKind.Video]);
        plan.IncludeSizes.ShouldBe([new SizeRange(1024 * 1024 + 1, long.MaxValue)]);
        plan.IncludeModified.ShouldBe([new DateRange(Now.AddDays(-7), null)]);
        plan.IncludeAttributes.ShouldBe([AttributeFilter.File]);
        plan.ExcludeAttributes.ShouldBe([AttributeFilter.Hidden]);
        plan.IncludeTags.ShouldBe(["работа"]);
        plan.Favorite.ShouldBe(true);
        plan.HasFilters.ShouldBeTrue();
    }

    [Fact]
    public void Build_NegatedFavorite_ExcludesFavorites()
    {
        // Act & Assert
        Plan("-fav:yes").Favorite.ShouldBe(false);
        Plan("fav:no").Favorite.ShouldBe(false);
    }

    [Fact]
    public void Build_InByName_MatchesRootLabelAndFolderName()
    {
        // Act
        var plan = Plan("in:проекты");

        // Assert
        plan.IncludeFolders.ShouldBe([new FolderScope(1, string.Empty, null), new FolderScope(null, null, "проекты")]);
    }

    [Fact]
    public void Build_InByAbsolutePath_ResolvesRootAndRelativePath()
    {
        // Act
        var inside = Plan(@"in:D:\Projects\DiskApp\src");
        var outside = Plan(@"in:C:\Windows");

        // Assert
        inside.IncludeFolders.ShouldBe([new FolderScope(1, @"DiskApp\src", null)]);
        outside.IncludeRoots.ShouldBe([-1L]);
        outside.Warnings.ShouldHaveSingleItem().ShouldContain("не входит");
    }

    [Theory]
    [InlineData("root:photos", 2L)]
    [InlineData("root:Проекты", 1L)]
    [InlineData("root:1", 1L)]
    [InlineData(@"root:E:\Photos", 2L)]
    public void Build_Root_MatchesByLabelNameIdOrPath(string text, long expected)
    {
        // Act & Assert
        Plan(text).IncludeRoots.ShouldBe([expected]);
    }

    [Fact]
    public void Build_UnknownRoot_EmptyResultWithWarning()
    {
        // Act
        var plan = Plan("root:Nowhere");

        // Assert
        plan.IncludeRoots.ShouldBe([-1L]);
        plan.Warnings.ShouldHaveSingleItem().ShouldContain("Nowhere");
    }

    [Fact]
    public void ConditionsSignature_SameConditionsDifferentTerms_Equal()
    {
        // Act & Assert
        Plan("rep ext:pdf").ConditionsSignature.ShouldBe(Plan("report ext:pdf").ConditionsSignature);
        Plan("rep ext:pdf").ConditionsSignature.ShouldNotBe(Plan("rep ext:doc").ConditionsSignature);
        Plan("rep -x").ConditionsSignature.ShouldNotBe(Plan("rep").ConditionsSignature);
    }

    [Fact]
    public void Build_ParserWarnings_Carried()
    {
        // Act & Assert
        Plan("size:abc").Warnings.ShouldHaveSingleItem();
    }

    private static SearchPlan Plan(string text)
        => SearchPlanner.Build(QueryParser.Parse(text), [Projects, Photos], PathNormalizer.Windows, Now, TimeZoneInfo.Utc);
}
