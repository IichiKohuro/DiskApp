using System.Globalization;

namespace DiskApp.Contracts;

/// <summary>
/// Маршруты API — общие для сервера и клиентов, чтобы адреса не расходились (PLAN.md, этап 4.2).
/// Шаблоны с параметрами (<c>{id}</c>) — для сервера, методы с аргументами — для клиентов.
/// </summary>
public static class ApiRoutes
{
    /// <summary>Проверка живости сервера; вне версии API.</summary>
    public const string Health = "/health";

    /// <summary>Маршруты первой версии API.</summary>
    public static class V1
    {
        /// <summary>Префикс всех маршрутов версии.</summary>
        public const string Prefix = "/api/v1";

        /// <summary>Корни: список и добавление.</summary>
        public const string Roots = Prefix + "/roots";

        /// <summary>Корень по идентификатору: изменение и удаление.</summary>
        public const string RootById = Roots + "/{id:long}";

        /// <summary>Пересканировать корень.</summary>
        public const string RootRescan = RootById + "/rescan";

        /// <summary>Приостановить индексацию.</summary>
        public const string RootPause = RootById + "/pause";

        /// <summary>Возобновить индексацию.</summary>
        public const string RootResume = RootById + "/resume";

        /// <summary>Сводка корня: число файлов и папок, суммарный размер.</summary>
        public const string RootStats = RootById + "/stats";

        /// <summary>Состояние индексации.</summary>
        public const string IndexStatus = Prefix + "/index/status";

        /// <summary>Пересобрать весь индекс.</summary>
        public const string IndexRebuild = Prefix + "/index/rebuild";

        /// <summary>Сколько записей индекса исключит правило: <c>?pattern=</c>.</summary>
        public const string IndexExclusionPreview = Prefix + "/index/exclusions/preview";

        /// <summary>Приостановить всю индексацию.</summary>
        public const string IndexPause = Prefix + "/index/pause";

        /// <summary>Возобновить индексацию.</summary>
        public const string IndexResume = Prefix + "/index/resume";

        /// <summary>Поиск: <c>GET</c> с параметрами или <c>POST</c> с телом.</summary>
        public const string Search = Prefix + "/search";

        /// <summary>Подсказки к запросу.</summary>
        public const string Suggest = Prefix + "/suggest";

        /// <summary>История запросов: список и очистка.</summary>
        public const string History = Prefix + "/history";

        /// <summary>Одна запись истории: <c>DELETE</c> с параметром <c>query</c>.</summary>
        public const string HistoryEntry = History + "/entry";

        /// <summary>Запись индекса.</summary>
        public const string EntryById = Prefix + "/entries/{id:long}";

        /// <summary>Содержимое папки.</summary>
        public const string EntryChildren = EntryById + "/children";

        /// <summary>Иконка записи (PNG).</summary>
        public const string EntryIcon = EntryById + "/icon";

        /// <summary>Миниатюра записи.</summary>
        public const string EntryThumbnail = EntryById + "/thumbnail";

        /// <summary>Содержимое файла потоком с поддержкой Range.</summary>
        public const string EntryContent = EntryById + "/content";

        /// <summary>Открыть приложением по умолчанию.</summary>
        public const string EntryOpen = EntryById + "/open";

        /// <summary>Диалог «Открыть с помощью».</summary>
        public const string EntryOpenWith = EntryById + "/open-with";

        /// <summary>Показать в Проводнике.</summary>
        public const string EntryReveal = EntryById + "/reveal";

        /// <summary>Окно свойств.</summary>
        public const string EntryProperties = EntryById + "/properties";

        /// <summary>Запуск от имени администратора.</summary>
        public const string EntryRunAsAdmin = EntryById + "/run-as-admin";

        /// <summary>Теги записи.</summary>
        public const string EntryTags = EntryById + "/tags";

        /// <summary>Содержимое папки по пути внутри корней.</summary>
        public const string Browse = Prefix + "/browse";

        /// <summary>Записи индекса по списку путей.</summary>
        public const string ResolveEntries = Prefix + "/entries/resolve";

