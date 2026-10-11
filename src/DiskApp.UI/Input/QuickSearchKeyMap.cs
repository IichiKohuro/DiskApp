using Avalonia.Input;

namespace DiskApp.UI.Input;

/// <summary>Команда быстрого поиска, вызываемая клавишей или пунктом контекстного меню.</summary>
public enum QuickSearchCommand
{
    /// <summary>Следующая строка; <c>↓</c>.</summary>
    MoveDown,

    /// <summary>Предыдущая строка или, наверху при пустом запросе, прошлый запрос из истории; <c>↑</c>.</summary>
    MoveUp,

    /// <summary>На страницу вниз; <c>PgDn</c>.</summary>
    PageDown,

    /// <summary>На страницу вверх; <c>PgUp</c>.</summary>
    PageUp,

    /// <summary>Первая строка; <c>Ctrl+Home</c>, а при пустом поле — <c>Home</c>.</summary>
    First,

    /// <summary>Последняя строка; <c>Ctrl+End</c>, а при пустом поле — <c>End</c>.</summary>
    Last,

    /// <summary>Открыть; <c>Enter</c>.</summary>
    Open,

    /// <summary>Открыть строку с номером; <c>Ctrl+1…9</c>.</summary>
    OpenNumber,

    /// <summary>Показать в Проводнике; <c>Ctrl+Enter</c>.</summary>
    Reveal,

    /// <summary>«Открыть с помощью»; <c>Shift+Enter</c>.</summary>
    OpenWith,

    /// <summary>Свойства; <c>Alt+Enter</c>.</summary>
    Properties,

    /// <summary>Запустить от имени администратора; <c>Ctrl+Shift+Enter</c>.</summary>
    RunAsAdmin,

    /// <summary>Копировать файл; <c>Ctrl+C</c>, если в поле не выделен текст.</summary>
    CopyFiles,

    /// <summary>Копировать путь; <c>Ctrl+Shift+C</c>.</summary>
    CopyPath,

    /// <summary>Переименовать; <c>F2</c>.</summary>
    Rename,

    /// <summary>В корзину; <c>Del</c>, если курсор в конце поля и текст не выделен.</summary>
    Delete,

    /// <summary>Закрепить или открепить; <c>Ctrl+P</c>.</summary>
    TogglePin,

    /// <summary>Контекстное меню; <c>Tab</c> (если нет подсказки), <c>Shift+F10</c>, клавиша меню.</summary>
    ContextMenu,

    /// <summary>Дополнить запрос первой подсказкой; <c>Tab</c>, пока под полем видны подсказки к фильтру.</summary>
    AcceptSuggestion,

    /// <summary>Показать или скрыть чипы фильтров; <c>Ctrl+F</c>.</summary>
    ToggleFilters,

    /// <summary>Очистить запрос, отменить действие или закрыть окно; <c>Esc</c>.</summary>
    Escape,
}

/// <summary>
/// Состояние поля запроса: от него зависит, отдать ли клавишу полю (редактирование текста) или списку.
/// </summary>
/// <param name="IsEmpty">Поле пустое.</param>
/// <param name="HasSelection">В поле выделен текст.</param>
/// <param name="CaretAtEnd">Курсор в конце текста.</param>
/// <param name="HasSuggestions">Под полем видны подсказки к фильтру: <c>Tab</c> дополняет, а не открывает меню.</param>
public readonly record struct QueryBoxState(bool IsEmpty, bool HasSelection, bool CaretAtEnd, bool HasSuggestions = false);

/// <summary>
/// Команда, найденная по клавише.
/// </summary>
/// <param name="Command">Команда.</param>
/// <param name="Number">Номер строки для <see cref="QuickSearchCommand.OpenNumber"/>.</param>
public readonly record struct QuickSearchKey(QuickSearchCommand Command, int Number = 0);

/// <summary>
/// Карта клавиш быстрого поиска (PLAN.md, этап 7.2) — единственное место, где клавиши связаны с командами;
/// на этапе 9 станет настраиваемой. Фокус всегда в поле запроса, поэтому клавиши, нужные для редактирования текста
/// (<c>Home</c>/<c>End</c>, <c>Del</c>, <c>Ctrl+C</c> с выделением), остаются полю, когда оно ими пользуется.
/// </summary>
public static class QuickSearchKeyMap
{
    /// <summary>Подписи сочетаний для меню и подсказок.</summary>
    public static IReadOnlyDictionary<QuickSearchCommand, KeyGesture> Gestures { get; } = new Dictionary<QuickSearchCommand, KeyGesture>
    {
        [QuickSearchCommand.Open] = new(Key.Enter),
        [QuickSearchCommand.Reveal] = new(Key.Enter, KeyModifiers.Control),
        [QuickSearchCommand.OpenWith] = new(Key.Enter, KeyModifiers.Shift),
        [QuickSearchCommand.Properties] = new(Key.Enter, KeyModifiers.Alt),
        [QuickSearchCommand.RunAsAdmin] = new(Key.Enter, KeyModifiers.Control | KeyModifiers.Shift),
        [QuickSearchCommand.CopyFiles] = new(Key.C, KeyModifiers.Control),
        [QuickSearchCommand.CopyPath] = new(Key.C, KeyModifiers.Control | KeyModifiers.Shift),
        [QuickSearchCommand.Rename] = new(Key.F2),
        [QuickSearchCommand.Delete] = new(Key.Delete),
        [QuickSearchCommand.TogglePin] = new(Key.P, KeyModifiers.Control),
    };

