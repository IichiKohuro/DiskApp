using DiskApp.UI.Models;

namespace DiskApp.UI.Input;

/// <summary>
/// Подсказка синтаксиса запроса и клавиш, которую показывает запрос <c>?</c> (PLAN.md, этап 7.5).
/// Фильтры совпадают с разбором на сервере (<c>QueryParser</c>, <c>FilterValues</c>), клавиши — с <see cref="QuickSearchKeyMap"/>.
/// </summary>
public static class SearchHelp
{
    /// <summary>Строки подсказки по порядку: заголовки разделов и примеры.</summary>
    public static IReadOnlyList<HelpItem> Items { get; } =
    [
        new(string.Empty, "Запрос", IsHeader: true),
        new("отчёт 2026", "все слова в имени, в любом порядке; регистр и «ё» не важны"),
        new("\"годовой отчёт\"", "точная фраза"),
        new("-черновик", "исключить слово; работает и с фильтрами: -ext:tmp"),
        new(string.Empty, "Фильтры", IsHeader: true),
        new("ext:pdf  ext:jpg,png", "расширение"),
        new("type:image", "вид: document, pdf, image, video, audio, archive, code, folder (и по-русски: фото, видео, папки)"),
        new("in:Проекты", "в папке с таким именем или по пути"),
        new("root:Документы", "в корне индекса"),
        new("modified:week", "изменён: today, yesterday, week, month, year, 3d, 2w, 2026-03, >2026-01-01"),
        new("created:2026", "создан — так же, как modified"),
        new("size:>10mb", "размер: 10mb, >1g, <500kb, 1mb..5mb, empty"),
        new("tag:работа  fav:yes", "тег; избранное"),
        new("is:dir  is:hidden", "папки, скрытые, облачные (is:cloud)"),
        new(string.Empty, "Клавиши", IsHeader: true),
        new("Enter", "открыть; Ctrl+1…9 — открыть строку с номером"),
        new(QuickSearchKeyMap.RevealText, "показать в папке"),
        new(QuickSearchKeyMap.OpenWithText, "открыть с помощью"),
        new(QuickSearchKeyMap.PropertiesText, "свойства"),
        new(QuickSearchKeyMap.CopyFilesText + " / " + QuickSearchKeyMap.CopyPathText, "копировать файл / путь"),
        new(QuickSearchKeyMap.RenameText + " / " + QuickSearchKeyMap.DeleteText, "переименовать / в корзину"),
        new(QuickSearchKeyMap.TogglePinText, "закрепить"),
        new("Tab", "дополнить фильтр по подсказке или открыть меню строки"),
        new("Ctrl+F", "показать или скрыть чипы фильтров"),
        new("↑ в пустом поле", "прошлые запросы"),
        new("Esc", "очистить запрос, затем закрыть окно"),
    ];
}
