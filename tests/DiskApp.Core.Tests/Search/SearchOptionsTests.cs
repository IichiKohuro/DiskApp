using DiskApp.Core.Search;
using DiskApp.Core.Settings;

namespace DiskApp.Core.Tests.Search;

/// <summary>
/// Параметры поиска из настроек: лимит кандидатов в пределах бюджета, пресет по имени, значения по умолчанию.
/// </summary>
public sealed class SearchOptionsTests
{
    [Fact]
    public void FromSettings_Defaults_MatchMeasuredConfiguration()
    {
        // Act
        var options = SearchOptions.FromSettings(new SearchSettings());

        // Assert
        options.CandidateLimit.ShouldBe(2_000);
        options.HistoryLimit.ShouldBe(100);
        options.FrecencyHalfLife.ShouldBe(TimeSpan.FromDays(30));
        options.Preset.ShouldBe(RankingPreset.Balanced);
    }

    [Theory]
    [InlineData(50_000, 5_000)]
    [InlineData(10, 100)]
    [InlineData(0, 2_000)]
    [InlineData(3_000, 3_000)]
    public void FromSettings_CandidateLimit_IsClampedToBudget(int configured, int expected)
    {
        // Act
        var options = SearchOptions.FromSettings(new SearchSettings { CandidateLimit = configured });

        // Assert
        options.CandidateLimit.ShouldBe(expected);
    }

    [Fact]
    public void FromSettings_PresetAndHalfLife_AreApplied()
    {
        // Act
        var options = SearchOptions.FromSettings(new SearchSettings { RankingPreset = "recent", FrecencyHalfLifeDays = 7, HistoryLimit = 20 });

        // Assert
        options.Preset.ShouldBe(RankingPreset.Recent);
        options.FrecencyHalfLife.ShouldBe(TimeSpan.FromDays(7));
        options.HistoryLimit.ShouldBe(20);
    }
}
