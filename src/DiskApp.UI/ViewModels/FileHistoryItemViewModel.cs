using System.Globalization;

using CommunityToolkit.Mvvm.Input;

using DiskApp.Contracts.History;
using DiskApp.UI.Localization;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentIcons.Common;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Строка истории файлов (PLAN.md, этап 20.4): имя, что случилось словами, кто сделал, время и «Вернуть на место» или
/// «Восстановить».
/// </summary>
public sealed class FileHistoryItemViewModel
{
    /// <summary>Создаёт строку.</summary>
    /// <param name="item">Событие.</param>
    /// <param name="revert">«Вернуть на место» или «Восстановить»; без него кнопки нет.</param>
    public FileHistoryItemViewModel(FileHistoryEvent item, Func<FileHistoryItemViewModel, Task>? revert = null)
    {
        Event = item ?? throw new ArgumentNullException(nameof(item));
        CanRevert = item.CanRevert && revert is not null;
        RevertCommand = new AsyncRelayCommand(() => revert is null ? Task.CompletedTask : revert(this), () => CanRevert);
    }

    /// <summary>Событие.</summary>
    public FileHistoryEvent Event { get; }

    /// <summary>Имя файла или папки; у сводки индекса — корень.</summary>
    public string Name => Event.Kind == FileEventKind.IndexSummary ? Event.Path : Event.Name;

    /// <summary>Что случилось: «Перенесён: Загрузки → Акты», «Переименован, было «Смета.xlsx»».</summary>
    public string ActionText => Describe(Event);

    /// <summary>Кто сделал: «Проводник», «Разбор», «Авторазбор», «DiskApp», «Сверка».</summary>
    public string SourceText => SourceName(Event.Source);

    /// <summary>Время: «14:32».</summary>
    public string TimeText => Event.Time.ToString("t", CultureInfo.CurrentCulture);

    /// <summary>Где файл сейчас (или где был у удалённого) — полный путь для подсказки.</summary>
    public string PathText => Event.OldPath is { } old ? old + " → " + Event.Path : Event.Path;

    /// <summary>Можно вернуть или восстановить.</summary>
    public bool CanRevert { get; }

    /// <summary>«Вернуть на место» или «Восстановить».</summary>
    public IAsyncRelayCommand RevertCommand { get; }

    /// <summary>Подпись кнопки: «Восстановить» для корзины, иначе «Вернуть на место».</summary>
    public string RevertText => Event.Kind == FileEventKind.Recycled ? L.T("FileHistory.Restore") : L.T("FileHistory.PutBack");

    /// <summary>Значок вида события.</summary>
    public Symbol Symbol => Event.Kind switch
    {
        FileEventKind.Created => Event.IsDirectory ? Symbol.FolderAdd : Symbol.DocumentAdd,
        FileEventKind.Renamed => Symbol.Rename,
        FileEventKind.Moved => Symbol.ArrowMove,
        FileEventKind.Recycled or FileEventKind.Deleted => Symbol.Delete,
        FileEventKind.Restored => Symbol.ArrowUndo,
        FileEventKind.TagsChanged => Symbol.Tag,
        _ => Symbol.Library,
    };

    /// <summary>Кто сделал, словами.</summary>
    public static string SourceName(FileEventSource source) => source switch
    {
        FileEventSource.Sorting => L.T("FileHistory.Source.Sorting"),
        FileEventSource.AutoSort => L.T("FileHistory.Source.AutoSort"),
        FileEventSource.Explorer => L.T("FileHistory.Source.Explorer"),
        FileEventSource.Reconcile => L.T("FileHistory.Source.Reconcile"),
        _ => L.T("FileHistory.Source.DiskApp"),
    };

    /// <summary>Что случилось, словами.</summary>
    public static string Describe(FileHistoryEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Kind switch
        {
            FileEventKind.Created => L.F("FileHistory.CreatedIn", Folder(item.Path)),
            FileEventKind.Renamed => L.F("FileHistory.RenamedFrom", item.OldName ?? string.Empty),
            FileEventKind.Moved when item.OldName is { } old && !string.Equals(old, item.Name, StringComparison.Ordinal)
                => L.F("FileHistory.MovedRenamed", Folder(item.OldPath!), Folder(item.Path), old),
            FileEventKind.Moved => L.F("FileHistory.Moved", Folder(item.OldPath ?? item.Path), Folder(item.Path)),
            FileEventKind.Recycled => L.F("FileHistory.RecycledFrom", Folder(item.Path)),
            FileEventKind.Deleted => L.F("FileHistory.DeletedFrom", Folder(item.Path)),
            FileEventKind.Restored => L.F("FileHistory.RestoredTo", Folder(item.Path)),
            FileEventKind.TagsChanged => L.F("FileHistory.Tags", item.Details ?? string.Empty),
            _ => L.F("FileHistory.Indexed", Formatting.Count(item.Size, L.T("Common.Entry.One"), L.T("Common.Entry.Few"), L.T("Common.Entry.Many")), item.Details ?? string.Empty),
        };
    }

    /// <summary>Заголовок дня ленты: «Сегодня», «Вчера», «12 окт», «12.10.2024».</summary>
    public static string DayTitle(DateTime day, DateTimeOffset now)
    {
        if (day == now.Date)
        {
            return L.T("FileHistory.Today");
        }

        if (day == now.Date.AddDays(-1))
        {
            return L.T("FileHistory.Yesterday");
        }

        return day.Year == now.Year
            ? $"{day.Day.ToString(CultureInfo.CurrentCulture)} {L.T("Month.Short." + day.Month.ToString(CultureInfo.InvariantCulture))}"
            : day.ToString(L.T("Date.Full"), CultureInfo.CurrentCulture);
    }

    private static string Folder(string path) => Text.PathText.FileName(Text.PathText.DirectoryName(path));
}

/// <summary>День ленты истории: заголовок и события, новые сверху.</summary>
/// <param name="Title">«Сегодня», «Вчера», «12 окт».</param>
/// <param name="Items">События дня.</param>
public sealed record FileHistoryDayViewModel(string Title, IReadOnlyList<FileHistoryItemViewModel> Items);
