using DiskApp.Contracts.App;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Operations;
using DiskApp.Contracts.Roots;
using DiskApp.Contracts.Search;
using DiskApp.Core.Domain;
using DiskApp.Core.Events;
using DiskApp.Core.Indexing;
using DiskApp.Core.Operations;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Core.Search;
using DiskApp.Core.Settings;

using Contract = DiskApp.Contracts;

namespace DiskApp.Server.Mapping;

/// <summary>
/// Перевод доменных типов в DTO контрактов и обратно. Перечисления контрактов повторяют доменные по именам и порядку
/// (проверяется тестом), поэтому переводятся приведением.
/// </summary>
internal static class DtoMapper
{
    private static readonly HashSet<string> OwnIconExtensions = new(StringComparer.OrdinalIgnoreCase) { "exe", "lnk", "ico", "url", "appref-ms" };

    public static RootDto ToDto(this Root root) => new(
        root.Id,
        root.Path,
        root.Label,
        root.IsEnabled,
        root.IncludeHidden,
        root.IncludeSystem,
        root.MaxDepth,
        root.LastScanUtc,
        root.Status.ToDto(),
        root.ErrorMessage);

    public static Contract.Roots.RootStatus ToDto(this Core.Domain.RootStatus status) => (Contract.Roots.RootStatus)(int)status;

    public static Contract.Roots.IndexerState ToDto(this Core.Indexing.IndexerState state) => (Contract.Roots.IndexerState)(int)state;

    public static IndexStatusDto ToIndexStatusDto(this IndexerService indexer)
        => new(indexer.GetAggregateState().ToDto(), indexer.IsPaused, indexer.GetStatuses().Select(static status => status.ToDto()).ToArray());

    public static RootIndexStatusDto ToDto(this RootIndexStatus status) => new(
        status.RootId,
        status.Path,
        status.Label,
        status.Status.ToDto(),
        status.Message,
        status.RunningTask is { } task ? (Contract.Roots.IndexTaskKind)(int)task : null,
        status.IsQueued,
        status.Progress?.ToDto(),
        status.LastScanUtc);

    public static IndexProgressDto ToDto(this IndexProgressEvent progress) => new(
        progress.RootId,
        (Contract.Roots.IndexPhase)(int)progress.Phase,
        progress.Scanned,
        progress.Written,
        progress.CurrentPath,
        progress.RatePerSecond,
        progress.Elapsed.TotalSeconds);

    public static RootStatusChangedDto ToDto(this RootStatusChangedEvent change) => new(change.RootId, change.Status.ToDto(), change.Message);

    public static EntriesChangedDto ToDto(this EntriesChangedEvent changed)
        => new(changed.RootId, changed.Changes.Select(static change => new EntryChangeDto((Contract.Entries.EntryChangeKind)(int)change.Kind, change.RelPath, change.EntryId, change.IsDirectory)).ToArray());

    /// <summary>
    /// Запись индекса; полный путь — от пути корня.
    /// </summary>
    public static EntryDto ToDto(this FileEntry entry, string rootPath, PathNormalizer paths, bool isFavorite = false, UsageStat? usage = null) => new()
    {
        Id = entry.Id,
        RootId = entry.RootId,
        ParentId = entry.ParentId,
        Name = entry.Name,
        FullPath = entry.GetFullPath(rootPath, paths),
        RelPath = entry.RelPath,
        Extension = entry.Extension,
        IsDirectory = entry.IsDirectory,
        Size = entry.Size,
        CreatedUtc = entry.CreatedUtc,
        ModifiedUtc = entry.ModifiedUtc,
        Kind = (Contract.Entries.FileKind)(int)entry.Kind,
        IconKey = IconKey(entry),
        IsHidden = entry.IsHidden,
        IsCloudPlaceholder = entry.IsCloudPlaceholder,
        IsFavorite = isFavorite,
        OpenCount = usage?.OpenCount ?? 0,
        LastOpenedUtc = usage?.LastOpenedUtc,
    };

