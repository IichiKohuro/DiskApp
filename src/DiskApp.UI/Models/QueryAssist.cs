using CommunityToolkit.Mvvm.ComponentModel;

namespace DiskApp.UI.Models;

/// <summary>Вид подсказки к запросу.</summary>
public enum QuerySuggestionKind
{
    /// <summary>Ключ фильтра: <c>ext:</c>.</summary>
    FilterKey,

    /// <summary>Значение фильтра: <c>ext:pdf</c>.</summary>
    FilterValue,

    /// <summary>Запрос из истории.</summary>
    History,
}

/// <summary>
/// Подсказка к запросу (PLAN.md, этап 7.5): дополнение фильтра или запрос из истории.
/// </summary>
/// <param name="Query">Полный текст запроса после выбора подсказки.</param>
/// <param name="Display">Что показать на чипе.</param>
/// <param name="Kind">Вид.</param>
public sealed record QuerySuggestion(string Query, string Display, QuerySuggestionKind Kind)
{
    /// <summary>Запрос из истории: его можно удалить.</summary>
    public bool IsHistory => Kind == QuerySuggestionKind.History;
}

/// <summary>Группа чипов фильтров: в группе активен не больше одного.</summary>
public enum FilterGroup
{
    /// <summary>Вид файла, <c>type:</c>.</summary>
    Kind,

    /// <summary>Дата изменения, <c>modified:</c>.</summary>
    Date,

    /// <summary>Корень, <c>root:</c>.</summary>
    Root,
}

/// <summary>
/// Чип фильтра над результатами: добавляет в запрос или убирает из него токен фильтра (<c>type:image</c>).
/// Состояние чипа следует из текста запроса, поэтому фильтр, набранный руками, тоже подсвечивает чип.
/// </summary>
public sealed partial class FilterChip(string label, string token, FilterGroup group) : ObservableObject
{
    /// <summary>Подпись.</summary>
    public string Label { get; } = label;

    /// <summary>Токен фильтра в запросе.</summary>
    public string Token { get; } = token;

    /// <summary>Группа.</summary>
    public FilterGroup Group { get; } = group;

    /// <summary>Ключ фильтра группы: <c>type</c>, <c>modified</c>, <c>root</c>.</summary>
    public string Key => Group switch
    {
        FilterGroup.Kind => "type",
        FilterGroup.Date => "modified",
        _ => "root",
    };

    /// <summary>Фильтр есть в запросе.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }
}

/// <summary>
/// Строка подсказки синтаксиса по запросу <c>?</c>.
/// </summary>
/// <param name="Example">Пример: <c>ext:pdf</c>, <c>Ctrl+Enter</c>.</param>
/// <param name="Description">Что делает; у заголовка — его текст.</param>
/// <param name="IsHeader">Заголовок раздела.</param>
public sealed record HelpItem(string Example, string Description, bool IsHeader = false);
