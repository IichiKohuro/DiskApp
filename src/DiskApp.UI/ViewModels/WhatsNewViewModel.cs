using DiskApp.UI.Localization;
using DiskApp.UI.Services;

using Symbol = FluentIcons.Common.Symbol;

namespace DiskApp.UI.ViewModels;

/// <summary>Пункт «Что нового».</summary>
/// <param name="Title">Заголовок.</param>
/// <param name="Text">Пояснение.</param>
/// <param name="Symbol">Значок.</param>
public sealed record WhatsNewItem(string Title, string Text, Symbol Symbol);

/// <summary>
/// «Что нового» после обновления (PLAN.md, этап 9.3): главное в текущей версии. Пункты — ключи
/// <c>WhatsNew.1.Title</c>, <c>WhatsNew.1.Text</c>, <c>WhatsNew.2.…</c> в ресурсах, поэтому список переводится вместе
/// с остальным интерфейсом.
/// </summary>
public sealed class WhatsNewViewModel
{
    private static readonly Symbol[] Symbols =
    [
        Symbol.Color, Symbol.Folder, Symbol.WindowMultiple, Symbol.Briefcase, Symbol.Alert, Symbol.Lightbulb, Symbol.Sparkle,
    ];

    /// <summary>Создаёт список для текущей версии.</summary>
    public WhatsNewViewModel()
    {
        var items = new List<WhatsNewItem>();
        for (var index = 1; index <= 20; index++)
        {
            var titleKey = $"WhatsNew.{index}.Title";
            var title = L.T(titleKey);
            if (title == titleKey)
            {
                break;
            }

            items.Add(new WhatsNewItem(title, L.T($"WhatsNew.{index}.Text"), Symbols[(index - 1) % Symbols.Length]));
        }

        Items = items;
    }

    /// <summary>Версия.</summary>
    public string Version { get; } = AppVersion.Current;

    /// <summary>Заголовок: «Что нового в 0.5.0».</summary>
    public string Title => L.F("WhatsNew.Title", Version);

    /// <summary>Пункты.</summary>
    public IReadOnlyList<WhatsNewItem> Items { get; }
}