    /// <summary>
    /// Результат поиска: запись с оценкой, типом совпадения и подсветкой.
    /// </summary>
    public static EntryDto ToDto(this SearchHit hit)
    {
        var entry = hit.Entry;
        return new EntryDto
        {
            Id = entry.Id,
            RootId = entry.RootId,
            ParentId = entry.ParentId,
            Name = entry.Name,
            FullPath = hit.FullPath,
            RelPath = entry.RelPath,
            Extension = entry.Extension,
            IsDirectory = entry.IsDirectory,
            Size = entry.Size,
            CreatedUtc = entry.CreatedUtc,
            ModifiedUtc = entry.ModifiedUtc,
            Kind = (Contract.Entries.FileKind)(int)entry.Kind,
            IconKey = IconKey(entry),
            IsHidden = entry.IsHidden,
            IsCloudPlaceholder = entry.IsCloudPlaceholder,
            IsFavorite = hit.IsFavorite,
            OpenCount = hit.OpenCount,
            LastOpenedUtc = hit.LastOpenedUtc,
            Score = hit.Score,
            Match = (Contract.Entries.MatchKind)(int)hit.Match,
            NameHighlights = hit.NameHighlights.Select(static range => new TextRangeDto(range.Start, range.Length)).ToArray(),
            PathHighlights = hit.PathHighlights.Select(static range => new TextRangeDto(range.Start, range.Length)).ToArray(),
            RankReason = hit.RankReason,
        };
    }

    /// <summary>
    /// Ключ иконки для кэша клиента: у программ, ярлыков и иконок она своя у каждого файла, у остальных — по расширению.
    /// </summary>
    public static string IconKey(FileEntry entry)
        => entry.IsDirectory ? "folder"
            : OwnIconExtensions.Contains(entry.Extension) ? "entry:" + entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : entry.Extension.Length > 0 ? "ext:" + entry.Extension.ToLowerInvariant()
            : "file";

    public static QueryTokenDto ToDto(this QueryToken token) => new((Contract.Search.QueryTokenKind)(int)token.Kind, token.Start, token.Length, token.Text);

    public static SuggestionDto ToDto(this SearchSuggestion suggestion) => new(suggestion.Query, suggestion.Display, (Contract.Search.SuggestionKind)(int)suggestion.Kind);

    public static HistoryEntryDto ToDto(this QueryHistoryEntry entry) => new(entry.Query, entry.UseCount, entry.LastUsedUtc);

    public static Core.Search.SearchSortField ToDomain(this SearchSort sort) => (Core.Search.SearchSortField)(int)sort;

    public static Core.Search.RankingPreset ToDomain(this Contract.Search.RankingPreset preset) => (Core.Search.RankingPreset)(int)preset;

    public static TagDto ToDto(this Tag tag) => new(tag.Id, tag.Name, tag.Color);

    public static FolderTagsDto ToDto(this FolderTags folder) => new(folder.Path, [.. folder.Tags.Select(static tag => tag.ToDto())]);

    public static Contracts.Notes.NoteDto ToDto(this Note note)
        => new(note.Id, note.Title, note.Body, note.IsPinned, note.CreatedUtc, note.ModifiedUtc, note.DeletedUtc, [.. note.Tags.Select(static tag => tag.ToDto())]);

    public static CollectionDto ToDto(this Collection collection) => new(collection.Id, collection.Name, (Contract.Library.CollectionKind)(int)collection.Kind, collection.Query);

    public static Core.Domain.CollectionKind ToDomain(this Contract.Library.CollectionKind kind) => (Core.Domain.CollectionKind)(int)kind;

