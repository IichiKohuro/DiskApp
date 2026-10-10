using DiskApp.Core.Domain;

namespace DiskApp.Core.Indexing.Watching;

/// <summary>
/// Сырое событие файловой системы относительно корня, как его сообщает наблюдатель ОС.
/// </summary>
/// <param name="Kind">Что произошло.</param>
/// <param name="RelPath">Путь от корня (для переименования — новый).</param>
/// <param name="OldRelPath">Старый путь для <see cref="RawChangeKind.Renamed"/>; иначе <c>null</c>.</param>
public sealed record RawChange(RawChangeKind Kind, string RelPath, string? OldRelPath = null);

/// <summary>
/// Вид сырого события.
/// </summary>
public enum RawChangeKind
{
    /// <summary>Создан файл или папка (в том числе перенесён в корень извне).</summary>
    Created,

    /// <summary>Изменились содержимое, размер, время или атрибуты.</summary>
    Changed,

    /// <summary>Удалён (в том числе перенесён за пределы корня).</summary>
    Deleted,

    /// <summary>Переименован или перенесён внутри корня.</summary>
    Renamed,
}

/// <summary>
/// Наблюдатель за одним корнем (PLAN.md, этап 2.3). События приходят на потоках ОС и должны обрабатываться быстро.
/// </summary>
public interface IRootWatcher : IDisposable
{
    /// <summary>Событие файловой системы.</summary>
    event EventHandler<RawChange>? Changed;

    /// <summary>Сбой наблюдателя: переполнение буфера или потеря доступа к корню.</summary>
    event EventHandler<WatcherFailure>? Failed;

    /// <summary>Начинает получать события.</summary>
    void Start();
}

/// <summary>
/// Создаёт наблюдателей за корнями.
/// </summary>
public interface IRootWatcherFactory
{
    /// <summary>Наблюдатель за корнем; события начинают приходить после <see cref="IRootWatcher.Start"/>.</summary>
    IRootWatcher Create(Root root);
}

/// <summary>
/// Сбой наблюдателя.
/// </summary>
/// <param name="IsOverflow">Переполнился буфер событий: часть изменений потеряна, нужна сверка.</param>
/// <param name="Message">Текст ошибки.</param>
public sealed record WatcherFailure(bool IsOverflow, string Message);
