using DiskApp.Core.Indexing;
using DiskApp.Core.Paths;
using DiskApp.Core.Settings;

namespace DiskApp.Core.Sorting;

/// <summary>Чем кончился подбор рабочей папки по шаблонам (этап 17.5).</summary>
public enum SortingDecision
{
    /// <summary>Ни один шаблон не подошёл.</summary>
    None = 0,

    /// <summary>Подошла одна папка, но только «предлагать первой» (или это папка: её содержимое могло ещё дописываться).</summary>
    Suggest = 1,

    /// <summary>Подошла одна папка с шаблоном «переносить самим».</summary>
    Auto = 2,

    /// <summary>Подошли разные папки: файл сам не переносится, выбирает пользователь.</summary>
    Choice = 3,
}

/// <summary>Совпадение с шаблоном рабочей папки.</summary>
/// <param name="Folder">Рабочая папка.</param>
/// <param name="Rule">Подошедший шаблон.</param>
/// <param name="Destination">Куда класть: папка или её подпапка из шаблона, нормализованный путь.</param>
public sealed record SortingMatch(SortingFolder Folder, SortingRule Rule, string Destination);

/// <summary>Итог подбора для одного файла.</summary>
/// <param name="Matches">Совпадения: сначала «переносить самим», затем по порядку рабочих папок.</param>
/// <param name="Decision">Что делать.</param>
public sealed record SortingVerdict(IReadOnlyList<SortingMatch> Matches, SortingDecision Decision)
{
    /// <summary>Ни один шаблон не подошёл.</summary>
    public static SortingVerdict None { get; } = new([], SortingDecision.None);
}

/// <summary>
/// Шаблоны имён всех рабочих папок, разобранные один раз (PLAN.md, этап 17.5, ADR-0012): по имени файла, накопителю и
/// расширению находит, куда его положить. Выключенные шаблоны не действуют. Файл, который уже лежит в папке назначения
/// (рабочая папка внутри накопителя с подпапками), к ней не подходит.
/// </summary>
public sealed class SortingRuleSet
{
    private readonly PathNormalizer _paths;
    private readonly List<CompiledRule> _rules = [];

    /// <summary>
    /// Разбирает шаблоны рабочих папок.
    /// </summary>
    /// <param name="workFolders">Рабочие папки по порядку настроек.</param>
    /// <param name="paths">Правила путей и регистра.</param>
    public SortingRuleSet(IEnumerable<SortingFolder> workFolders, PathNormalizer paths)
    {
        ArgumentNullException.ThrowIfNull(workFolders);
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

        var ignoreCase = paths.Comparison == StringComparison.OrdinalIgnoreCase;
        foreach (var folder in workFolders)
        {
            if (!paths.TryNormalize(folder.Path, out var folderPath))
            {
                continue;
            }

            foreach (var rule in folder.Rules.Where(static rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.Pattern)))
            {
                var destination = SubfolderPath(rule.Subfolder) is { } subfolder ? paths.Combine(folderPath, subfolder) : folderPath;
                var inbox = rule.Inbox is { Length: > 0 } && paths.TryNormalize(rule.Inbox, out var normalizedInbox) ? normalizedInbox : null;
                var extensions = rule.Extensions.Select(NormalizeExtension).Where(static extension => extension.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _rules.Add(new CompiledRule(folder, rule, new GlobPattern(ToGlob(rule.Pattern), ignoreCase), inbox, extensions, destination));
            }
        }
    }

    /// <summary>Нет ни одного действующего шаблона.</summary>
    public bool IsEmpty => _rules.Count == 0;

    /// <summary>Есть действующие шаблоны «переносить самим».</summary>
    public bool HasAuto => _rules.Exists(static rule => rule.Rule.Mode == SortingRuleMode.Auto);

    /// <summary>
    /// Маска, по которой сравнивается имя: без <c>*</c> и <c>?</c> — «где угодно в имени».
    /// </summary>
    public static string ToGlob(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var trimmed = pattern.Trim();
        return trimmed.IndexOfAny(['*', '?']) >= 0 ? trimmed : "*" + trimmed + "*";
    }

    /// <summary>Расширение условия без точки и маски: <c>.PDF</c>, <c>*.pdf</c> → <c>pdf</c>.</summary>
    public static string NormalizeExtension(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return extension.Trim().TrimStart('*').TrimStart('.').Trim().ToLowerInvariant();
    }

    /// <summary>Подпапка шаблона с разделителями Windows, без разделителей по краям; <c>null</c> — подпапки нет.</summary>
    public static string? SubfolderPath(string? subfolder)
    {
        var trimmed = subfolder?.Replace('/', '\\').Trim().Trim('\\').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Куда положить файл или папку из накопителя.
    /// </summary>
    /// <param name="fullPath">Полный путь элемента.</param>
    /// <param name="name">Имя с расширением.</param>
    /// <param name="isFolder">Это папка: для неё — только предложение, расширений у неё нет.</param>
    /// <param name="inbox">Путь накопителя из настроек.</param>
    public SortingVerdict Match(string fullPath, string name, bool isFolder, string inbox)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(inbox);

        if (_rules.Count == 0)
        {
            return SortingVerdict.None;
        }

        var normalizedInbox = _paths.TryNormalize(inbox, out var inboxPath) ? inboxPath : inbox;
        var extension = isFolder ? string.Empty : NormalizeExtension(Path.GetExtension(name));
        var matches = new List<SortingMatch>();
        foreach (var rule in _rules)
        {
            if ((rule.Inbox is null || _paths.Comparer.Equals(rule.Inbox, normalizedInbox))
                && (rule.Extensions.Count == 0 || (!isFolder && rule.Extensions.Contains(extension)))
                && rule.Glob.IsMatch(name, name)
                && !_paths.IsNestedIn(fullPath, rule.Destination))
            {
                matches.Add(new SortingMatch(rule.Folder, rule.Rule, rule.Destination));
            }
        }

        if (matches.Count == 0)
        {
            return SortingVerdict.None;
        }

        // Самопереносящий шаблон — первым: он и есть причина, которую показывают пользователю.
        var ordered = matches.OrderByDescending(static match => match.Rule.Mode == SortingRuleMode.Auto).ToArray();
        var destinations = ordered.Select(static match => match.Destination).Distinct(_paths.Comparer).Count();
        var decision = destinations > 1
            ? SortingDecision.Choice
            : !isFolder && ordered[0].Rule.Mode == SortingRuleMode.Auto ? SortingDecision.Auto : SortingDecision.Suggest;
        return new SortingVerdict(destinations > 1 ? [.. matches] : ordered, decision);
    }

    /// <summary>Куда положить элемент очереди накопителей.</summary>
    public SortingVerdict Match(InboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Match(item.FullPath, item.Entry.Name, item.Entry.IsDirectory, item.Inbox);
    }

    /// <summary>
    /// Авторазбор перенесёт этот элемент сам: подошла одна папка с «переносить самим», файл пришёл и новый — не отложен,
    /// не «оставлен здесь» и не возвращён после автопереноса.
    /// </summary>
    public static bool WillAutoSort(InboxSnapshot snapshot, InboxItem item, SortingVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(verdict);
        return verdict.Decision == SortingDecision.Auto && item.State == InboxItemState.New && snapshot.IsReady(item);
    }

    private sealed record CompiledRule(SortingFolder Folder, SortingRule Rule, GlobPattern Glob, string? Inbox, HashSet<string> Extensions, string Destination);
}