    /// <summary>Подпись клавиши пункта «Открыть».</summary>
    public static string OpenText => GestureText(QuickSearchCommand.Open);

    /// <summary>Подпись клавиши пункта «Показать в папке».</summary>
    public static string RevealText => GestureText(QuickSearchCommand.Reveal);

    /// <summary>Подпись клавиши пункта «Открыть с помощью».</summary>
    public static string OpenWithText => GestureText(QuickSearchCommand.OpenWith);

    /// <summary>Подпись клавиши пункта «Свойства».</summary>
    public static string PropertiesText => GestureText(QuickSearchCommand.Properties);

    /// <summary>Подпись клавиши пункта «Запустить от имени администратора».</summary>
    public static string RunAsAdminText => GestureText(QuickSearchCommand.RunAsAdmin);

    /// <summary>Подпись клавиши пункта «Копировать».</summary>
    public static string CopyFilesText => GestureText(QuickSearchCommand.CopyFiles);

    /// <summary>Подпись клавиши пункта «Копировать путь».</summary>
    public static string CopyPathText => GestureText(QuickSearchCommand.CopyPath);

    /// <summary>Подпись клавиши пункта «Переименовать».</summary>
    public static string RenameText => GestureText(QuickSearchCommand.Rename);

    /// <summary>Подпись клавиши пункта «В корзину».</summary>
    public static string DeleteText => GestureText(QuickSearchCommand.Delete);

    /// <summary>Подпись клавиши пункта «Закрепить».</summary>
    public static string TogglePinText => GestureText(QuickSearchCommand.TogglePin);

    /// <summary>
    /// Подпись сочетания, как её пишет Windows: «Ctrl+Enter», «Del»; пусто, если у команды нет сочетания.
    /// </summary>
    public static string GestureText(QuickSearchCommand command)
    {
        if (!Gestures.TryGetValue(command, out var gesture))
        {
            return string.Empty;
        }

        var parts = new List<string>(4);
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        parts.Add(gesture.Key switch
        {
            Key.Enter => "Enter",
            Key.Delete => "Del",
            _ => gesture.Key.ToString(),
        });
        return string.Join('+', parts);
    }

    /// <summary>
    /// Команда для клавиши или <c>null</c>, если клавишу надо отдать полю ввода.
    /// </summary>
    public static QuickSearchKey? Resolve(Key key, KeyModifiers modifiers, QueryBoxState box)
    {
        var ctrl = modifiers == KeyModifiers.Control;
        var none = modifiers == KeyModifiers.None;

        return key switch
        {
            Key.Down when none => new(QuickSearchCommand.MoveDown),
            Key.Up when none => new(QuickSearchCommand.MoveUp),
            Key.PageDown when none => new(QuickSearchCommand.PageDown),
            Key.PageUp when none => new(QuickSearchCommand.PageUp),
            Key.Home when ctrl || (none && box.IsEmpty) => new(QuickSearchCommand.First),
            Key.End when ctrl || (none && box.IsEmpty) => new(QuickSearchCommand.Last),

            Key.Enter when none => new(QuickSearchCommand.Open),
            Key.Enter when ctrl => new(QuickSearchCommand.Reveal),
            Key.Enter when modifiers == KeyModifiers.Shift => new(QuickSearchCommand.OpenWith),
            Key.Enter when modifiers == KeyModifiers.Alt => new(QuickSearchCommand.Properties),
            Key.Enter when modifiers == (KeyModifiers.Control | KeyModifiers.Shift) => new(QuickSearchCommand.RunAsAdmin),

            Key.C when ctrl && !box.HasSelection => new(QuickSearchCommand.CopyFiles),
            Key.C when modifiers == (KeyModifiers.Control | KeyModifiers.Shift) => new(QuickSearchCommand.CopyPath),
            Key.F2 when none => new(QuickSearchCommand.Rename),
            Key.Delete when none && box.CaretAtEnd && !box.HasSelection => new(QuickSearchCommand.Delete),
            Key.P when ctrl => new(QuickSearchCommand.TogglePin),

            Key.Tab when none && box.HasSuggestions => new(QuickSearchCommand.AcceptSuggestion),
            Key.Tab when none => new(QuickSearchCommand.ContextMenu),
            Key.F when ctrl => new(QuickSearchCommand.ToggleFilters),
            Key.F10 when modifiers == KeyModifiers.Shift => new(QuickSearchCommand.ContextMenu),
            Key.Apps when none => new(QuickSearchCommand.ContextMenu),

            >= Key.D1 and <= Key.D9 when ctrl => new(QuickSearchCommand.OpenNumber, key - Key.D0),
            >= Key.NumPad1 and <= Key.NumPad9 when ctrl => new(QuickSearchCommand.OpenNumber, key - Key.NumPad0),

            Key.Escape when none => new(QuickSearchCommand.Escape),
            _ => null,
        };
    }
}
