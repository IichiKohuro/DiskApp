using DiskApp.Contracts.App;
using DiskApp.UI.Localization;

namespace DiskApp.UI.Text;

/// <summary>
/// Уведомление о восстановлении после повреждения (PLAN.md, этап 10.3): база индекса или файл настроек были
/// повреждены при старте. Обе причины сводятся в одно сообщение, потому что полоса уведомлений в окне одна.
/// </summary>
/// <param name="Title">Заголовок.</param>
/// <param name="Message">Что случилось и что из этого следует.</param>
public sealed record RecoveryNotice(string Title, string Message)
{
    /// <summary>Уведомление по сведениям о приложении; <c>null</c>, если восстанавливать ничего не пришлось.</summary>
    public static RecoveryNotice? From(AppInfoDto info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var parts = new List<string>(2);
        if (info.CorruptDatabasePath is { } database)
        {
            parts.Add(L.F("Recovery.Database", database));
        }

        if (info.CorruptSettingsPath is { } settings)
        {
            parts.Add(L.F(info.SettingsRestoredFromBackup ? "Recovery.SettingsFromBackup" : "Recovery.SettingsDefaults", settings));
        }

        return parts.Count == 0 ? null : new RecoveryNotice(L.T("Recovery.Title"), string.Join(" ", parts));
    }
}
