using DiskApp.UI.Localization;

namespace DiskApp.UI.Services;

/// <summary>
/// Заголовки секций подборки для пустого запроса на языке интерфейса: одни и те же у макета и у сервиса поверх API.
/// </summary>
public static class SectionTitles
{
    /// <summary>Избранное пользователя.</summary>
    public static string Pinned => L.T("Sections.Pinned");

    /// <summary>Открытые через DiskApp, самые свежие первыми.</summary>
    public static string RecentlyOpened => L.T("Sections.RecentlyOpened");

    /// <summary>Изменённые на диске, самые свежие первыми.</summary>
    public static string RecentlyModified => L.T("Sections.RecentlyModified");
}
