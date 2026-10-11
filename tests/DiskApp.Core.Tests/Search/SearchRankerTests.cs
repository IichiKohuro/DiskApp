using DiskApp.Core.Domain;
using DiskApp.Core.Search;

namespace DiskApp.Core.Tests.Search;

/// <summary>
/// Ранжирование: порядок типов совпадения, frecency и избранное, глубина, свежесть, пресеты, подсветка, нечёткие
/// совпадения и объяснение оценки.
/// </summary>
public sealed class SearchRankerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rank_MatchKinds_OrderedExactPrefixWordStartSubstring()
    {
        // Arrange
        var ranker = Ranker();
        string[] names = ["report.docx", "report-final.docx", "Quarterly report.docx", "myreports.docx"];

        // Act
        var ranked = names.Select(name => ranker.Rank(Candidate(name), ["report"], @"D:\x\" + name, false)).ToList();

        // Assert
        ranked.Select(static item => item.BestMatch).ShouldBe([MatchKind.Exact, MatchKind.Prefix, MatchKind.WordStart, MatchKind.Substring]);
        ranked.Select(static item => item.Score).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public void Rank_DecomposedName_HighlightIncludesCombiningMark()
    {
        // Act: «é» в имени записано как «e» + U+0301, запрос — в составной форме
        var ranked = Ranker().Rank(Candidate("Cafe\u0301 menu.pdf"), [SearchText.Fold("caf\u00E9")], @"D:\x\" + "Cafe\u0301 menu.pdf", false);

        // Assert
        ranked.BestMatch.ShouldBe(MatchKind.Prefix);
        ranked.NameHighlights.ShouldBe([new TextRange(0, 5)]);
    }

    [Fact]
    public void Rank_CamelCaseBoundary_CountsAsWordStart()
    {
        // Act
        var ranked = Ranker().Rank(Candidate("QuarterlyReport.xlsx"), ["report"], @"D:\QuarterlyReport.xlsx", false);

        // Assert
        ranked.BestMatch.ShouldBe(MatchKind.WordStart);
        ranked.NameHighlights.ShouldBe([new TextRange(9, 6)]);
    }

    [Fact]
    public void Rank_FrecencyAndFavorite_LiftEqualMatches()
    {
        // Arrange
        var ranker = Ranker();
        var plain = Candidate("budget.xlsx", id: 1);
        var used = Candidate("budget.xlsx", id: 2, opens: 20, lastOpened: Now.AddDays(-1));
        var favorite = Candidate("budget.xlsx", id: 3, favorite: true);

        // Act
        var scores = new[] { plain, used, favorite }.Select(candidate => ranker.Rank(candidate, ["budget"], @"D:\budget.xlsx", false).Score).ToArray();

        // Assert
        scores[1].ShouldBeGreaterThan(scores[0]);
        scores[2].ShouldBeGreaterThan(scores[0]);
    }

    [Fact]
    public void Rank_DeepPath_PenalizedAgainstShallow()
    {
        // Arrange
        var ranker = Ranker();
        var shallow = Candidate("notes.txt", relPath: "notes.txt");
        var deep = Candidate("notes.txt", relPath: @"a\b\c\d\e\f\g\h\notes.txt");

        // Act & Assert
        ranker.Rank(shallow, ["notes"], @"D:\notes.txt", false).Score
            .ShouldBeGreaterThan(ranker.Rank(deep, ["notes"], @"D:\a\b\c\d\e\f\g\h\notes.txt", false).Score);
    }

    [Fact]
    public void Rank_RecentPreset_PrefersRecentlyModified()
    {
        // Arrange
        var fresh = Candidate("plan.docx", modified: Now.AddDays(-1));
        var old = Candidate("plan-v2.docx", modified: Now.AddYears(-3));
        var byName = new SearchRanker(RankingWeights.For(RankingPreset.ByName), Now, TimeSpan.FromDays(30));
        var recent = new SearchRanker(RankingWeights.For(RankingPreset.Recent), Now, TimeSpan.FromDays(30));

        // Act
        var byNameGap = byName.Rank(fresh, ["plan"], "p", false).Score - byName.Rank(old, ["plan"], "p", false).Score;
        var recentGap = recent.Rank(fresh, ["plan"], "p", false).Score - recent.Rank(old, ["plan"], "p", false).Score;

        // Assert
        recentGap.ShouldBeGreaterThan(byNameGap);
    }

    [Fact]
    public void Rank_SeveralTerms_HighlightsMergedAndSorted()
    {
        // Act
        var ranked = Ranker().Rank(Candidate("Annual Report 2026.pdf"), ["2026", "report", "repo"], @"D:\Annual Report 2026.pdf", false);

        // Assert
        ranked.NameHighlights.ShouldBe([new TextRange(7, 6), new TextRange(14, 4)]);
    }

    [Fact]
    public void Rank_TermInFolder_HighlightsPath()
    {
        // Act
        var ranked = Ranker().Rank(Candidate("main.cs", relPath: @"DiskApp\src\main.cs"), ["diskapp", "main"], @"D:\Projects\DiskApp\src\main.cs", false);

        // Assert
        ranked.PathHighlights.ShouldBe([new TextRange(12, 7)]);
        ranked.NameHighlights.ShouldBe([new TextRange(0, 4)]);
    }

    [Fact]
    public void Rank_Typo_FuzzyMatchWithLowerScore()
    {
        // Arrange
        var ranker = Ranker();

        // Act
        var typo = ranker.Rank(Candidate("report.docx"), ["reprot"], "r", false);
        var exact = ranker.Rank(Candidate("report.docx"), ["report"], "r", false);

        // Assert
        typo.BestMatch.ShouldBe(MatchKind.Fuzzy);
        typo.Score.ShouldBeLessThan(exact.Score);
        typo.Score.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Rank_IncludeReason_DescribesComponents()
    {
        // Act
        var ranked = Ranker().Rank(Candidate("report.docx", opens: 3, lastOpened: Now, favorite: true), ["report"], "r", true);

        // Assert
        ranked.Reason.ShouldNotBeNull();
        ranked.Reason.ShouldContain("точное имя");
        ranked.Reason.ShouldContain("frecency");
        ranked.Reason.ShouldContain("избранное");
    }

    [Fact]
    public void Rank_NoTerms_UsesFrecencyAndRecencyOnly()
    {
        // Act
        var ranked = Ranker().Rank(Candidate("any.txt", opens: 5, lastOpened: Now), [], "a", false);

        // Assert
        ranked.BestMatch.ShouldBe(MatchKind.None);
        ranked.NameHighlights.ShouldBeEmpty();
        ranked.Score.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("name", RankingPreset.ByName)]
    [InlineData("recent", RankingPreset.Recent)]
    [InlineData("balanced", RankingPreset.Balanced)]
    [InlineData(null, RankingPreset.Balanced)]
    [InlineData("???", RankingPreset.Balanced)]
    public void ParsePreset_Names(string? name, RankingPreset expected)
    {
        // Act & Assert
        RankingWeights.ParsePreset(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("rpt", "report.docx", true)]
    [InlineData("qr", "QuarterlyReport.xlsx", true)]
    [InlineData("xyz", "report.docx", false)]
    public void FuzzyMatcher_Subsequence_ScoresWordStartsHigher(string pattern, string text, bool matches)
    {
        // Act
        var match = FuzzyMatcher.Match(pattern, SearchText.Fold(text), text);

        // Assert: непохожий образец получает слабую оценку, которую отсекает порог сходства сервиса
        (match.Score >= 0.5).ShouldBe(matches);
        if (matches)
        {
            match.Positions.Count.ShouldBe(pattern.Length);
        }
    }

    [Fact]
    public void FuzzyMatcher_WordStartMatch_BeatsScatteredMatch()
    {
        // Act
        var boundary = FuzzyMatcher.Match("qr", "quarterlyreport", "QuarterlyReport");
        var scattered = FuzzyMatcher.Match("qr", "aqbcrd", "aqbcrd");

        // Assert
        boundary.Score.ShouldBeGreaterThan(scattered.Score);
    }

    [Fact]
    public void FuzzyMatcher_Trigrams_Distinct()
    {
        // Act & Assert
        FuzzyMatcher.Trigrams("aaaa").ShouldBe(["aaa"]);
        FuzzyMatcher.Trigrams("ab").ShouldBeEmpty();
        FuzzyMatcher.Trigrams("report").ShouldBe(["rep", "epo", "por", "ort"]);
    }

    private static SearchRanker Ranker() => new(RankingWeights.For(RankingPreset.Balanced), Now, TimeSpan.FromDays(30));

    private static SearchCandidate Candidate(
        string name,
        long id = 1,
        string? relPath = null,
        int opens = 0,
        DateTimeOffset? lastOpened = null,
        bool favorite = false,
        DateTimeOffset? modified = null)
    {
        var entry = new FileEntry
        {
            Id = id,
            RootId = 1,
            Name = name,
            Extension = FileEntry.ExtensionOf(name),
            RelPath = relPath ?? name,
            ModifiedUtc = modified ?? Now.AddDays(-90),
            CreatedUtc = Now.AddYears(-1),
        };
        return new SearchCandidate(entry, opens, lastOpened, favorite);
    }
}
