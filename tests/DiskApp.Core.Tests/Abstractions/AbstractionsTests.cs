using DiskApp.Core.Abstractions;
using DiskApp.Core.Repositories;
using DiskApp.Core.Results;

namespace DiskApp.Core.Tests.Abstractions;

/// <summary>
/// Мелкие реализации и типы рядом с интерфейсами: системные часы, результат без значения, сумма итогов записи.
/// </summary>
public sealed class AbstractionsTests
{
    [Fact]
    public void SystemClock_UtcNow_IsCloseToSystemTimeInUtc()
    {
        // Act
        var now = SystemClock.Instance.UtcNow;

        // Assert
        now.Offset.ShouldBe(TimeSpan.Zero);
        (DateTimeOffset.UtcNow - now).ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Result_OkAndFail_ReportStateWithoutValue()
    {
        // Arrange
        var error = new DomainError("shell.open_failed", "Не удалось открыть");

        // Act
        var ok = Result.Ok();
        var fail = Result.Fail(error);
        Result uninitialized = default;

        // Assert
        ok.IsSuccess.ShouldBeTrue();
        ok.Error.ShouldBeNull();
        ok.ToString().ShouldBe("Ok");
        fail.IsFailure.ShouldBeTrue();
        fail.Error.ShouldBe(error);
        uninitialized.IsFailure.ShouldBeTrue();
        uninitialized.Error.ShouldBe(DomainError.Uninitialized);
    }

    [Fact]
    public void UpsertResult_Add_SumsCountsAndTotal()
    {
        // Act
        var sum = new UpsertResult(1, 2, 3) + new UpsertResult(10, 20, 30);

        // Assert
        sum.ShouldBe(new UpsertResult(11, 22, 33));
        sum.Total.ShouldBe(66);
        UpsertResult.Add(sum, default).ShouldBe(sum);
    }

    [Fact]
    public void IndexStats_EntryCount_SumsFilesAndDirectories()
    {
        // Act
        var stats = new IndexStats(1, 100, 20, 4096);

        // Assert
        stats.EntryCount.ShouldBe(120);
    }
}
