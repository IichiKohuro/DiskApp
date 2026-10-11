using DiskApp.Core.Domain;
using DiskApp.Core.Search;

namespace DiskApp.Core.Repositories;

/// <summary>
/// Записи индекса. Запись идёт пакетами через единственного писателя (docs/ARCHITECTURE.md, раздел 5),
/// чтение — параллельно. Уникальный ключ записи — пара (корень, относительный путь).
/// </summary>
public interface IFileIndexRepository
{
    /// <summary>
    /// Вставляет или обновляет записи пакетом. Родитель определяется по <see cref="FileEntry.RelPath"/>,
    /// поле <see cref="FileEntry.ParentId"/> игнорируется. Записи с теми же размером и временем изменения
    /// только помечаются текущим сканированием.
    /// </summary>
    /// <param name="entries">Записи одного корня; папки должны идти раньше своих детей.</param>
    /// <param name="scanId">Идентификатор сканирования для mark-and-sweep.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<UpsertResult> UpsertAsync(IReadOnlyList<FileEntry> entries, long scanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// То же, что <see cref="UpsertAsync"/>, но дополнительно сообщает, какие записи пакета добавлены и какие обновлены:
    /// по ним конвейер сканирования строит агрегированное событие изменений по папкам.
    /// </summary>
    Task<UpsertDetails> UpsertDetailedAsync(IReadOnlyList<FileEntry> entries, long scanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Следующий идентификатор сканирования корня: на единицу больше наибольшего у его записей (1 для пустого корня).
    /// Одновременно корень сканирует только одна задача, поэтому гонки здесь нет.
    /// </summary>
    Task<long> NextScanIdAsync(long rootId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Помечает запись и её поддерево сканированием, не меняя остальных полей; возвращает число помеченных.
    /// Так сохраняются записи папок, которые не удалось прочитать в этом сканировании (нет доступа, временная ошибка).
    /// </summary>
    Task<int> MarkSubtreeAsync(long rootId, string relPath, long scanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет из поддерева (сама папка не трогается) записи, не помеченные сканированием; возвращает число удалённых.
    /// Завершение mark-and-sweep для пересканирования одной папки.
    /// </summary>
    Task<int> SweepSubtreeAsync(long rootId, string relPath, long scanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Прямые дети папки по её относительному пути; пустой путь — верхний уровень корня. Без сортировки и страниц:
    /// используется сверкой для сравнения с содержимым папки на диске.
    /// </summary>
    Task<IReadOnlyList<FileEntry>> GetChildrenOfPathAsync(long rootId, string relPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет запись и всё её поддерево; возвращает число удалённых.
    /// </summary>
    Task<int> DeleteSubtreeAsync(long rootId, string relPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Переименовывает или переносит поддерево внутри корня одним запросом; возвращает число затронутых записей.
    /// </summary>
    Task<int> RenameSubtreeAsync(long rootId, string oldRelPath, string newRelPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет записи корня, не помеченные указанным сканированием (завершение mark-and-sweep); возвращает число удалённых.
    /// </summary>
    Task<int> SweepAsync(long rootId, long scanId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет все записи корня.
    /// </summary>
    Task<int> DeleteByRootAsync(long rootId, CancellationToken cancellationToken = default);

    /// <summary>Запись по идентификатору.</summary>
    Task<FileEntry?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Записи по идентификаторам в произвольном порядке; отсутствующие пропускаются.</summary>
    Task<IReadOnlyList<FileEntry>> GetManyAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default);

    /// <summary>Запись по корню и относительному пути.</summary>
    Task<FileEntry?> GetByPathAsync(long rootId, string relPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Дети папки (или верхний уровень корня при <paramref name="parentId"/> = <c>null</c>) с сортировкой и постраничной выборкой.
    /// Папки идут раньше файлов.
    /// </summary>
    Task<IReadOnlyList<FileEntry>> GetChildrenAsync(
        long rootId,
        long? parentId,
        SearchSortField sortField,
        bool descending,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Сводка по корню: число файлов и папок, суммарный размер.</summary>
    Task<IndexStats> GetStatsAsync(long rootId, CancellationToken cancellationToken = default);

    /// <summary>Общее число записей в индексе.</summary>
    Task<long> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Считает записи, относительный путь которых подходит под условие (предпросмотр правила исключения, этап 10.2).
    /// Пути читаются потоком; по истечении <paramref name="timeLimit"/> подсчёт останавливается и результат неполный.
    /// </summary>
    Task<PathCount> CountPathsAsync(Func<string, bool> matches, TimeSpan timeLimit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Итог пакетной записи.
/// </summary>
/// <param name="Inserted">Добавлено новых записей.</param>
/// <param name="Updated">Обновлено изменившихся.</param>
/// <param name="Unchanged">Совпали по размеру и времени, только помечены сканированием.</param>
public readonly record struct UpsertResult(int Inserted, int Updated, int Unchanged)
{
    /// <summary>Всего обработано.</summary>
    public int Total => Inserted + Updated + Unchanged;

    /// <summary>Поэлементная сумма двух итогов.</summary>
    public static UpsertResult operator +(UpsertResult left, UpsertResult right)
        => new(left.Inserted + right.Inserted, left.Updated + right.Updated, left.Unchanged + right.Unchanged);

    /// <summary>Сумма двух итогов (именованный аналог оператора).</summary>
    public static UpsertResult Add(UpsertResult left, UpsertResult right) => left + right;
}

/// <summary>
/// Итог пакетной записи с номерами (индексами в пакете) добавленных и обновлённых записей.
/// </summary>
/// <param name="Totals">Счётчики.</param>
/// <param name="InsertedIndices">Индексы добавленных записей в переданном пакете, по возрастанию.</param>
/// <param name="UpdatedIndices">Индексы обновлённых записей, по возрастанию.</param>
public sealed record UpsertDetails(UpsertResult Totals, IReadOnlyList<int> InsertedIndices, IReadOnlyList<int> UpdatedIndices)
{
    /// <summary>Пустой итог.</summary>
    public static UpsertDetails Empty { get; } = new(default, [], []);
}

/// <summary>
/// Итог подсчёта путей (<see cref="IFileIndexRepository.CountPathsAsync"/>).
/// </summary>
/// <param name="Matched">Подошло записей.</param>
/// <param name="Checked">Проверено записей.</param>
/// <param name="Complete">Проверены все записи; <c>false</c> — подсчёт остановлен по времени.</param>
public sealed record PathCount(long Matched, long Checked, bool Complete);

/// <summary>
/// Сводка по корню.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="FileCount">Файлов.</param>
/// <param name="DirectoryCount">Папок.</param>
/// <param name="TotalSize">Суммарный размер файлов в байтах.</param>
public sealed record IndexStats(long RootId, long FileCount, long DirectoryCount, long TotalSize)
{
    /// <summary>Файлов и папок вместе.</summary>
    public long EntryCount => FileCount + DirectoryCount;
}
