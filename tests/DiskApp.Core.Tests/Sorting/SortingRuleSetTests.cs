using DiskApp.Core.Paths;
using DiskApp.Core.Settings;
using DiskApp.Core.Sorting;

namespace DiskApp.Core.Tests.Sorting;

/// <summary>
/// Подбор рабочей папки по шаблонам имён (PLAN.md, этап 17.5): маски glob и «где угодно в имени», условия накопителя и
/// расширения, выключенный шаблон, подпапка, конфликт двух папок, папка из накопителя, файл, уже лежащий в назначении.
/// </summary>
public sealed class SortingRuleSetTests
{
    private const string Downloads = @"D:\Загрузки";
    private const string Skype = @"D:\Skype";

    [Theory]
    [InlineData("417_*", "417_ТЗ v3.docx", true)]
    [InlineData("417_*", "ТЗ 417.docx", false)]
    [InlineData("*TCI2*", "отчёт tci2 март.pdf", true)]
    [InlineData("417", "Счёт по 417 за май.pdf", true)]
    [InlineData("счёт*.pdf", "Счёт 12.PDF", true)]
    [InlineData("счёт*.pdf", "Счёт 12.docx", false)]
    [InlineData("акт_??.pdf", "акт_07.pdf", true)]
    public void Match_Pattern_ComparesNameIgnoringCase(string pattern, string name, bool expected)
    {
        // Arrange
        var rules = Rules(Folder(@"D:\Работа\417", new SortingRule { Pattern = pattern }));

        // Act
        var verdict = rules.Match(Path.Combine(Downloads, name), name, isFolder: false, Downloads);

        // Assert
        (verdict.Decision == SortingDecision.Auto).ShouldBe(expected);
    }

    [Fact]
    public void Match_InboxAndExtensionConditions_LimitTheRule()
    {
        // Arrange: только из Skype и только pdf/docx (расширения в любом виде)
        var rules = Rules(Folder(@"D:\Работа\417", new SortingRule { Pattern = "417", Inbox = Skype + @"\", Extensions = [".PDF", "*.docx"] }));

        // Act
        var fromSkype = rules.Match(Skype + @"\417.pdf", "417.pdf", false, Skype);
        var fromDownloads = rules.Match(Downloads + @"\417.pdf", "417.pdf", false, Downloads);
        var image = rules.Match(Skype + @"\417.jpg", "417.jpg", false, Skype);

        // Assert
        fromSkype.Decision.ShouldBe(SortingDecision.Auto);
        fromDownloads.Decision.ShouldBe(SortingDecision.None);
        image.Decision.ShouldBe(SortingDecision.None);
    }

    [Fact]
    public void Match_DisabledRule_DoesNothing()
    {
        // Arrange
        var rules = Rules(Folder(@"D:\Работа\417", new SortingRule { Pattern = "417_*", Enabled = false }));

        // Act
        var verdict = rules.Match(Downloads + @"\417_ТЗ.pdf", "417_ТЗ.pdf", false, Downloads);

        // Assert
        rules.IsEmpty.ShouldBeTrue();
        verdict.ShouldBeSameAs(SortingVerdict.None);
    }

    [Fact]
    public void Match_TwoFolders_IsChoiceEvenWhenOneOnlySuggests()
    {
        // Arrange
        var rules = Rules(
            Folder(@"D:\Работа\417", new SortingRule { Pattern = "417_*" }),
            Folder(@"D:\Работа\TCI2", new SortingRule { Pattern = "TCI2", Mode = SortingRuleMode.Suggest }));

        // Act
        var verdict = rules.Match(Downloads + @"\417_TCI2.pdf", "417_TCI2.pdf", false, Downloads);

        // Assert
        verdict.Decision.ShouldBe(SortingDecision.Choice);
        verdict.Matches.Select(static match => match.Destination).ShouldBe([@"D:\Работа\417", @"D:\Работа\TCI2"]);
    }

    [Fact]
    public void Match_SameFolderTwoRules_AutoRuleIsTheReason()
    {
        // Arrange
        var rules = Rules(Folder(@"D:\Работа\417", new SortingRule { Pattern = "*.pdf", Mode = SortingRuleMode.Suggest }, new SortingRule { Pattern = "417_*" }));

        // Act
        var verdict = rules.Match(Downloads + @"\417_акт.pdf", "417_акт.pdf", false, Downloads);

        // Assert
        verdict.Decision.ShouldBe(SortingDecision.Auto);
        verdict.Matches[0].Rule.Pattern.ShouldBe("417_*");
    }

    [Fact]
    public void Match_SubfolderRule_PointsInsideAndDiffersFromFolderItself()
    {
        // Arrange
        var rules = Rules(Folder(@"D:\Работа\417", new SortingRule { Pattern = "акт*", Subfolder = "/Акты/2026/" }, new SortingRule { Pattern = "417" }));

        // Act
        var act = rules.Match(Downloads + @"\акт 07.pdf", "акт 07.pdf", false, Downloads);
        var both = rules.Match(Downloads + @"\акт 417.pdf", "акт 417.pdf", false, Downloads);

        // Assert: «акт 417» подходит и к подпапке, и к самой папке — это два разных места
        act.Matches.ShouldHaveSingleItem().Destination.ShouldBe(@"D:\Работа\417\Акты\2026");
        both.Decision.ShouldBe(SortingDecision.Choice);
    }

    [Fact]
    public void Match_FolderItem_IsOnlySuggested()
    {
        // Arrange
        var rules = Rules(Folder(@"D:\Работа\417", new SortingRule { Pattern = "417_*" }));

        // Act
        var verdict = rules.Match(Downloads + @"\417_исходники", "417_исходники", isFolder: true, Downloads);

        // Assert
        verdict.Decision.ShouldBe(SortingDecision.Suggest);
    }

    [Fact]
    public void Match_FileAlreadyInsideDestination_IsLeftAlone()
    {
        // Arrange: рабочая папка лежит внутри накопителя с подпапками
        var rules = Rules(Folder(Downloads + @"\417", new SortingRule { Pattern = "417_*" }));

        // Act
        var verdict = rules.Match(Downloads + @"\417\417_ТЗ.pdf", "417_ТЗ.pdf", false, Downloads);

        // Assert
        verdict.Decision.ShouldBe(SortingDecision.None);
    }

    [Theory]
    [InlineData("417", "*417*")]
    [InlineData(" 417_* ", "417_*")]
    [InlineData("акт_?", "акт_?")]
    public void ToGlob_WithoutWildcards_MatchesAnywhere(string pattern, string expected)
        => SortingRuleSet.ToGlob(pattern).ShouldBe(expected);

    private static SortingFolder Folder(string path, params SortingRule[] rules) => new() { Path = path, Rules = rules };

    private static SortingRuleSet Rules(params SortingFolder[] folders) => new(folders, PathNormalizer.Windows);
}
