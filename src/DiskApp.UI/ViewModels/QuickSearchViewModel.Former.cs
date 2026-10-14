using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DiskApp.Contracts.History;
using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Секция «Было» (PLAN.md, этап 20.5): после поиска по словам ищет их в истории файлов — в прежних именах перенесённых и
/// переименованных и в именах удалённых — и показывает под результатами, куда файл делся; файл, который поиск и так
/// нашёл под нынешним именем, не повторяется.
/// </summary>
public sealed partial class QuickSearchViewModel
{
    /// <summary>Строк в секции «Было».</summary>
    public const int FormerLimit = 5;

    private static readonly FileEventKind[] FormerKinds = [FileEventKind.Moved, FileEventKind.Renamed, FileEventKind.Recycled, FileEventKind.Deleted];

    private readonly IFileHistoryService? _fileHistory;
    private int _formerVersion;

    /// <summary>Файлы, найденные по прежнему имени.</summary>
    public ObservableCollection<FormerFileViewModel> Former { get; } = [];

    /// <summary>Секция «Было» видна.</summary>
    [ObservableProperty]
    public partial bool HasFormer { get; set; }

    /// <summary>Последний поиск по истории (тесты).</summary>
    public Task PendingFormer { get; private set; } = Task.CompletedTask;

    /// <summary>Слова запроса без фильтров, кавычек и исключений: по ним ищется прежнее имя.</summary>
    public static IReadOnlyList<string> FormerWords(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return [.. query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => !token.StartsWith('-') && !token.Contains(':', StringComparison.Ordinal))
            .Select(static token => token.Trim('"', '\''))
            .Where(static word => word.Length > 0)];
    }

    private void ClearFormer()
    {
        _formerVersion++;
        Former.Clear();
        HasFormer = false;
        PendingFormer = Task.CompletedTask;
    }

    private void RefreshFormer(string query, IReadOnlyList<FileEntry> found)
    {
        ClearFormer();
        var words = FormerWords(query);

        // Одна-две буквы находят в истории почти всё — как и поиск, ждём хотя бы трёх символов.
        if (_fileHistory is null || words.Count == 0 || words.Max(static word => word.Length) < 3)
        {
            return;
        }

        PendingFormer = LoadFormerAsync(words, [.. found.Select(static entry => entry.FullPath)], _formerVersion);
    }

    private async Task LoadFormerAsync(IReadOnlyList<string> words, HashSet<string> found, int version)
    {
        try
        {
            var page = await _fileHistory!.GetAsync(new FileHistoryFilter { Text = string.Join(' ', words), Kinds = FormerKinds, Limit = 50 });
            if (version != _formerVersion)
            {
                return;
            }

            var now = _clock();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in page.Items)
            {
                // Новые сверху: у файла остаётся последнее, что с ним случилось под этим именем.
                var former = item.Kind is FileEventKind.Moved or FileEventKind.Renamed ? item.OldPath ?? item.Path : item.Path;
                if (!seen.Add(former) || found.Contains(item.Path) || (item.Kind is FileEventKind.Moved or FileEventKind.Renamed && Matches(item.Name, words)))
                {
                    continue;
                }

                Former.Add(new FormerFileViewModel(item, now, GoToFormer, RevertFormerAsync));
                if (Former.Count == FormerLimit)
                {
                    break;
                }
            }

            HasFormer = Former.Count > 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // История недоступна — поиск работает без секции «Было».
            _ = exception;
        }
    }

    // Нынешнее имя тоже подходит под запрос — файл уже в результатах поиска.
    private static bool Matches(string name, IReadOnlyList<string> words)
        => words.All(word => Fold(name).Contains(Fold(word), StringComparison.Ordinal));

    private static string Fold(string text) => text.ToLowerInvariant().Replace('ё', 'е');

    private void GoToFormer(FormerFileViewModel item) => Query = "\"" + item.Event.Name + "\"";

    private async Task RevertFormerAsync(FormerFileViewModel item)
    {
        try
        {
            await _fileHistory!.RevertAsync(item.Event.Id);
            Former.Remove(item);
            HasFormer = Former.Count > 0;
            ShowNotice(item.Event.Kind == FileEventKind.Recycled ? L.T("FileHistory.Restored") : L.T("FileHistory.PutBackDone"), item.Name, FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowNotice(L.T("FileHistory.CouldNotRevert"), exception.Message, FAInfoBarSeverity.Error);
        }
    }
}
