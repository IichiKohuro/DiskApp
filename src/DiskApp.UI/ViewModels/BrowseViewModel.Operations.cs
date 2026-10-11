using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Models;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>Шаблон нового файла.</summary>
/// <param name="Label">Подпись в меню.</param>
/// <param name="Extension">Расширение с точкой.</param>
public sealed record NewFileTemplate(string Label, string Extension);

/// <summary>
/// Файловые операции «Обзора» (PLAN.md, этап 8.4): вырезать, копировать и вставить (`Ctrl+X/C/V`), новая папка
/// (`Ctrl+Shift+N`) и файл по шаблону, перенос перетаскиванием. Пути внутри корней переносит очередь сервера
/// (с конфликтами и отменой результата), файлы извне копируются загрузкой.
/// </summary>
public sealed partial class BrowseViewModel
{
    private IReadOnlyList<string> _clipboard = [];
    private bool _clipboardCut;

    /// <summary>Шаблоны новых файлов.</summary>
    public IReadOnlyList<NewFileTemplate> FileTemplates { get; } =
    [
        new("Текстовый документ", ".txt"),
        new("Заметка Markdown", ".md"),
        new("JSON", ".json"),
    ];

    /// <summary>В буфере «Обзора» есть что вставить.</summary>
    public bool HasClipboard => _clipboard.Count > 0;

    /// <summary>
    /// Вставляет в текущую папку: из буфера «Обзора» (вырезанное — перемещается), иначе файлы из системного буфера
    /// обмена, которые передаёт представление.
    /// </summary>
    /// <param name="systemFiles">Файлы из системного буфера обмена; <c>null</c> — нет.</param>
    public async Task PasteAsync(IReadOnlyList<string>? systemFiles = null)
    {
        if (CurrentPath is not { } target)
        {
            return;
        }

        var sources = _clipboard.Count > 0 ? _clipboard : systemFiles ?? [];
        if (sources.Count == 0)
        {
            _notices?.Show("Нечего вставить", "Скопируйте или вырежьте файлы в «Обзоре» или в Проводнике.", FAInfoBarSeverity.Informational);
            return;
        }

        var move = _clipboard.Count > 0 && _clipboardCut;
        await TransferAsync(sources, target, move);
        if (move)
        {
            _clipboard = [];
            _clipboardCut = false;
            OnPropertyChanged(nameof(HasClipboard));
        }
    }

