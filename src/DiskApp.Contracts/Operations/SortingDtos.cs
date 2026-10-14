using DiskApp.Contracts.App;
using DiskApp.Contracts.Entries;

namespace DiskApp.Contracts.Operations;

/// <summary>
/// Разбор из накопителя: <c>POST /api/v1/sorting/move</c>. Перенос встаёт в общую очередь операций (ответ — операция,
/// отменяется как любая другая); после него перенесённые элементы получают прежние теги и <see cref="Tags"/>.
/// </summary>
public sealed record SortFilesRequest
{
    /// <summary>Полные пути файлов и папок из накопителя.</summary>
    public IReadOnlyList<string> Sources { get; set; } = [];

    /// <summary>Рабочая папка или её подпапка.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Имя новой подпапки внутри <see cref="Destination"/>; пусто — переносить прямо в неё.</summary>
    public string? NewFolder { get; set; }

    /// <summary>Теги для перенесённых элементов.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>
    /// Новые имена по порядку <see cref="Sources"/> (с расширением); пусто или <c>null</c> — прежнее имя.
    /// </summary>
    public IReadOnlyList<string?>? Names { get; set; }
}

/// <summary>
/// Бросок файлов в папку-накопитель из окна Drag&amp;Drop: <c>POST /api/v1/sorting/drop</c> (PLAN.md, этап 18.2, ADR-0011).
/// Источники могут лежать вне добавленных корней (файл с рабочего стола, из мессенджера), назначение — только накопитель
/// из настроек; запрос принимается только с этого компьютера. Конфликт имён — «оставить оба».
/// </summary>
public sealed record DropFilesRequest
{
    /// <summary>Полные пути брошенных файлов и папок.</summary>
    public IReadOnlyList<string> Sources { get; set; } = [];

    /// <summary>Папка-накопитель.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Копировать; иначе — перенести (как в Проводнике: на том же диске — перенос, с другого — копия).</summary>
    public bool Copy { get; set; }
}

/// <summary>Состояние файла в папке-накопителе (PLAN.md, этап 17.1).</summary>
public enum InboxState
{
    /// <summary>Ждёт разбора.</summary>
    New,

    /// <summary>Отложен: в конце списка, не считается в бейдже.</summary>
    Postponed,

    /// <summary>«Оставить здесь»: больше не предлагается к разбору.</summary>
    Kept,
}

/// <summary>Почему накопитель не прочитан.</summary>
public enum InboxProblem
{
    /// <summary>Всё в порядке.</summary>
    None,

    /// <summary>Папка вне добавленных корней.</summary>
    OutsideIndex,

    /// <summary>Корень недоступен.</summary>
    Offline,

    /// <summary>Папки нет: переименована, перенесена или удалена.</summary>
    Missing,
}

/// <summary>
/// Очередь папок-накопителей: <c>GET /api/v1/sorting/inbox</c> (PLAN.md, этап 17.1). Файлы новые сверху; ещё дописываемые
/// (не прошло «время успокоения») приходят с <see cref="InboxItemDto.IsReady"/> = <c>false</c>, временные, служебные и
/// скрытые не приходят вовсе.
/// </summary>
/// <param name="Folders">Накопители по порядку настроек.</param>
/// <param name="Items">Файлы и папки накопителей.</param>
/// <param name="NewCount">Сколько ждут разбора: пришедшие и не отложенные.</param>
/// <param name="NextReadyUtc">Когда придёт следующий дописываемый файл — тогда стоит перечитать очередь.</param>
/// <param name="IsTruncated">В каком-то накопителе файлов больше 5 000 — показаны самые новые.</param>
public sealed record InboxDto(IReadOnlyList<InboxFolderDto> Folders, IReadOnlyList<InboxItemDto> Items, int NewCount, DateTimeOffset? NextReadyUtc, bool IsTruncated);

/// <summary>Накопитель в очереди.</summary>
/// <param name="Path">Путь из настроек.</param>
/// <param name="RootId">Корень, в который он входит: на его изменения подписываются в хабе.</param>
/// <param name="Problem">Почему не прочитан.</param>
public sealed record InboxFolderDto(string Path, long? RootId, InboxProblem Problem);

/// <summary>Файл или папка в накопителе.</summary>
/// <param name="Entry">Запись.</param>
/// <param name="Inbox">Путь накопителя из настроек.</param>
/// <param name="State">Состояние.</param>
/// <param name="IsReady">Файл пришёл: не меняется дольше «времени успокоения».</param>
/// <param name="ReadyUtc">С какого момента файл считается пришедшим.</param>
public sealed record InboxItemDto(EntryDto Entry, string Inbox, InboxState State, bool IsReady, DateTimeOffset ReadyUtc)
{
    /// <summary>Что подсказывают шаблоны рабочих папок (этап 17.5).</summary>
    public SortingDecision Decision { get; set; }

