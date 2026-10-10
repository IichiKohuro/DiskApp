using DiskApp.Core.Domain;

namespace DiskApp.Core.Tests.Domain;

/// <summary>
/// Небольшие модели домена: frecency статистики использования, сохранённые запросы, правила исключения.
/// </summary>
public sealed class DomainRecordsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Frecency_NeverOpened_IsZero()
    {
        // Arrange
        var stat = new UsageStat(1, 0, null);

        // Act & Assert
        stat.Frecency(Now).ShouldBe(0);
    }

    [Fact]
    public void Frecency_JustOpened_EqualsLogOfOpens()
    {
        // Arrange
        var stat = new UsageStat(1, 9, Now);

        // Act
        var frecency = stat.Frecency(Now);

        // Assert
        frecency.ShouldBe(Math.Log(10), tolerance: 1e-9);
    }

    [Fact]
    public void Frecency_AfterHalfLife_HalvesAndNeverGrowsWithFutureDates()
    {
        // Arrange
        var recent = new UsageStat(1, 9, Now);
        var old = new UsageStat(1, 9, Now - TimeSpan.FromDays(30));
        var future = new UsageStat(1, 9, Now + TimeSpan.FromDays(5));

        // Act & Assert
        old.Frecency(Now).ShouldBe(recent.Frecency(Now) / 2, tolerance: 1e-9);
        future.Frecency(Now).ShouldBe(recent.Frecency(Now), tolerance: 1e-9);
        old.Frecency(Now, TimeSpan.FromDays(60)).ShouldBeGreaterThan(old.Frecency(Now));
    }

    [Fact]
    public void Opened_IncrementsCountAndUpdatesTime()
    {
        // Arrange
        var stat = new UsageStat(1, 2, Now - TimeSpan.FromDays(1));

        // Act
        var opened = stat.Opened(Now);

        // Assert
        opened.OpenCount.ShouldBe(3);
        opened.LastOpenedUtc.ShouldBe(Now);
        stat.OpenCount.ShouldBe(2, "исходная запись не меняется");
    }

    [Fact]
    public void IsSavedQuery_RequiresKindAndNonEmptyQuery()
    {
        // Act & Assert
        new Collection(1, "Отчёты", CollectionKind.SavedQuery, "ext:pdf").IsSavedQuery.ShouldBeTrue();
        new Collection(2, "Пустая", CollectionKind.SavedQuery, " ").IsSavedQuery.ShouldBeFalse();
        new Collection(3, "Ручная", CollectionKind.Manual, "ext:pdf").IsSavedQuery.ShouldBeFalse();
    }

    [Fact]
    public void ExclusionRule_Factories_ProduceKindsAndThresholds()
    {
        // Act & Assert
        ExclusionRule.Glob("*.tmp").ShouldBe(new ExclusionRule(ExclusionKind.Glob, "*.tmp"));
        ExclusionRule.Hidden().Kind.ShouldBe(ExclusionKind.Hidden);
        ExclusionRule.System().Pattern.ShouldBe(string.Empty);
        ExclusionRule.MaxSize(10_485_760).Threshold.ShouldBe(10_485_760);
        ExclusionRule.MaxDepth(3).Threshold.ShouldBe(3);
        ExclusionRule.Glob("bin").Threshold.ShouldBeNull();
    }

    [Fact]
    public void ExclusionRule_InvalidArguments_Throw()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => ExclusionRule.Glob(" "));
        Should.Throw<ArgumentOutOfRangeException>(() => ExclusionRule.MaxSize(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => ExclusionRule.MaxDepth(0));
    }

    [Fact]
    public void ExclusionRule_Defaults_CoverServiceFoldersAndBuildArtifacts()
    {
        // Act
        var patterns = ExclusionRule.Defaults.Select(static rule => rule.Pattern).ToArray();

        // Assert
        patterns.ShouldContain("$RECYCLE.BIN");
        patterns.ShouldContain("node_modules");
        patterns.ShouldContain(".git");
        patterns.ShouldContain("*.tmp");
        ExclusionRule.Defaults.ShouldAllBe(static rule => rule.Kind == ExclusionKind.Glob);
    }
}
