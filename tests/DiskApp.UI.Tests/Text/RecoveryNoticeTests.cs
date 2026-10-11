using DiskApp.Contracts.App;
using DiskApp.UI.Text;

namespace DiskApp.UI.Tests.Text;

/// <summary>
/// Уведомление о восстановлении при старте: ничего не было повреждено, база, настройки из копии или по умолчанию.
/// </summary>
public sealed class RecoveryNoticeTests
{
    private static readonly AppInfoDto Info = new("DiskApp", "0.6.0", ".NET", "Windows", @"C:\Data", false, DateTimeOffset.UnixEpoch);

    [Fact]
    public void From_NothingRecovered_ReturnsNull()
    {
        // Act
        var notice = RecoveryNotice.From(Info);

        // Assert
        notice.ShouldBeNull();
    }

    [Fact]
    public void From_DatabaseAndSettingsFromBackup_CombinesBothInOneMessage()
    {
        // Arrange
        var info = Info with
        {
            CorruptDatabasePath = @"C:\Data\diskapp.corrupt.db",
            CorruptSettingsPath = @"C:\Data\settings.corrupt.json",
            SettingsRestoredFromBackup = true,
        };

        // Act
        var notice = RecoveryNotice.From(info);

        // Assert
        notice.ShouldNotBeNull();
        notice.Title.ShouldBe("Данные восстановлены после повреждения");
        notice.Message.ShouldContain(@"«C:\Data\diskapp.corrupt.db»; индекс строится заново");
        notice.Message.ShouldContain(@"«C:\Data\settings.corrupt.json»; настройки восстановлены из резервной копии");
    }

    [Fact]
    public void From_SettingsWithoutBackup_SaysDefaultsAreInEffect()
    {
        // Act
        var notice = RecoveryNotice.From(Info with { CorruptSettingsPath = @"C:\Data\settings.corrupt.json" });

        // Assert
        notice.ShouldNotBeNull();
        notice.Message.ShouldEndWith("действуют настройки по умолчанию.");
        notice.Message.ShouldNotContain("База индекса");
    }
}
