using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using Symbol = FluentIcons.Common.Symbol;

namespace DiskApp.UI.ViewModels;

/// <summary>Вариант места назначения в окне «Добавить».</summary>
/// <param name="Label">Подпись.</param>
/// <param name="Path">Полный путь.</param>
/// <param name="Hint">Откуда вариант: «недавнее», «закреплено», «Входящие».</param>
/// <param name="Symbol">Значок.</param>
public sealed record AddTarget(string Label, string Path, string Hint, Symbol Symbol);

/// <summary>
/// Окно «Добавить» (PLAN.md, этап 8.5): что добавляется, куда (недавние места, «Входящие», закреплённые папки,
/// поиск по папкам), копировать или переместить.
/// </summary>
public sealed partial class AddFilesViewModel : ViewModelBase
{
    private readonly ISearchService _search;
    private readonly IAddPreferences _preferences;
    private readonly IReadOnlyList<string> _rootPaths;
    private CancellationTokenSource? _searching;

    /// <summary>
    /// Создаёт окно для набора путей.
    /// </summary>
    /// <param name="sources">Что добавить.</param>
    /// <param name="search">Поиск папок.</param>
    /// <param name="preferences">«Входящие» и недавние места.</param>
    /// <param name="rootPaths">Пути корней: только внутри них можно переместить.</param>
    public AddFilesViewModel(IReadOnlyList<string> sources, ISearchService search, IAddPreferences preferences, IReadOnlyList<string> rootPaths)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(rootPaths);

        Sources = sources;
        _search = search;
        _preferences = preferences;
        _rootPaths = rootPaths;
        CanMove = sources.Count > 0 && sources.All(IsInsideRoots);
        SourcesText = Describe(sources);
    }

    /// <summary>Что добавляется.</summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>Описание: «3 файла и 1 папка» или имя одного файла.</summary>
    public string SourcesText { get; }

    /// <summary>Переместить можно, только если всё добавляемое уже лежит в корнях (иначе — копирование загрузкой).</summary>
    public bool CanMove { get; }

    /// <summary>Варианты места назначения: «Входящие», недавние, закреплённые или найденные папки.</summary>
    public ObservableCollection<AddTarget> Targets { get; } = [];

    /// <summary>Поиск папки по имени.</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>Выбранное место.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(TargetText))]
    public partial AddTarget? SelectedTarget { get; set; }

    /// <summary>Переместить вместо копирования.</summary>
    [ObservableProperty]
    public partial bool Move { get; set; }

    /// <summary>Можно добавлять: место выбрано.</summary>
    public bool CanAdd => SelectedTarget is not null;

    /// <summary>Куда: путь выбранного места.</summary>
    public string TargetText => SelectedTarget is { } target ? L.T("AddFiles.To") + target.Path : L.T("AddFiles.ChooseAFolder");

    /// <summary>Завершается, когда загружены начальные варианты (последний поиск).</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Загружает начальные варианты: «Входящие», недавние места, закреплённые папки.
    /// </summary>
    public Task LoadAsync()
    {
        PendingLoad = LoadDefaultsAsync();
        return PendingLoad;
    }

    partial void OnQueryChanged(string value)
    {
        PendingLoad = value.Trim().Length == 0 ? LoadDefaultsAsync() : SearchFoldersAsync(value.Trim());
    }

    private static string Describe(IReadOnlyList<string> sources)
    {
        if (sources.Count == 1)
        {
            return $"«{PathText.FileName(sources[0])}»";
        }

        var folders = sources.Count(Directory.Exists);
        var files = sources.Count - folders;
        var parts = new List<string>();
        if (files > 0)
        {
            parts.Add(Formatting.Count(files, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many")));
        }

        if (folders > 0)
        {
            parts.Add(Formatting.Count(folders, L.T("Common.Folder.One"), L.T("Common.Folder.Few"), L.T("Common.Folder.Many")));
        }

        return string.Join(L.T("AddFiles.And"), parts);
    }

    private async Task LoadDefaultsAsync()
    {
        var targets = new List<AddTarget>();
        if (_preferences.InboxPath is { Length: > 0 } inbox)
        {
            targets.Add(new AddTarget(L.T("AddFiles.Inbox"), inbox, L.T("AddFiles.DefaultPlace"), Symbol.MailInbox));
        }

        targets.AddRange(_preferences.RecentTargets.Select(static path => new AddTarget(PathText.FileName(path), path, L.T("AddFiles.Recent"), Symbol.History)));

        try
        {
            var sections = await _search.GetSuggestionsAsync(CancellationToken.None);
            targets.AddRange(sections
                .Where(static section => section.Title == SectionTitles.Pinned)
                .SelectMany(static section => section.Entries)
                .Where(static entry => entry.Kind == FileKind.Folder)
                .Select(static entry => new AddTarget(entry.Name, entry.FullPath, L.T("AddFiles.Pinned"), Symbol.Pin)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Без закреплённых папок окно всё равно работает: остаются недавние места и поиск.
            _ = exception;
        }

        if (targets.Count == 0)
        {
            targets.AddRange(_rootPaths.Select(static path => new AddTarget(PathText.FileName(path), path, L.T("AddFiles.Root"), Symbol.HardDrive)));
        }

        Show(targets);
    }

    private async Task SearchFoldersAsync(string text)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _searching, cts);
        if (previous is not null)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        try
        {
            var result = await _search.SearchAsync("is:dir " + text, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            Show([.. result.Entries
                .Where(static entry => entry.Kind == FileKind.Folder && !entry.IsOffline)
                .Select(static entry => new AddTarget(entry.Name, entry.FullPath, entry.Root, Symbol.Folder))]);
        }
        catch (OperationCanceledException)
        {
            // Пользователь продолжил ввод.
        }
        catch (Exception exception)
        {
            _ = exception;
            Show([]);
        }
    }

    private void Show(IReadOnlyList<AddTarget> targets)
    {
        var selected = SelectedTarget?.Path;
        Targets.Clear();
        foreach (var target in targets.DistinctBy(static target => target.Path, StringComparer.OrdinalIgnoreCase))
        {
            Targets.Add(target);
        }

        SelectedTarget = Targets.FirstOrDefault(target => string.Equals(target.Path, selected, StringComparison.OrdinalIgnoreCase)) ?? Targets.FirstOrDefault();
    }

    private bool IsInsideRoots(string path)
        => _rootPaths.Any(root => path.StartsWith(root.TrimEnd('\\', '/') + '\\', StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.TrimEnd('\\', '/') + '/', StringComparison.OrdinalIgnoreCase));
}
