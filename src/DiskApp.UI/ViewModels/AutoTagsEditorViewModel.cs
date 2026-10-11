using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Поле «Авто-теги» (PLAN.md, этап 19): теги папки, которые получает всё внутри неё, — в карточке рабочей папки и в
/// диалоге «Авто-теги папки…» «Обзора». Чипы с крестиком, поле ввода и подсказки из библиотеки тегов; сохранение — по кнопке диалога.
/// </summary>
public sealed partial class AutoTagsEditorViewModel : ObservableObject
{
    /// <summary>Сколько авто-тегов у папки, как на сервере.</summary>
    public const int MaxTags = 20;

    private const int MaxSuggestions = 8;

    private readonly IReadOnlyList<TagInfo> _known;
    private readonly HashSet<string> _initial;

    /// <summary>
    /// Создаёт поле.
    /// </summary>
    /// <param name="tags">Авто-теги папки сейчас.</param>
    /// <param name="known">Все теги библиотеки — для подсказок и цветов.</param>
    /// <param name="inherited">Авто-теги папок выше: папка их уже получает, и это видно под полем.</param>
    public AutoTagsEditorViewModel(IEnumerable<TagInfo> tags, IReadOnlyList<TagInfo> known, IReadOnlyList<TagLabel>? inherited = null)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(known);

        _known = known;
        InheritedText = inherited is { Count: > 0 }
            ? L.F("AutoTags.InheritedFromAbove", string.Join(", ", inherited.Select(static tag => L.F("AutoTags.TagOfFolder", tag.Name, PathText.FileName(tag.Folder ?? string.Empty)))))
            : string.Empty;
        foreach (var tag in tags)
        {
            Tags.Add(new TagLabel(tag.Name, tag.Color));
        }

        _initial = new HashSet<string>(Tags.Select(static tag => tag.Name), StringComparer.OrdinalIgnoreCase);
        Tags.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTags));
            OnPropertyChanged(nameof(IsChanged));
            AddCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Авто-теги по порядку добавления.</summary>
    public ObservableCollection<TagLabel> Tags { get; } = [];

    /// <summary>Подходящие теги библиотеки к набранному тексту.</summary>
    public ObservableCollection<string> Suggestions { get; } = [];

    /// <summary>Набираемое имя тега.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>Что папка уже получает от папок выше: «уже от папок выше: ТЗ (417)»; пусто — ничего.</summary>
    public string InheritedText { get; }

    /// <summary>Есть авто-теги от папок выше.</summary>
    public bool HasInherited => InheritedText.Length > 0;

    /// <summary>Авто-теги есть.</summary>
    public bool HasTags => Tags.Count > 0;

    /// <summary>Авто-теги у папки были при открытии.</summary>
    public bool HadTags => _initial.Count > 0;

    /// <summary>Набор отличается от того, что был при открытии.</summary>
    public bool IsChanged => !_initial.SetEquals(Tags.Select(static tag => tag.Name));

    /// <summary>Имена авто-тегов для сохранения.</summary>
    public IReadOnlyList<string> Names => [.. Tags.Select(static tag => tag.Name)];

    /// <summary>Имена, которых нет в библиотеке: их создаёт сохранение.</summary>
    public IReadOnlyList<string> NewNames => [.. Names.Where(name => Find(name) is null)];

    /// <summary>
    /// Поле для папки: её авто-теги и то, что она уже получает от папок выше.
    /// </summary>
    /// <param name="library">Библиотека тегов.</param>
    /// <param name="path">Папка.</param>
    public static async Task<AutoTagsEditorViewModel> LoadAsync(ILibraryService library, string path)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var known = await library.GetTagsAsync();
        var folders = await library.GetAutoTagsAsync() ?? [];
        var own = folders.FirstOrDefault(folder => SamePath(folder.Path, path));
        return new AutoTagsEditorViewModel(own?.Tags ?? [], known, FolderTagsInfo.InheritedAt(folders.Where(folder => folder != own), path));
    }

    /// <summary>
    /// Сохраняет авто-теги папки: новые имена сначала становятся тегами с цветом из палитры — как в диалоге тегов.
    /// </summary>
    /// <param name="library">Библиотека тегов.</param>
    /// <param name="path">Папка.</param>
    public async Task<FolderTagsInfo> SaveAsync(ILibraryService library, string path)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var colors = _known.Select(static tag => tag.Color).ToList();
        foreach (var name in NewNames)
        {
            var color = TagPalette.Next(colors);
            await library.CreateTagAsync(name, color);
            colors.Add(color);
        }

        return await library.SetAutoTagsAsync(path, Names);
    }

    /// <summary>Добавить тег: из подсказки или набранный.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add(string? name)
    {
        var text = (name ?? Text).Trim().TrimStart('#').Trim();
        if (text.Length == 0 || Tags.Count >= MaxTags)
        {
            return;
        }

        if (!Tags.Any(tag => string.Equals(tag.Name, text, StringComparison.OrdinalIgnoreCase)))
        {
            // Имя из библиотеки пишется так, как там: «ТЗ», а не «тз».
            var known = Find(text);
            Tags.Add(new TagLabel(known?.Name ?? text, known?.Color));
        }

        Text = string.Empty;
    }

    private bool CanAdd(string? name) => Tags.Count < MaxTags && ((name ?? Text).Trim().TrimStart('#').Trim().Length > 0);

    /// <summary>Убрать тег.</summary>
    [RelayCommand]
    private void Remove(TagLabel? tag)
    {
        if (tag is not null)
        {
            Tags.Remove(tag);
        }
    }

    partial void OnTextChanged(string value)
    {
        Suggestions.Clear();
        var text = value.Trim().TrimStart('#');
        if (text.Length == 0)
        {
            return;
        }

        foreach (var tag in _known.Where(tag => tag.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                     && !Tags.Any(existing => string.Equals(existing.Name, tag.Name, StringComparison.OrdinalIgnoreCase))).Take(MaxSuggestions))
        {
            Suggestions.Add(tag.Name);
        }
    }

    private static bool SamePath(string left, string right)
        => string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private TagInfo? Find(string name) => _known.FirstOrDefault(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase));
}
