using CommunityToolkit.Mvvm.Input;

using DiskApp.Contracts.History;
using DiskApp.UI.Localization;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentIcons.Common;

using PathNames = DiskApp.UI.Text.PathText;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Строка секции «Было» под результатами поиска (PLAN.md, этап 20.5): файл, найденный по прежнему имени, — куда он
/// делся («Перенесён в «Акты» · вчера», «Переименован в «…»», «В корзине с 12 окт») и что с этим сделать: найти на
/// новом месте, вернуть или восстановить.
/// </summary>
public sealed class FormerFileViewModel
{
    /// <summary>Создаёт строку.</summary>
    /// <param name="item">Последнее событие этого файла.</param>
    /// <param name="now">Сейчас — для «вчера», «3 дн назад».</param>
    /// <param name="goTo">Найти файл на новом месте.</param>
    /// <param name="revert">Вернуть на место или восстановить.</param>
    public FormerFileViewModel(FileHistoryEvent item, DateTimeOffset now, Action<FormerFileViewModel> goTo, Func<FormerFileViewModel, Task> revert)
    {
        ArgumentNullException.ThrowIfNull(goTo);
        ArgumentNullException.ThrowIfNull(revert);
        Event = item ?? throw new ArgumentNullException(nameof(item));
        var when = Formatting.RelativeTime(item.Time, now);
        Text = item.Kind switch
        {
            FileEventKind.Moved => L.F("Former.MovedTo", PathNames.FileName(PathNames.DirectoryName(item.Path)), when),
            FileEventKind.Renamed => L.F("Former.RenamedTo", item.Name, when),
            FileEventKind.Recycled => L.F("Former.InRecycleBin", when),
            _ => L.F("Former.Deleted", when),
        };
        GoToCommand = new RelayCommand(() => goTo(this), () => CanGoTo);
        RevertCommand = new AsyncRelayCommand(() => revert(this), () => CanRevert);
    }

    /// <summary>Событие.</summary>
    public FileHistoryEvent Event { get; }

    /// <summary>Каким файл был: прежнее имя у переноса и переименования, имя у удалённого.</summary>
    public string Name => Event.Kind is FileEventKind.Moved or FileEventKind.Renamed ? Event.OldName ?? Event.Name : Event.Name;

    /// <summary>Куда делся и когда.</summary>
    public string Text { get; }

    /// <summary>Прежний и нынешний путь для подсказки.</summary>
    public string PathText => Event.OldPath is { } old ? old + " → " + Event.Path : Event.Path;

    /// <summary>Значок: перенос, переименование или корзина.</summary>
    public Symbol Symbol => Event.Kind switch
    {
        FileEventKind.Moved => Symbol.ArrowMove,
        FileEventKind.Renamed => Symbol.Rename,
        _ => Symbol.Delete,
    };

    /// <summary>Файл есть на новом месте: его можно найти.</summary>
    public bool CanGoTo => Event.Kind is FileEventKind.Moved or FileEventKind.Renamed;

    /// <summary>Можно вернуть или восстановить.</summary>
    public bool CanRevert => Event.CanRevert;

    /// <summary>«Восстановить» для корзины, иначе «Вернуть на место».</summary>
    public string RevertText => Event.Kind == FileEventKind.Recycled ? L.T("FileHistory.Restore") : L.T("FileHistory.PutBack");

    /// <summary>Найти файл на новом месте.</summary>
    public IRelayCommand GoToCommand { get; }

    /// <summary>Вернуть на место или восстановить.</summary>
    public IAsyncRelayCommand RevertCommand { get; }
}