    public static SettingsDto ToDto(this AppSettings settings) => new()
    {
        Hotkeys = new HotkeySettingsDto
        {
            QuickSearch = settings.Hotkeys.QuickSearch,
            Triage = settings.Hotkeys.Triage,
            QuickSwitch = settings.Hotkeys.QuickSwitch,
            MainWindow = settings.Hotkeys.MainWindow,
            DropWindow = settings.Hotkeys.DropWindow,
        },
        Appearance = new AppearanceSettingsDto
        {
            Theme = (Contract.App.ThemeMode)(int)settings.Appearance.Theme,
            Backdrop = (Contract.App.BackdropMode)(int)settings.Appearance.Backdrop,
            UseSystemAccent = settings.Appearance.UseSystemAccent,
            ReduceMotion = settings.Appearance.ReduceMotion,
            HardwareAcceleration = settings.Appearance.HardwareAcceleration,
            AccentColor = settings.Appearance.AccentColor,
            FontScale = settings.Appearance.FontScale,
            Compact = settings.Appearance.Compact,
            Language = settings.Appearance.Language,
        },
        Indexing = new IndexingSettingsDto
        {
            Exclusions = settings.Indexing.Exclusions.Select(static rule => new ExclusionRuleDto((Contract.App.ExclusionKind)(int)rule.Kind, rule.Pattern)).ToArray(),
            IncludeHiddenByDefault = settings.Indexing.IncludeHiddenByDefault,
            IncludeSystemByDefault = settings.Indexing.IncludeSystemByDefault,
            BatchSize = settings.Indexing.BatchSize,
            WatcherDebounceMilliseconds = settings.Indexing.WatcherDebounceMilliseconds,
            ReconcileIntervalMinutes = settings.Indexing.ReconcileIntervalMinutes,
            FollowReparsePoints = settings.Indexing.FollowReparsePoints,
            MaxScanParallelism = settings.Indexing.MaxScanParallelism,
        },
        Search = new SearchSettingsDto
        {
            PageSize = settings.Search.PageSize,
            CandidateLimit = settings.Search.CandidateLimit,
            HistoryLimit = settings.Search.HistoryLimit,
            FrecencyHalfLifeDays = settings.Search.FrecencyHalfLifeDays,
            RankingPreset = (Contract.Search.RankingPreset)(int)RankingWeights.ParsePreset(settings.Search.RankingPreset),
            Fuzzy = settings.Search.Fuzzy,
            ShowSuggestionsOnEmpty = settings.Search.ShowSuggestionsOnEmpty,
        },
        Tray = new TraySettingsDto
        {
            StartMinimized = settings.Tray.StartMinimized,
            CloseToTray = settings.Tray.CloseToTray,
            ConfirmExitDuringOperations = settings.Tray.ConfirmExitDuringOperations,
            AutoStart = settings.Tray.AutoStart,
        },
        Add = new AddSettingsDto
        {
            InboxPath = settings.Add.InboxPath,
            RecentTargets = settings.Add.RecentTargets,
            ClipboardNameTemplate = settings.Add.ClipboardNameTemplate,
        },
        Experimental = new ExperimentalSettingsDto { QuickSwitch = settings.Experimental.QuickSwitch },
        Popup = new PopupSettingsDto
        {
            Monitor = (Contract.App.PopupMonitor)(int)settings.Popup.Monitor,
            HideOnDeactivate = settings.Popup.HideOnDeactivate,
            ShowPath = settings.Popup.ShowPath,
            ShowSize = settings.Popup.ShowSize,
            ShowDate = settings.Popup.ShowDate,
        },
        Diagnostics = new DiagnosticsSettingsDto { LogLevel = settings.Diagnostics.LogLevel },
        Onboarding = new OnboardingSettingsDto
        {
            Completed = settings.Onboarding.Completed,
            LastSeenVersion = settings.Onboarding.LastSeenVersion,
            TipsShown = settings.Onboarding.TipsShown,
        },
        Sorting = new SortingSettingsDto
        {
            Inboxes = [.. settings.Sorting.Inboxes.Select(static folder => new SortingFolderDto { Path = folder.Path, Label = folder.Label, IncludeSubfolders = folder.IncludeSubfolders })],
            WorkFolders = [.. settings.Sorting.WorkFolders.Select(static folder => folder.ToDto())],
            NameTemplate = settings.Sorting.NameTemplate,
            MainInbox = settings.Sorting.MainInbox,
            SettleSeconds = settings.Sorting.SettleSeconds,
            IgnorePatterns = settings.Sorting.IgnorePatterns,
        },
        History = new HistorySettingsDto { RetentionDays = settings.History.RetentionDays, MaxSizeMegabytes = settings.History.MaxSizeMegabytes },
        Notifications = new NotificationSettingsDto
        {
            AutoSort = settings.Notifications.AutoSort,
            Operations = settings.Notifications.Operations,
            Index = settings.Notifications.Index,
            SortingReminders = settings.Notifications.SortingReminders,
            Persistence = (Contract.App.NotificationPersistence)(int)settings.Notifications.Persistence,
            QuietHours = settings.Notifications.QuietHours,
            QuietFrom = settings.Notifications.QuietFrom,
            QuietTo = settings.Notifications.QuietTo,
            RespectWindowsDoNotDisturb = settings.Notifications.RespectWindowsDoNotDisturb,
            DoNotDisturbUntil = settings.Notifications.DoNotDisturbUntil,
        },
    };