        /// <summary>Файловые операции: постановка и список.</summary>
        public const string Operations = Prefix + "/operations";

        /// <summary>Операция по идентификатору.</summary>
        public const string OperationById = Operations + "/{id:guid}";

        /// <summary>Отменить операцию.</summary>
        public const string OperationCancel = OperationById + "/cancel";

        /// <summary>Решение по конфликту.</summary>
        public const string OperationDecision = OperationById + "/decision";

        /// <summary>Отменить результат операции.</summary>
        public const string OperationUndo = OperationById + "/undo";

        /// <summary>Загрузка файлов в папку (multipart).</summary>
        public const string Upload = Prefix + "/upload";

        /// <summary>Разбор: перенос из накопителя в рабочую папку с тегами.</summary>
        public const string SortingMove = Prefix + "/sorting/move";

        /// <summary>Избранное: список (записи) и добавление.</summary>
        public const string Favorites = Prefix + "/favorites";

        /// <summary>Убрать из избранного.</summary>
        public const string FavoriteByEntry = Favorites + "/{entryId:long}";

        /// <summary>Порядок избранного.</summary>
        public const string FavoritesOrder = Favorites + "/order";

        /// <summary>Закреплённое (избранное) для главной.</summary>
        public const string Pinned = Prefix + "/pinned";

        /// <summary>Недавно открытые.</summary>
        public const string Recent = Prefix + "/recent";

        /// <summary>Теги: список и создание.</summary>
        public const string Tags = Prefix + "/tags";

        /// <summary>Тег по идентификатору.</summary>
        public const string TagById = Tags + "/{id:long}";

        /// <summary>Коллекции: список и создание.</summary>
        public const string Collections = Prefix + "/collections";

        /// <summary>Коллекция по идентификатору.</summary>
        public const string CollectionById = Collections + "/{id:long}";

        /// <summary>Элементы коллекции.</summary>
        public const string CollectionItems = CollectionById + "/items";

        /// <summary>Настройки: чтение и частичное изменение.</summary>
        public const string Settings = Prefix + "/settings";

        /// <summary>Сведения о приложении.</summary>
        public const string AppInfo = Prefix + "/app/info";

        /// <summary>Адрес корня.</summary>
        public static string Root(long id) => Roots + "/" + Id(id);

        /// <summary>Адрес записи.</summary>
        public static string Entry(long id) => Prefix + "/entries/" + Id(id);

        /// <summary>Адрес операции.</summary>
        public static string Operation(Guid id) => Operations + "/" + id.ToString("D", CultureInfo.InvariantCulture);

        /// <summary>Адрес тега.</summary>
        public static string Tag(long id) => Tags + "/" + Id(id);

        /// <summary>Адрес коллекции.</summary>
        public static string Collection(long id) => Collections + "/" + Id(id);

        private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Заголовки API.
/// </summary>
public static class ApiHeaders
{
    /// <summary>
    /// Токен сессии, если сервер его требует (флаг): случайная строка, известная только десктопу. Хаб принимает её
    /// также в параметре <see cref="SessionTokenQuery"/> (браузерный WebSocket не умеет заголовки).
    /// </summary>
    public const string SessionToken = "X-DiskApp-Token";

    /// <summary>Параметр запроса с токеном сессии для хаба.</summary>
    public const string SessionTokenQuery = "access_token";
}

/// <summary>
/// Хаб SignalR: адрес и методы, которые вызывает клиент.
/// </summary>
public static class HubRoutes
{
    /// <summary>Адрес хаба.</summary>
    public const string Path = "/hubs/diskapp";

    /// <summary>Подписаться на события корня (изменения записей).</summary>
    public const string SubscribeRoot = "SubscribeRoot";

    /// <summary>Отписаться от событий корня.</summary>
    public const string UnsubscribeRoot = "UnsubscribeRoot";

    /// <summary>Текущее состояние индексации и активные операции.</summary>
    public const string GetStatus = "GetStatus";
}
