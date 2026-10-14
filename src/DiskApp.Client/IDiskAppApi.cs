using System.Text.Json.Nodes;

using DiskApp.Contracts.App;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.History;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Notes;
using DiskApp.Contracts.Operations;
using DiskApp.Contracts.Roots;
using DiskApp.Contracts.Search;

namespace DiskApp.Client;

/// <summary>
/// Типизированный клиент API DiskApp (PLAN.md, этап 5.1). Ошибки сервера — <see cref="DiskAppApiException"/>
/// с кодом доменной ошибки; отмена — через <see cref="CancellationToken"/> (поиск по мере ввода отменяет прошлый запрос).
/// </summary>
public interface IDiskAppApi
{
    /// <summary>Сервер жив: версия, схема базы, состояние индексации.</summary>
    Task<HealthDto> GetHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>Сведения о приложении.</summary>
    Task<AppInfoDto> GetAppInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Корни.</summary>
    Task<IReadOnlyList<RootDto>> GetRootsAsync(CancellationToken cancellationToken = default);

    /// <summary>Добавить корень.</summary>
    Task<RootDto> CreateRootAsync(CreateRootRequest request, CancellationToken cancellationToken = default);

    /// <summary>Изменить корень.</summary>
    Task<RootDto> UpdateRootAsync(long id, UpdateRootRequest request, CancellationToken cancellationToken = default);

    /// <summary>Удалить корень из индекса (файлы на диске не трогаются).</summary>
    Task DeleteRootAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Пересканировать корень.</summary>
    Task RescanRootAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Приостановить индексацию корня.</summary>
    Task PauseRootAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Возобновить индексацию корня.</summary>
    Task ResumeRootAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Сводка корня: файлы, папки, размер.</summary>
    Task<RootStatsDto> GetRootStatsAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Состояние индексации.</summary>
    Task<IndexStatusDto> GetIndexStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Пересобрать индекс.</summary>
    Task RebuildIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>Сколько записей индекса исключит правило (этап 10.2).</summary>
    Task<ExclusionPreviewDto> PreviewExclusionAsync(string pattern, CancellationToken cancellationToken = default);

    /// <summary>Приостановить всю индексацию.</summary>
    Task<IndexStatusDto> PauseIndexingAsync(CancellationToken cancellationToken = default);

    /// <summary>Возобновить индексацию.</summary>
    Task<IndexStatusDto> ResumeIndexingAsync(CancellationToken cancellationToken = default);