    /// <summary>Событие истории файлов (этап 20).</summary>
    public static Contract.History.FileEventDto ToDto(this Core.History.FileEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new Contract.History.FileEventDto(
            item.Id,
            item.TimeUtc,
            Enum.Parse<Contract.History.FileEventKind>(item.Kind.ToString()),
            Enum.Parse<Contract.History.FileEventSource>(item.Source.ToString()),
            item.Path,
            item.OldPath,
            item.Name,
            item.OldName,
            item.IsDirectory,
            item.Size,
            item.OperationId,
            item.Details,
            CanRevert: item.Kind is Core.History.FileEventKind.Recycled || (item.Kind is Core.History.FileEventKind.Moved or Core.History.FileEventKind.Renamed && item.OldPath is not null));
    }

    /// <summary>
    /// Настройки из DTO поверх текущих: версия схемы и поля, которых нет в контракте, сохраняются.
    /// </summary>
    public static AppSettings ToDomain(this SettingsDto dto, AppSettings current) => current with
    {
        Hotkeys = current.Hotkeys with
        {
            QuickSearch = dto.Hotkeys.QuickSearch,
            Triage = dto.Hotkeys.Triage,
            QuickSwitch = dto.Hotkeys.QuickSwitch,
            MainWindow = dto.Hotkeys.MainWindow,
            DropWindow = dto.Hotkeys.DropWindow,
        },
        Appearance = current.Appearance with
        {
            Theme = (Core.Settings.ThemeMode)(int)dto.Appearance.Theme,
            Backdrop = (Core.Settings.BackdropMode)(int)dto.Appearance.Backdrop,
            UseSystemAccent = dto.Appearance.UseSystemAccent,
            ReduceMotion = dto.Appearance.ReduceMotion,
            HardwareAcceleration = dto.Appearance.HardwareAcceleration,
            AccentColor = dto.Appearance.AccentColor,
            FontScale = dto.Appearance.FontScale,
            Compact = dto.Appearance.Compact,
            Language = dto.Appearance.Language,
        },
        Indexing = current.Indexing with
        {
            Exclusions = dto.Indexing.Exclusions.Select(static rule => new ExclusionRule((Core.Domain.ExclusionKind)(int)rule.Kind, rule.Pattern ?? string.Empty)).ToArray(),
            IncludeHiddenByDefault = dto.Indexing.IncludeHiddenByDefault,
            IncludeSystemByDefault = dto.Indexing.IncludeSystemByDefault,
            BatchSize = dto.Indexing.BatchSize,
            WatcherDebounceMilliseconds = dto.Indexing.WatcherDebounceMilliseconds,
            ReconcileIntervalMinutes = dto.Indexing.ReconcileIntervalMinutes,
            FollowReparsePoints = dto.Indexing.FollowReparsePoints,
            MaxScanParallelism = dto.Indexing.MaxScanParallelism,
        },
        Search = current.Search with
        {
            PageSize = dto.Search.PageSize,
            CandidateLimit = dto.Search.CandidateLimit,
            HistoryLimit = dto.Search.HistoryLimit,
            FrecencyHalfLifeDays = dto.Search.FrecencyHalfLifeDays,
            RankingPreset = PresetName(dto.Search.RankingPreset),
            Fuzzy = dto.Search.Fuzzy,
            ShowSuggestionsOnEmpty = dto.Search.ShowSuggestionsOnEmpty,
        },
        Tray = current.Tray with
        {
            StartMinimized = dto.Tray.StartMinimized,
            CloseToTray = dto.Tray.CloseToTray,
            ConfirmExitDuringOperations = dto.Tray.ConfirmExitDuringOperations,
            AutoStart = dto.Tray.AutoStart,
        },
        Add = current.Add with
        {
            InboxPath = string.IsNullOrWhiteSpace(dto.Add.InboxPath) ? null : dto.Add.InboxPath,
            RecentTargets = dto.Add.RecentTargets ?? [],
            ClipboardNameTemplate = dto.Add.ClipboardNameTemplate,
        },
        Experimental = current.Experimental with { QuickSwitch = dto.Experimental.QuickSwitch },
        Popup = current.Popup with
        {
            Monitor = (Core.Settings.PopupMonitor)(int)dto.Popup.Monitor,
            HideOnDeactivate = dto.Popup.HideOnDeactivate,
            ShowPath = dto.Popup.ShowPath,
            ShowSize = dto.Popup.ShowSize,
            ShowDate = dto.Popup.ShowDate,
        },
        Diagnostics = current.Diagnostics with { LogLevel = dto.Diagnostics.LogLevel },
        Onboarding = current.Onboarding with
        {
            Completed = dto.Onboarding.Completed,
            LastSeenVersion = string.IsNullOrWhiteSpace(dto.Onboarding.LastSeenVersion) ? null : dto.Onboarding.LastSeenVersion,
            TipsShown = dto.Onboarding.TipsShown ?? [],
        },
        Sorting = new SortingSettings
        {
            Inboxes = ToSortingFolders(dto.Sorting.Inboxes),
            WorkFolders = ToSortingFolders(dto.Sorting.WorkFolders),
            NameTemplate = string.IsNullOrWhiteSpace(dto.Sorting.NameTemplate) ? null : dto.Sorting.NameTemplate.Trim(),
            MainInbox = string.IsNullOrWhiteSpace(dto.Sorting.MainInbox) ? null : dto.Sorting.MainInbox,
            SettleSeconds = dto.Sorting.SettleSeconds,
            IgnorePatterns = [.. (dto.Sorting.IgnorePatterns ?? []).Select(static pattern => pattern?.Trim() ?? string.Empty)],
        },
        History = dto.History is { } history
            ? current.History with { RetentionDays = history.RetentionDays, MaxSizeMegabytes = history.MaxSizeMegabytes }
            : current.History,
        Notifications = dto.Notifications is { } notifications
            ? new NotificationSettings
            {
                AutoSort = notifications.AutoSort,
                Operations = notifications.Operations,
                Index = notifications.Index,
                SortingReminders = notifications.SortingReminders,
                Persistence = (Core.Settings.NotificationPersistence)(int)notifications.Persistence,
                QuietHours = notifications.QuietHours,
                QuietFrom = notifications.QuietFrom?.Trim() ?? string.Empty,
                QuietTo = notifications.QuietTo?.Trim() ?? string.Empty,
                RespectWindowsDoNotDisturb = notifications.RespectWindowsDoNotDisturb,
                DoNotDisturbUntil = notifications.DoNotDisturbUntil,
            }
            : current.Notifications,
    };

