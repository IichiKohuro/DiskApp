using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Тег в диалоге выбора: отмечен у всех файлов, ни у одного или у части (<c>null</c>).
/// </summary>
public sealed partial class TagChoice : ObservableObject
{
    /// <summary>Создаёт вариант с начальным состоянием.</summary>
    public TagChoice(TagInfo tag, bool? isChecked)
    {
        ArgumentNullException.ThrowIfNull(tag);

        Tag = tag;
        Initial = isChecked;
        IsChecked = isChecked;
        IsThreeState = isChecked is null;
    }

    /// <summary>Тег.</summary>
    public TagInfo Tag { get; }

    /// <summary>Состояние при открытии диалога.</summary>
    public bool? Initial { get; }

    /// <summary>Третье состояние «у части файлов» доступно, только если оно было при открытии.</summary>
    public bool IsThreeState { get; }

    /// <summary>Отмечен у всех, ни у одного или у части (<c>null</c>).</summary>
    [ObservableProperty]
    public partial bool? IsChecked { get; set; }
}

/// <summary>
/// Диалог тегов (PLAN.md, этап 8.7): отметить теги одному или нескольким файлам, создать новый тег с цветом из палитры.
/// Тег, отмеченный у части файлов, остаётся как был, пока его не переключат.
/// </summary>
public sealed partial class TagPickerViewModel : ViewModelBase
{
    private readonly ILibraryService _library;

    /// <summary>
    /// Создаёт диалог для набора файлов.
    /// </summary>
    public TagPickerViewModel(IReadOnlyList<FileEntry> entries, ILibraryService library)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(library);

        Entries = entries;
        _library = library;
        TargetText = entries.Count == 1 ? $"Теги для «{entries[0].Name}»" : "Теги для " + Formatting.Count(entries.Count, "файла", "файлов", "файлов");
    }

    /// <summary>Файлы, которым назначаются теги.</summary>
    public IReadOnlyList<FileEntry> Entries { get; }

    /// <summary>Заголовок: для чего теги.</summary>
    public string TargetText { get; }

    /// <summary>Все теги с отметками.</summary>
    public ObservableCollection<TagChoice> Choices { get; } = [];

    /// <summary>Имя нового тега.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTagCommand))]
    public partial string NewTagName { get; set; } = string.Empty;

    /// <summary>Ошибка создания тега или загрузки; пусто — всё хорошо.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = string.Empty;

    /// <summary>Есть ошибка.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>Тегов ещё нет.</summary>
    public bool IsEmpty => Choices.Count == 0;

    /// <summary>
    /// Загружает теги и отмечает те, что уже стоят у файлов.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            var tags = await _library.GetTagsAsync();
            Choices.Clear();
            foreach (var tag in tags)
            {
                var count = Entries.Count(entry => entry.Tags.Contains(tag.Name, StringComparer.OrdinalIgnoreCase));
                Choices.Add(new TagChoice(tag, count == 0 ? false : count == Entries.Count ? true : null));
            }

            Error = string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = "Не удалось загрузить теги: " + exception.Message;
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Записывает теги файлам, у которых набор изменился.
    /// </summary>
    /// <returns>Файлы с новыми тегами (только изменённые).</returns>
    public async Task<IReadOnlyList<FileEntry>> ApplyAsync()
    {
        var updated = new List<FileEntry>();
        foreach (var entry in Entries)
        {
            var current = Choices.Where(choice => entry.Tags.Contains(choice.Tag.Name, StringComparer.OrdinalIgnoreCase))
                .Select(static choice => choice.Tag.Id)
                .ToHashSet();
            var desired = current.ToHashSet();
            foreach (var choice in Choices)
            {
                if (choice.IsChecked == true)
                {
                    desired.Add(choice.Tag.Id);
                }
                else if (choice.IsChecked == false)
                {
                    desired.Remove(choice.Tag.Id);
                }
            }

            if (!desired.SetEquals(current))
            {
                updated.Add(await _library.SetTagsAsync(entry, [.. desired.Order()]));
            }
        }

        return updated;
    }

    private bool CanAddTag() => NewTagName.Trim().Length > 0;

    /// <summary>Создаёт тег с цветом из палитры и сразу отмечает его.</summary>
    [RelayCommand(CanExecute = nameof(CanAddTag))]
    private async Task AddTagAsync()
    {
        var name = NewTagName.Trim();
        if (Choices.FirstOrDefault(choice => string.Equals(choice.Tag.Name, name, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            existing.IsChecked = true;
            NewTagName = string.Empty;
            return;
        }

        try
        {
            var tag = await _library.CreateTagAsync(name, TagPalette.Next(Choices.Select(static choice => choice.Tag.Color)));
            Choices.Add(new TagChoice(tag, isChecked: true));
            NewTagName = string.Empty;
            Error = string.Empty;
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
        }
    }
}