    /// <summary>Поиск.</summary>
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Подсказки к запросу.</summary>
    Task<SuggestResponse> SuggestAsync(string? query, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>История запросов.</summary>
    Task<IReadOnlyList<HistoryEntryDto>> GetHistoryAsync(int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>Удалить запрос из истории.</summary>
    Task DeleteHistoryEntryAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Очистить историю.</summary>
    Task ClearHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Запись индекса.</summary>
    Task<EntryDto> GetEntryAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Содержимое папки по идентификатору.</summary>
    Task<BrowseResponse> GetChildrenAsync(long id, BrowseOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Содержимое папки по пути внутри корней.</summary>
    Task<BrowseResponse> BrowseAsync(string path, BrowseOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Скачивает содержимое файла в поток; прогресс — число полученных байтов.
    /// </summary>
    Task DownloadContentAsync(long id, Stream destination, IProgress<long>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Иконка записи (PNG).</summary>
    Task<byte[]> GetIconAsync(long id, int size = 32, CancellationToken cancellationToken = default);

    /// <summary>Миниатюра записи (PNG).</summary>
    Task<byte[]> GetThumbnailAsync(long id, int size = 256, CancellationToken cancellationToken = default);

    /// <summary>Открыть приложением по умолчанию; <paramref name="query"/> попадёт в историю.</summary>
    Task OpenAsync(long id, string? query = null, CancellationToken cancellationToken = default);

    /// <summary>Диалог «Открыть с помощью».</summary>
    Task OpenWithAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Показать в Проводнике.</summary>
    Task RevealAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Окно свойств.</summary>
    Task ShowPropertiesAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Запустить от имени администратора.</summary>
    Task RunAsAdminAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Заменить набор тегов записи.</summary>
    Task<EntryDto> SetEntryTagsAsync(long id, IReadOnlyList<long> tagIds, CancellationToken cancellationToken = default);

    /// <summary>Записи индекса по списку путей (до <see cref="ResolveEntriesRequest.MaxPaths"/>).</summary>
    Task<ResolveEntriesResponse> ResolveEntriesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Поставить файловую операцию.</summary>
    Task<OperationDto> EnqueueOperationAsync(OperationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Разбор: перенести из накопителя в рабочую папку с тегами (операция очереди).</summary>
    Task<OperationDto> SortFilesAsync(SortFilesRequest request, CancellationToken cancellationToken = default);

    /// <summary>Окно Drag&amp;Drop: бросить файлы в папку-накопитель — перенос или копия (операция очереди).</summary>
    Task<OperationDto> DropFilesAsync(DropFilesRequest request, CancellationToken cancellationToken = default);

    /// <summary>Очередь папок-накопителей (этап 17.1).</summary>
    Task<InboxDto> GetInboxAsync(CancellationToken cancellationToken = default);

    /// <summary>Отложить, оставить здесь или вернуть в новые файлы накопителя.</summary>
    Task SetInboxStateAsync(SetInboxStateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Сухой прогон шаблонов рабочих папок (этап 17.5): что куда ушло бы из накопителей сейчас; ничего не переносит.</summary>
    Task<SortingPreviewDto> PreviewSortingRulesAsync(SortingPreviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>Лента истории файлов (этап 20), новые сверху; <c>before</c> — номер события для следующей страницы.</summary>
    Task<FileHistoryPageDto> GetFileHistoryAsync(FileHistoryFilter filter, CancellationToken cancellationToken = default);

    /// <summary>История одного файла или папки по пути, через переносы и переименования.</summary>
    Task<IReadOnlyList<FileEventDto>> GetFileHistoryOfPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>«Вернуть на место» или «Восстановить» по событию истории.</summary>
    Task<FileHistoryRevertDto> RevertFileEventAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Заметки (этап 23): закреплённые сверху; поиск по тексту, тег заметок, корзина.</summary>
    Task<IReadOnlyList<NoteDto>> GetNotesAsync(string? query = null, long? tagId = null, bool deleted = false, CancellationToken cancellationToken = default);

    /// <summary>Заметка.</summary>
    Task<NoteDto> GetNoteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Новая заметка.</summary>
    Task<NoteDto> CreateNoteAsync(NoteRequest request, CancellationToken cancellationToken = default);

    /// <summary>Изменить заметку.</summary>
    Task<NoteDto> UpdateNoteAsync(long id, NoteRequest request, CancellationToken cancellationToken = default);

    /// <summary>Удалить заметку в корзину; <paramref name="permanent"/> — навсегда, только из корзины.</summary>
    Task DeleteNoteAsync(long id, bool permanent = false, CancellationToken cancellationToken = default);

    /// <summary>Вернуть заметку из корзины.</summary>
    Task<NoteDto> RestoreNoteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Теги заметок.</summary>
    Task<IReadOnlyList<TagDto>> GetNoteTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Создать тег заметок.</summary>
    Task<TagDto> CreateNoteTagAsync(TagRequest request, CancellationToken cancellationToken = default);

    /// <summary>Изменить тег заметок.</summary>
    Task<TagDto> UpdateNoteTagAsync(long id, TagRequest request, CancellationToken cancellationToken = default);

    /// <summary>Удалить тег заметок.</summary>
    Task DeleteNoteTagAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Операции.</summary>
    Task<IReadOnlyList<OperationDto>> GetOperationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Операция.</summary>
    Task<OperationDto> GetOperationAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Отменить операцию.</summary>
    Task CancelOperationAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Решение по конфликту.</summary>
    Task DecideOperationAsync(Guid id, ConflictDecisionDto decision, CancellationToken cancellationToken = default);

    /// <summary>Отменить результат операции.</summary>
    Task<OperationDto> UndoOperationAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Загружает файлы в папку; прогресс — число отправленных байтов.
    /// </summary>
    Task<UploadResponse> UploadAsync(string destination, IReadOnlyList<UploadFile> files, ConflictPolicy conflicts = ConflictPolicy.KeepBoth,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Избранное.</summary>
    Task<IReadOnlyList<EntryDto>> GetFavoritesAsync(CancellationToken cancellationToken = default);

    /// <summary>Добавить в избранное.</summary>
    Task<EntryDto> AddFavoriteAsync(long entryId, CancellationToken cancellationToken = default);

    /// <summary>Убрать из избранного.</summary>
    Task RemoveFavoriteAsync(long entryId, CancellationToken cancellationToken = default);

    /// <summary>Порядок избранного.</summary>
    Task ReorderFavoritesAsync(IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default);

    /// <summary>Закреплённое на главной.</summary>
    Task<IReadOnlyList<EntryDto>> GetPinnedAsync(CancellationToken cancellationToken = default);

    /// <summary>Недавно открытые.</summary>
    Task<IReadOnlyList<EntryDto>> GetRecentAsync(int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>Теги.</summary>
    Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Создать тег.</summary>
    Task<TagDto> CreateTagAsync(TagRequest request, CancellationToken cancellationToken = default);

    /// <summary>Изменить тег.</summary>
    Task<TagDto> UpdateTagAsync(long id, TagRequest request, CancellationToken cancellationToken = default);

    /// <summary>Авто-теги всех папок (этап 19).</summary>
    Task<IReadOnlyList<FolderTagsDto>> GetAutoTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Заменить авто-теги папки; пустой список снимает все.</summary>
    Task<FolderTagsDto> SetAutoTagsAsync(SetFolderTagsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Удалить тег.</summary>
    Task DeleteTagAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Коллекции.</summary>
    Task<IReadOnlyList<CollectionDto>> GetCollectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Создать коллекцию.</summary>
    Task<CollectionDto> CreateCollectionAsync(CollectionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Изменить коллекцию.</summary>
    Task<CollectionDto> UpdateCollectionAsync(long id, CollectionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Удалить коллекцию.</summary>
    Task DeleteCollectionAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Записи ручной коллекции.</summary>
    Task<IReadOnlyList<EntryDto>> GetCollectionItemsAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Заменить состав ручной коллекции.</summary>
    Task SetCollectionItemsAsync(long id, IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default);

    /// <summary>Настройки.</summary>
    Task<SettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Изменить настройки частичным JSON (JSON Merge Patch).</summary>
    Task<SettingsDto> PatchSettingsAsync(JsonObject patch, CancellationToken cancellationToken = default);
}

/// <summary>
/// Сортировка и страница содержимого папки.
/// </summary>
/// <param name="Sort">Поле сортировки; по умолчанию — имя.</param>
/// <param name="Descending">По убыванию.</param>
/// <param name="Offset">Смещение.</param>
/// <param name="Limit">Записей на странице.</param>
public sealed record BrowseOptions(SearchSort? Sort = null, bool? Descending = null, int? Offset = null, int? Limit = null);

/// <summary>
/// Файл для загрузки.
/// </summary>
/// <param name="FileName">Имя файла в папке назначения.</param>
/// <param name="OpenRead">Открывает содержимое; поток закрывается после отправки.</param>
/// <param name="Length">Размер, если известен (для прогресса).</param>
public sealed record UploadFile(string FileName, Func<Stream> OpenRead, long? Length = null);