    /// <summary>Подошедшие шаблоны: куда класть и почему.</summary>
    public IReadOnlyList<SortingMatchDto> Matches { get; set; } = [];

    /// <summary>
    /// Подсказка по истории решений (этап 17.4), если шаблоны молчат; <c>null</c> — подсказать нечего.
    /// </summary>
    public SortingSuggestionDto? Suggestion { get; set; }
}

/// <summary>Почему подсказана папка (этап 17.4).</summary>
public enum SortingSuggestionReason
{
    /// <summary>Туда уже отправляли файлы с этим словом в имени.</summary>
    Word,

    /// <summary>Туда уже отправляли файлы с этим расширением.</summary>
    Extension,

    /// <summary>Туда недавно переносили из этого накопителя.</summary>
    Recent,
}

/// <summary>
/// Подсказка рабочей папки по истории решений разбора (этап 17.4): переносы пользователя по словам имени и расширению,
/// затем недавние переносы из того же накопителя; «оставить здесь» гасит подсказку.
/// </summary>
/// <param name="Destination">Куда: папка или подпапка.</param>
/// <param name="Reason">Почему.</param>
/// <param name="Count">Сколько файлов с этим признаком уже отправлено туда; для «недавно» — сколько переносов из накопителя.</param>
/// <param name="Feature">Слово, расширение без точки или, для «недавно», путь накопителя.</param>
public sealed record SortingSuggestionDto(string Destination, SortingSuggestionReason Reason, int Count, string Feature);

/// <summary>Чем кончился подбор рабочей папки по шаблонам (этап 17.5).</summary>
public enum SortingDecision
{
    /// <summary>Ни один шаблон не подошёл.</summary>
    None,

    /// <summary>Подошла одна папка — предложить её первой.</summary>
    Suggest,

    /// <summary>Подошла одна папка с «переносить самим» — авторазбор перенесёт файл, когда тот придёт.</summary>
    Auto,

    /// <summary>Подошли разные папки — выбирает пользователь, сам файл не переносится.</summary>
    Choice,
}

/// <summary>Подошедший шаблон рабочей папки.</summary>
/// <param name="WorkFolder">Путь рабочей папки из настроек.</param>
/// <param name="Destination">Куда класть: папка или подпапка из шаблона.</param>
/// <param name="Pattern">Маска шаблона, как задана.</param>
/// <param name="Mode">Режим шаблона.</param>
public sealed record SortingMatchDto(string WorkFolder, string Destination, string Pattern, SortingRuleMode Mode);

/// <summary>
/// Сухой прогон шаблонов: <c>POST /api/v1/sorting/rules/preview</c> (этап 17.5). Ничего не переносит — показывает, что куда
/// ушло бы из накопителей сейчас.
/// </summary>
public sealed record SortingPreviewRequest
{
    /// <summary>Рабочие папки с шаблонами, которые проверить (например, несохранённая карточка); <c>null</c> — из настроек.</summary>
    public IReadOnlyList<SortingFolderDto>? WorkFolders { get; set; }
}

/// <summary>Итог сухого прогона: файлы накопителей, к которым подошёл хоть один шаблон.</summary>
/// <param name="Items">Файлы, новые сверху.</param>
public sealed record SortingPreviewDto(IReadOnlyList<SortingPreviewItemDto> Items);

/// <summary>Файл накопителя в сухом прогоне.</summary>
/// <param name="Path">Полный путь.</param>
/// <param name="Name">Имя.</param>
/// <param name="Inbox">Путь накопителя из настроек.</param>
/// <param name="Decision">Что подсказывают шаблоны.</param>
/// <param name="WillMove">Авторазбор перенесёт файл сам: одна папка, «переносить самим», файл пришёл и новый.</param>
/// <param name="Matches">Подошедшие шаблоны.</param>
/// <param name="NewName">Имя после переноса по шаблону имени разбора; <c>null</c> — прежнее.</param>
public sealed record SortingPreviewItemDto(string Path, string Name, string Inbox, SortingDecision Decision, bool WillMove, IReadOnlyList<SortingMatchDto> Matches, string? NewName);

/// <summary>
/// Состояние файлов накопителя: <c>PUT /api/v1/sorting/inbox/state</c> — «отложить», «оставить здесь» или вернуть в новые.
/// </summary>
public sealed record SetInboxStateRequest
{
    /// <summary>Наибольшее число путей в запросе.</summary>
    public const int MaxPaths = 1_000;

    /// <summary>Полные пути файлов и папок внутри накопителей.</summary>
    public IReadOnlyList<string> Paths { get; set; } = [];

    /// <summary>Новое состояние.</summary>
    public InboxState State { get; set; }
}