    /// <summary>
    /// Переносит пути в папку: внутри корней — операцией сервера, извне — копированием загрузкой.
    /// </summary>
    /// <param name="sources">Что переносить.</param>
    /// <param name="targetFolder">Куда.</param>
    /// <param name="move">Переместить, а не скопировать.</param>
    public async Task TransferAsync(IReadOnlyList<string> sources, string targetFolder, bool move)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);

        var items = sources.Where(source => !IsSameOrParent(source, targetFolder)).ToList();
        if (items.Count == 0)
        {
            _notices?.Show("Некуда переносить", "Папку нельзя перенести в неё саму.", FAInfoBarSeverity.Warning);
            return;
        }

        var inside = items.Where(IsInsideRoots).ToList();
        var outside = items.Except(inside).ToList();
        if (inside.Count > 0)
        {
            if (_operations is null)
            {
                _notices?.Show("Операции недоступны", "Очередь операций сервера не подключена.", FAInfoBarSeverity.Warning);
            }
            else
            {
                await _operations.EnqueueAsync(new NewOperation(move ? FileOperationKind.Move : FileOperationKind.Copy, inside, targetFolder));
            }
        }

        if (outside.Count > 0)
        {
            var files = outside.Where(File.Exists).ToList();
            var saved = 0;
            if (files.Count > 0 && await TryAsync(async () => saved = await _actions.CopyIntoAsync(files, new FileEntry { FullPath = targetFolder, Kind = FileKind.Folder, Root = string.Empty }), "Не удалось скопировать"))
            {
                var skipped = outside.Count - files.Count;
                _notices?.Show(
                    "Скопировано",
                    $"{Formatting.Count(saved, "файл", "файла", "файлов")} в «{PathText.FileName(targetFolder)}»." + (skipped > 0 ? $" Папки извне пока не копируются: {skipped}." : string.Empty),
                    skipped > 0 ? FAInfoBarSeverity.Warning : FAInfoBarSeverity.Success);
                await RefreshAsync();
            }
        }
    }

    /// <summary>Вырезать выбранное: вставка переместит.</summary>
    [RelayCommand]
    private async Task CutAsync()
    {
        var entries = Selected();
        if (entries.Count == 0)
        {
            return;
        }

        _clipboard = [.. entries.Select(static entry => entry.FullPath)];
        _clipboardCut = true;
        OnPropertyChanged(nameof(HasClipboard));
        _notices?.Show("Вырезано", Formatting.Count(entries.Count, "элемент", "элемента", "элементов") + " — вставьте в нужную папку (Ctrl+V).", FAInfoBarSeverity.Informational);
        await Task.CompletedTask;
    }

    /// <summary>Вставить в текущую папку из буфера «Обзора».</summary>
    [RelayCommand]
    private Task PasteFromAppAsync() => PasteAsync();

    /// <summary>Создать папку в текущей папке (`Ctrl+Shift+N`).</summary>
    [RelayCommand]
    private Task NewFolderAsync() => CreateAsync(FileOperationKind.CreateFolder, "Новая папка", "Новая папка", string.Empty);

    /// <summary>Создать файл по шаблону.</summary>
    [RelayCommand]
    private Task NewFileAsync(NewFileTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return CreateAsync(FileOperationKind.CreateFile, "Новый файл", template.Label, template.Extension);
    }

    private async Task CreateAsync(FileOperationKind kind, string title, string baseName, string extension)
    {
        if (CurrentPath is not { } target || _dialogs is null)
        {
            return;
        }

        var initial = baseName + extension;
        var name = await _dialogs.PromptAsync(title, $"Имя в папке «{PathText.FileName(target)}»", initial, "Создать", baseName.Length);
        if (name is null)
        {
            return;
        }

        name = name.Trim();
        if (PathText.ValidateFileName(name) is { } error)
        {
            _notices?.Show("Такое имя не подходит", error, FAInfoBarSeverity.Warning);
            return;
        }

        if (_operations is null)
        {
            _notices?.Show("Операции недоступны", "Очередь операций сервера не подключена.", FAInfoBarSeverity.Warning);
            return;
        }

        await _operations.EnqueueAsync(new NewOperation(kind, [], target, name));
    }

    /// <summary>Показывает ошибку действия, начатого представлением (перетаскивание).</summary>
    public void ReportError(string title, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _notices?.Show(title, exception.Message, FAInfoBarSeverity.Error);
    }

    private void OnOperationFinished(object? sender, OperationInfo info)
    {
        if (CurrentPath is not { } current || info.State != FileOperationState.Completed)
        {
            return;
        }

        // Папка изменилась, если в неё положили или из неё унесли.
        var touched = string.Equals(info.Destination?.TrimEnd('\\', '/'), current, StringComparison.OrdinalIgnoreCase)
            || info.Sources.Any(source => string.Equals(PathText.DirectoryName(source), current, StringComparison.OrdinalIgnoreCase));
        if (touched && !IsLoading)
        {
            _ = NavigateAsync(current, pushHistory: false);
        }
    }

    private bool IsInsideRoots(string path)
        => _rootsById.Values.Any(root => path.StartsWith(root.Path.TrimEnd('\\', '/') + '\\', StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.Path.TrimEnd('\\', '/') + '/', StringComparison.OrdinalIgnoreCase));

    private static bool IsSameOrParent(string source, string target)
    {
        var from = source.TrimEnd('\\', '/');
        var to = target.TrimEnd('\\', '/');
        return string.Equals(from, to, StringComparison.OrdinalIgnoreCase)
            || to.StartsWith(from + '\\', StringComparison.OrdinalIgnoreCase)
            || string.Equals(PathText.DirectoryName(from), to, StringComparison.OrdinalIgnoreCase);
    }
}
