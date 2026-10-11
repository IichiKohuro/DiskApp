namespace DiskApp.UI.Services;

/// <summary>
/// Заголовки секций подборки для пустого запроса: одни и те же у макета и у сервиса поверх API.
/// </summary>
public static class SectionTitles
{
    /// <summary>Избранное пользователя.</summary>
    public const string Pinned = "Закреплённые";

    /// <summary>Открытые через DiskApp, самые свежие первыми.</summary>
    public const string RecentlyOpened = "Недавно открытые";

    /// <summary>Изменённые на диске, самые свежие первыми.</summary>
    public const string RecentlyModified = "Недавно изменённые";
}
