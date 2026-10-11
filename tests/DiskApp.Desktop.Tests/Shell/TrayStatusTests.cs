using DiskApp.Contracts.Hub;
using DiskApp.Contracts.Roots;
using DiskApp.Desktop.Shell;

namespace DiskApp.Desktop.Tests.Shell;

/// <summary>
/// Состояние трея по событиям хаба (PLAN.md, этап 6.3): запуск, простой, индексация с числом записей, пауза,
/// недоступные корни, ошибка корня и сбой сервера.
/// </summary>
public sealed class TrayStatusTests
{
    [Fact]
    public void BeforeServer_IsStarting()
    {
        // Act
        var status = new TrayStatus();

        // Assert
        status.State.ShouldBe(TrayState.Starting);
        status.ToolTip.ShouldBe("DiskApp — запускается…");
        status.IsPaused.ShouldBeFalse();
    }

    [Fact]
    public void Status_IdleThenIndexingWithProgressThenIdle()
    {
        // Arrange
        var status = new TrayStatus();
        status.Apply(Hub(IndexerState.Idle, Root(1, "Документы", RootStatus.Online)));
        var idle = (status.State, status.ToolTip);

        // Act
        status.Apply(new RootStatusChangedDto(1, RootStatus.Scanning, null));
        status.Apply(new IndexProgressDto(1, IndexPhase.Writing, 15_200, 15_000, @"D:\Документы\Фото", 2_000, 7.5));
        var indexing = (status.State, status.ToolTip);
        status.Apply(new RootStatusChangedDto(1, RootStatus.Online, null));

        // Assert
        idle.ShouldBe((TrayState.Idle, "DiskApp — индекс актуален"));
        indexing.State.ShouldBe(TrayState.Indexing);
        indexing.ToolTip.ShouldStartWith("DiskApp — индексация: 15");
        indexing.ToolTip.ShouldEndWith("записей");
        status.State.ShouldBe(TrayState.Idle);
    }

    [Fact]
    public void PausedOfflineAndError_ShowRootLabels()
    {
        // Arrange
        var status = new TrayStatus();

        // Act & Assert
        status.Apply(new IndexStatusDto(IndexerState.Paused, true, [Root(1, "Работа", RootStatus.Paused)]));
        status.State.ShouldBe(TrayState.Paused);
        status.IsPaused.ShouldBeTrue();
        status.ToolTip.ShouldBe("DiskApp — индексация на паузе");

        status.Apply(Hub(IndexerState.PartiallyOffline, Root(1, "Работа", RootStatus.Online), Root(2, "Флешка", RootStatus.Offline)));
        status.State.ShouldBe(TrayState.Offline);
        status.ToolTip.ShouldBe("DiskApp — недоступно: Флешка");

        status.Apply(new RootStatusChangedDto(1, RootStatus.Error, "Нет доступа"));
        status.State.ShouldBe(TrayState.Error);
        status.ToolTip.ShouldBe("DiskApp — ошибка индексации: Работа");
    }

    [Fact]
    public void ServerFailure_IsError()
    {
        // Arrange
        var status = new TrayStatus();

        // Act
        status.Fail("Порт занят");

        // Assert
        status.State.ShouldBe(TrayState.Error);
        status.ToolTip.ShouldBe("DiskApp — сервер не запустился");
    }

    [Fact]
    public void ToolTip_IsLimitedTo127Characters()
    {
        // Arrange
        var status = new TrayStatus();
        var roots = Enumerable.Range(1, 30).Select(static i => Root(i, $"Очень длинная подпись корня {i}", RootStatus.Offline)).ToArray();

        // Act
        status.Apply(Hub(IndexerState.PartiallyOffline, roots));

        // Assert
        status.ToolTip.Length.ShouldBeLessThanOrEqualTo(127);
        status.ToolTip.ShouldEndWith("…");
    }

    private static HubStatusDto Hub(IndexerState state, params RootIndexStatusDto[] roots) => new(new IndexStatusDto(state, state == IndexerState.Paused, roots), []);

    private static RootIndexStatusDto Root(long id, string label, RootStatus status) => new(id, @"D:\" + label, label, status, null, null, false, null, null);
}