    /// <summary>Папки разбора из DTO: пути и подписи без пробелов по краям, шаблоны — без пустых условий.</summary>
    public static SortingFolder[] ToSortingFolders(IReadOnlyList<SortingFolderDto>? folders)
        => [.. (folders ?? []).Select(static folder => new SortingFolder
        {
            Path = folder.Path.Trim(),
            Label = string.IsNullOrWhiteSpace(folder.Label) ? null : folder.Label.Trim(),
            IncludeSubfolders = folder.IncludeSubfolders,
            Rules = [.. (folder.Rules ?? []).Select(static rule => new SortingRule
            {
                Pattern = rule.Pattern?.Trim() ?? string.Empty,
                Mode = (Core.Settings.SortingRuleMode)(int)rule.Mode,
                Enabled = rule.Enabled,
                Inbox = string.IsNullOrWhiteSpace(rule.Inbox) ? null : rule.Inbox.Trim(),
                Extensions = [.. (rule.Extensions ?? []).Select(static extension => Core.Sorting.SortingRuleSet.NormalizeExtension(extension ?? string.Empty)).Where(static extension => extension.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
                Subfolder = Core.Sorting.SortingRuleSet.SubfolderPath(rule.Subfolder),
            })],
        })];

    /// <summary>Рабочая папка с шаблонами в DTO.</summary>
    public static SortingFolderDto ToDto(this SortingFolder folder) => new()
    {
        Path = folder.Path,
        Label = folder.Label,
        IncludeSubfolders = folder.IncludeSubfolders,
        Rules = [.. folder.Rules.Select(static rule => new SortingRuleDto
        {
            Pattern = rule.Pattern,
            Mode = (Contract.App.SortingRuleMode)(int)rule.Mode,
            Enabled = rule.Enabled,
            Inbox = rule.Inbox,
            Extensions = rule.Extensions,
            Subfolder = rule.Subfolder,
        })],
    };

    public static FileOperationRequest ToDomain(this OperationRequest request) => new(
        (FileOperationKind)(int)request.Kind,
        request.Sources ?? [],
        request.Destination,
        request.NewName,
        (Core.Operations.ConflictPolicy)(int)request.Conflicts,
        request.Permanent);

    public static OperationDto ToDto(this FileOperationInfo info) => new(
        info.Id,
        (OperationKind)(int)info.Kind,
        (Contract.Operations.OperationState)(int)info.State,
        info.Sources,
        info.Destination,
        info.CreatedUtc,
        info.CompletedUtc,
        info.Progress.ToDto(info.Id),
        info.Message,
        info.CanUndo,
        info.PendingConflict is { } conflict ? conflict.ToDto() : null)
    {
        Origin = (Contract.Operations.OperationOrigin)(int)info.Origin,
    };

    public static OperationProgressDto ToDto(this OperationProgress progress, Guid operationId) => new(
        operationId,
        progress.ProcessedItems,
        progress.TotalItems,
        progress.ProcessedBytes,
        progress.TotalBytes,
        progress.CurrentPath,
        progress.BytesPerSecond,
        progress.Remaining?.TotalSeconds);

    public static OperationProgressDto ToDto(this OperationProgressEvent progress) => new(
        progress.OperationId,
        progress.ProcessedItems,
        progress.TotalItems,
        progress.ProcessedBytes,
        progress.TotalBytes,
        progress.CurrentPath,
        progress.BytesPerSecond,
        progress.Remaining?.TotalSeconds);

    public static ConflictDto ToDto(this OperationConflict conflict) => new(
        conflict.OperationId, conflict.SourcePath, conflict.TargetPath, conflict.SourceSize, conflict.SourceModifiedUtc, conflict.TargetSize, conflict.TargetModifiedUtc);

    public static ConflictDto ToDto(this OperationNeedsDecisionEvent conflict) => new(
        conflict.OperationId, conflict.SourcePath, conflict.TargetPath, conflict.SourceSize, conflict.SourceModifiedUtc, conflict.TargetSize, conflict.TargetModifiedUtc);

    public static OperationCompletedDto ToDto(this OperationCompletedEvent completed) => new(
        completed.OperationId,
        completed.Outcome switch
        {
            OperationOutcome.Completed => Contract.Operations.OperationState.Completed,
            OperationOutcome.Cancelled => Contract.Operations.OperationState.Cancelled,
            _ => Contract.Operations.OperationState.Failed,
        },
        completed.Message);

    private static string PresetName(Contract.Search.RankingPreset preset) => preset switch
    {
        Contract.Search.RankingPreset.ByName => "name",
        Contract.Search.RankingPreset.Recent => "recent",
        _ => "balanced",
    };
}
