namespace DiskApp.UI.Models;

/// <summary>
/// Секция подборки для пустого запроса: «Недавно открытые», «Закреплённые», «Недавно изменённые».
/// </summary>
/// <param name="Title">Заголовок секции.</param>
/// <param name="Entries">Записи секции в порядке показа.</param>
public sealed record ResultSection(string Title, IReadOnlyList<FileEntry> Entries);

/// <summary>
/// Строка-заголовок секции в списке результатов: не выбирается, клавиши её пропускают.
/// </summary>
/// <param name="Title">Заголовок.</param>
public sealed record ResultSectionHeader(string Title);
