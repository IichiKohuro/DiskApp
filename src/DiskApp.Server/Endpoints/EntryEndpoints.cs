using System.Globalization;
using System.Text;

using DiskApp.Contracts;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Search;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Core.Results;
using DiskApp.Core.Search;
using DiskApp.Server.Localization;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Записи индекса (PLAN.md, этап 4.3): карточка, содержимое папки, обзор по пути внутри корней, содержимое файла
/// с Range, иконки и миниатюры, действия оболочки (открыть, показать в Проводнике и др.) и теги записи.
/// Действия и иконки выполняет платформенная реализация; без неё — 501.
/// </summary>
internal static class EntryEndpoints
{
    public const int MaxChildren = 5_000;

    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private delegate Task<Result> ShellAction(IPlatformShell shell, string path, CancellationToken cancellationToken);

    public static void MapEntries(this IEndpointRouteBuilder endpoints)
    {
        var entries = endpoints.MapGroup(string.Empty).WithTags("Entries");

        entries.MapGet(ApiRoutes.V1.EntryById, GetAsync)
            .WithName("GetEntry")
            .WithSummary("Запись индекса")
            .WithDescription("Файл или папка с полным путём, видом, ключом иконки, избранным, статистикой открытий и тегами.");

        entries.MapGet(ApiRoutes.V1.EntryChildren, GetChildrenAsync)
            .WithName("GetEntryChildren")
            .WithSummary("Содержимое папки")
            .WithDescription("Папки, затем файлы. sort — Name, Modified, Created, Size; desc; offset; limit — до 5 000.");

        entries.MapGet(ApiRoutes.V1.Browse, BrowseAsync)
            .WithName("Browse")
            .WithSummary("Содержимое папки по пути")
            .WithDescription("path — абсолютный путь внутри добавленных корней; путь вне корней — 403. Содержимое берётся из индекса.");

        entries.MapGet(ApiRoutes.V1.EntryContent, GetContentAsync)
            .WithName("GetEntryContent")
            .WithSummary("Содержимое файла")
            .WithDescription("Поток с поддержкой Range для удалённых клиентов. Облачные заглушки не отдаются, чтобы не запускать скачивание.");

        entries.MapGet(ApiRoutes.V1.EntryIcon, GetIconAsync)
            .WithName("GetEntryIcon")
            .WithSummary("Иконка записи (PNG)")
            .WithDescription("size — 16…256 пикселей (по умолчанию 32). Ответ кэшируется по ETag: ключ иконки и размер.");

        entries.MapGet(ApiRoutes.V1.EntryThumbnail, GetThumbnailAsync)
            .WithName("GetEntryThumbnail")
            .WithSummary("Миниатюра записи (PNG)")
            .WithDescription("size — 32…1024 пикселей (по умолчанию 256). Для облачных заглушек миниатюр нет.");

        MapAction(entries, ApiRoutes.V1.EntryOpen, "OpenEntry", "Открыть приложением по умолчанию",
            "Записывает открытие в статистику; параметр query (необязательный) — запрос, из которого открыли, попадёт в историю.",
            static (shell, path, ct) => shell.OpenAsync(path, ct), recordsUsage: true);
        MapAction(entries, ApiRoutes.V1.EntryOpenWith, "OpenEntryWith", "Открыть с помощью…", "Системный диалог выбора приложения.",
            static (shell, path, ct) => shell.OpenWithAsync(path, ct), recordsUsage: true);
        MapAction(entries, ApiRoutes.V1.EntryReveal, "RevealEntry", "Показать в Проводнике", "Открывает папку с выделенной записью.",
            static (shell, path, ct) => shell.RevealInExplorerAsync(path, ct), recordsUsage: false);
        MapAction(entries, ApiRoutes.V1.EntryProperties, "ShowEntryProperties", "Свойства", "Системное окно свойств.",
            static (shell, path, ct) => shell.ShowPropertiesAsync(path, ct), recordsUsage: false);
        MapAction(entries, ApiRoutes.V1.EntryRunAsAdmin, "RunEntryAsAdmin", "Запустить от имени администратора", "Запрос повышения прав Windows.",
            static (shell, path, ct) => shell.RunAsAdminAsync(path, ct), recordsUsage: true);

        entries.MapPost(ApiRoutes.V1.EntryTags, SetTagsAsync)
            .WithName("SetEntryTags")
            .WithSummary("Теги записи")
            .WithDescription("Заменяет набор тегов записи; неизвестный тег — 400.");

        entries.MapPost(ApiRoutes.V1.ResolveEntries, ResolveAsync)
            .WithName("ResolveEntries")
            .WithSummary("Записи по путям")
            .WithDescription("Записи индекса для списка путей (до 1 000): импорт списков и перетаскивание в коллекции. Пути вне корней и без записи в индексе возвращаются в Missing.");
    }

    private static async Task<Results<Ok<ResolveEntriesResponse>, ValidationProblem>> ResolveAsync(
        ResolveEntriesRequest request,
        IRootRepository roots,
        IFileIndexRepository entries,
        PathNormalizer paths,
        EntryLookup lookup,
        CancellationToken cancellationToken)
    {
        if (request.Paths.Count > ResolveEntriesRequest.MaxPaths)
        {
            return ApiProblems.Invalid("paths", L.F("EntryEndpoints.NoMoreThanPathsPerRequest", ResolveEntriesRequest.MaxPaths));
        }

        var all = await roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<FileEntry>();
        var seen = new HashSet<long>();
        var missing = new List<string>();
        foreach (var raw in request.Paths)
        {
            var entry = await FindByPathAsync(raw, all, entries, paths, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                missing.Add(raw);
            }
            else if (seen.Add(entry.Id))
            {
                found.Add(entry);
            }
        }

        var items = await lookup.ToDtosAsync(found, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new ResolveEntriesResponse(items, missing));
    }

    /// <summary>
    /// Запись индекса по абсолютному пути: самый глубокий корень, внутри которого лежит путь, и относительный путь в нём.
    /// Индекс хранит путь в настоящем регистре, поэтому для пути в другом регистре настоящее написание берётся с диска.
    /// </summary>
    private static async Task<FileEntry?> FindByPathAsync(string? raw, IReadOnlyList<Root> roots, IFileIndexRepository entries, PathNormalizer paths, CancellationToken cancellationToken)
    {
        if (!paths.TryNormalize(raw, out var normalized))
        {
            return null;
        }

        var root = RootNesting.OwnerOf(normalized, roots, paths);
        if (root is null || !paths.TryGetRelativePath(root.Path, normalized, out var relPath) || relPath.Length == 0)
        {
            return null;
        }

        return await entries.GetByPathAsync(root.Id, relPath, cancellationToken).ConfigureAwait(false)
            ?? (ActualCase(root.Path, relPath) is { } actual && !string.Equals(actual, relPath, StringComparison.Ordinal)
                ? await entries.GetByPathAsync(root.Id, actual, cancellationToken).ConfigureAwait(false)
                : null);
    }

    /// <summary>Относительный путь в настоящем регистре, как на диске; <c>null</c> — такого пути нет.</summary>
    private static string? ActualCase(string rootPath, string relPath)
    {
        var current = rootPath;
        var parts = new List<string>();
        foreach (var part in relPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string? match;
            try
            {
                // Шаблон без подстановочных знаков совпадает с одним именем, а сравнение в Windows не учитывает регистр.
                match = Directory.Exists(current) ? Directory.EnumerateFileSystemEntries(current, part).FirstOrDefault() : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }

            if (match is null)
            {
                return null;
            }

            parts.Add(Path.GetFileName(match));
            current = match;
        }

        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    private static async Task<Results<Ok<EntryDto>, ProblemHttpResult>> GetAsync(long id, EntryLookup lookup, CancellationToken cancellationToken)
        => await lookup.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } dto
            ? TypedResults.Ok(dto)
            : ApiProblems.NotFound(L.F("Entries.EntryNotFound", id));

    private static async Task<Results<Ok<BrowseResponse>, ValidationProblem, ProblemHttpResult>> GetChildrenAsync(
        long id,
        SearchSort? sort,
        bool? desc,
        int? offset,
        int? limit,
        EntryLookup lookup,
        IFileIndexRepository entries,
        CancellationToken cancellationToken)
    {
        if (ValidatePaging(offset, limit) is { } invalid)
        {
            return invalid;
        }

        var found = await lookup.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return ApiProblems.NotFound(L.F("Entries.EntryNotFound", id));
        }

        var (folder, root) = found.Value;

        if (!folder.IsDirectory)
        {
            return ApiProblems.Invalid("id", L.F("Entries.FileNotFolder", folder.Name));
        }

        return TypedResults.Ok(await ListAsync(root, folder, sort, desc, offset, limit, lookup, entries, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<BrowseResponse>, ValidationProblem, ProblemHttpResult>> BrowseAsync(
        string? path,
        SearchSort? sort,
        bool? desc,
        int? offset,
        int? limit,
        EntryLookup lookup,
        RootGuard guard,
        IFileIndexRepository entries,
        PathNormalizer paths,
        CancellationToken cancellationToken)
    {
        if (ValidatePaging(offset, limit) is { } invalid)
        {
            return invalid;
        }

        var (allowed, normalized, root) = await guard.CheckAsync(path, cancellationToken).ConfigureAwait(false);
        if (normalized is null)
        {
            return ApiProblems.Invalid("path", L.T("EntryEndpoints.AnAbsolutePathIsRequired"));
        }

        if (!allowed || root is null || !paths.TryGetRelativePath(root.Path, normalized, out var relPath))
        {
            return TypedResults.Problem(L.F("Entries.OutsideRoots", normalized), statusCode: StatusCodes.Status403Forbidden, title: L.T("Entries.AccessDenied"));
        }

        FileEntry? folder = null;
        if (relPath.Length > 0)
        {
            folder = await entries.GetByPathAsync(root.Id, relPath, cancellationToken).ConfigureAwait(false);
            if (folder is null)
            {
                return ApiProblems.NotFound(L.F("EntryEndpoints.FolderIsNotInTheIndex", normalized));
            }

            if (!folder.IsDirectory)
            {
                return ApiProblems.Invalid("path", L.F("Entries.FileNotFolder", folder.Name));
            }
        }

        return TypedResults.Ok(await ListAsync(root, folder, sort, desc, offset, limit, lookup, entries, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<BrowseResponse> ListAsync(
        Root root,
        FileEntry? folder,
        SearchSort? sort,
        bool? desc,
        int? offset,
        int? limit,
        EntryLookup lookup,
        IFileIndexRepository entries,
        CancellationToken cancellationToken)
    {
        var take = limit ?? 1_000;
        var children = await entries.GetChildrenAsync(
            root.Id,
            folder?.Id,
            sort is null or SearchSort.Relevance ? SearchSortField.Name : sort.Value.ToDomain(),
            desc ?? false,
            offset ?? 0,
            take + 1,
            cancellationToken).ConfigureAwait(false);

        var truncated = children.Count > take;
        var items = await lookup.ToDtosAsync(truncated ? children.Take(take).ToArray() : children, cancellationToken).ConfigureAwait(false);
        var folderDto = folder is null ? null : await lookup.GetAsync(folder.Id, cancellationToken).ConfigureAwait(false);
        return new BrowseResponse(folder is null ? root.Path : lookup.FullPath(folder, root), root.Id, folderDto, items, truncated);
    }

    private static ValidationProblem? ValidatePaging(int? offset, int? limit)
        => offset is < 0 ? ApiProblems.Invalid("offset", L.T("EntryEndpoints.TheOffsetCannotBeNegative"))
            : limit is < 1 or > MaxChildren ? ApiProblems.Invalid("limit", L.F("EntryEndpoints.EntriesPerPageFrom1To", MaxChildren))
            : null;

    private static async Task<Results<PhysicalFileHttpResult, ValidationProblem, ProblemHttpResult>> GetContentAsync(long id, EntryLookup lookup, CancellationToken cancellationToken)
    {
        var found = await lookup.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return ApiProblems.NotFound(L.F("Entries.EntryNotFound", id));
        }

        var (entry, root) = found.Value;

        if (entry.IsDirectory)
        {
            return ApiProblems.Invalid("id", L.T("EntryEndpoints.AFolderHasNoContentUse"));
        }

        if (entry.IsCloudPlaceholder)
        {
            return ApiProblems.Conflict("entry.cloud_placeholder", L.F("EntryEndpoints.FileIsStoredInTheCloud", entry.Name));
        }

        var path = lookup.FullPath(entry, root);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return ApiProblems.NotFound(L.F("EntryEndpoints.FileIsNoLongerOnDisk", path));
        }

        var contentType = ContentTypes.TryGetContentType(path, out var known) ? known : "application/octet-stream";
        return TypedResults.PhysicalFile(path, contentType, fileDownloadName: entry.Name, lastModified: info.LastWriteTimeUtc, enableRangeProcessing: true);
    }

    private static async Task<IResult> GetIconAsync(long id, int? size, HttpContext context, EntryLookup lookup, IServiceProvider services, CancellationToken cancellationToken)
    {
        var pixels = size ?? 32;
        if (Array.IndexOf(IconSizes, pixels) < 0)
        {
            return ApiProblems.Invalid("size", L.T("EntryEndpoints.IconSize") + string.Join(", ", IconSizes) + ".");
        }

        var found = await lookup.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return ApiProblems.NotFound(L.F("Entries.EntryNotFound", id));
        }

        var (entry, root) = found.Value;

        if (services.GetService<IIconProvider>() is not { } icons)
        {
            return ApiProblems.Unavailable(L.T("EntryEndpoints.GettingIcons"));
        }

        var key = DtoMapper.IconKey(entry);
        // Заголовки HTTP — только ASCII, а в ключе бывает кириллическое расширение («ext:бд»): ключ экранируется.
        var etag = new EntityTagHeaderValue("\"" + AsciiTag(key) + ":" + pixels.ToString(CultureInfo.InvariantCulture) + "\"");
        if (NotModified(context, etag))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        var image = key.StartsWith("ext:", StringComparison.Ordinal)
            ? await icons.GetIconForExtensionAsync(entry.Extension, pixels, cancellationToken).ConfigureAwait(false)
            : await icons.GetIconForFileAsync(lookup.FullPath(entry, root), pixels, cancellationToken).ConfigureAwait(false);
        return Png(context, image, etag);
    }

    private static async Task<IResult> GetThumbnailAsync(long id, int? size, HttpContext context, EntryLookup lookup, IServiceProvider services, CancellationToken cancellationToken)
    {
        var pixels = size ?? 256;
        if (pixels is < 32 or > 1024)
        {
            return ApiProblems.Invalid("size", L.T("EntryEndpoints.ThumbnailSizeIsFrom32To"));
        }

        var found = await lookup.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return ApiProblems.NotFound(L.F("Entries.EntryNotFound", id));
        }

        var (entry, root) = found.Value;

        if (services.GetService<IIconProvider>() is not { } icons)
        {
            return ApiProblems.Unavailable(L.T("EntryEndpoints.GettingThumbnails"));
        }

        if (entry.IsCloudPlaceholder)
        {
            return ApiProblems.NotFound(L.F("EntryEndpoints.ACloudFileHasNoThumbnail", entry.Name));
        }

        var etag = new EntityTagHeaderValue("\"t" + entry.Id.ToString(CultureInfo.InvariantCulture) + "-" + entry.ModifiedUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)
            + "-" + pixels.ToString(CultureInfo.InvariantCulture) + "\"");
        if (NotModified(context, etag))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return Png(context, await icons.GetThumbnailAsync(lookup.FullPath(entry, root), pixels, cancellationToken).ConfigureAwait(false), etag);
    }

    // Видимые ASCII-символы остаются как есть, остальное и кавычки — %XX от UTF-8: заголовок принимает только ASCII.
    private static string AsciiTag(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is > 0x20 and < 0x7F and not '"' and not '\\' and not '%')
            {
                builder.Append((char)rune.Value);
            }
            else
            {
                builder.Append(Uri.EscapeDataString(rune.ToString()));
            }
        }

        return builder.ToString();
    }

    private static bool NotModified(HttpContext context, EntityTagHeaderValue etag)
        => context.Request.GetTypedHeaders().IfNoneMatch is { Count: > 0 } ifNoneMatch && ifNoneMatch.Any(tag => tag.Compare(etag, useStrongComparison: false));

    private static IResult Png(HttpContext context, RasterImage? image, EntityTagHeaderValue etag)
    {
        if (image is null)
        {
            return ApiProblems.NotFound(L.T("EntryEndpoints.ImageUnavailable"));
        }

        var headers = context.Response.GetTypedHeaders();
        headers.ETag = etag;
        headers.CacheControl = new CacheControlHeaderValue { Private = true, MaxAge = TimeSpan.FromDays(1) };
        return TypedResults.Bytes(image.Png, "image/png");
    }

    private static void MapAction(RouteGroupBuilder group, string pattern, string name, string summary, string description, ShellAction action, bool recordsUsage)
        => group.MapPost(pattern, (long id, string? query, EntryLookup lookup, IServiceProvider services, IUsageRepository usage, SearchService search, IClock clock, CancellationToken cancellationToken)
                => RunActionAsync(id, query, action, recordsUsage, lookup, services, usage, search, clock, cancellationToken))
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description + " Ошибка оболочки — 422 с кодом причины.");

    private static async Task<Results<NoContent, ProblemHttpResult>> RunActionAsync(
        long id,
        string? query,
        ShellAction action,
        bool recordsUsage,
        EntryLookup lookup,
        IServiceProvider services,
        IUsageRepository usage,
        SearchService search,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var found = await lookup.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return ApiProblems.NotFound(L.F("Entries.EntryNotFound", id));
        }

        var (entry, root) = found.Value;

        if (services.GetService<IPlatformShell>() is not { } shell)
        {
            return ApiProblems.Unavailable(L.T("EntryEndpoints.FileAction"));
        }

        var result = await action(shell, lookup.FullPath(entry, root), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ApiProblems.Domain(result.Error!, StatusCodes.Status422UnprocessableEntity);
        }

        if (recordsUsage)
        {
            await usage.RecordOpenAsync(entry.Id, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(query))
            {
                await search.RecordQueryAsync(query, cancellationToken).ConfigureAwait(false);
            }
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<EntryDto>, ValidationProblem, ProblemHttpResult>> SetTagsAsync(
        long id,
        EntryTagsRequest request,
        EntryLookup lookup,
        ITagRepository tags,
        IFileIndexRepository entries,
        CancellationToken cancellationToken)
    {
        var entry = await entries.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return ApiProblems.NotFound(L.F("Entries.EntryNotFound", id));
        }

        var known = (await tags.GetAllAsync(cancellationToken).ConfigureAwait(false)).Select(static tag => tag.Id).ToHashSet();
        var desired = request.TagIds.ToHashSet();
        var unknown = desired.Where(tagId => !known.Contains(tagId)).ToArray();
        if (unknown.Length > 0)
        {
            return ApiProblems.Invalid(nameof(request.TagIds), L.T("EntryEndpoints.UnknownTags") + string.Join(", ", unknown) + ".");
        }

        var current = (await tags.GetTagsOfEntryAsync(id, cancellationToken).ConfigureAwait(false)).Select(static tag => tag.Id).ToHashSet();
        foreach (var tagId in current.Except(desired))
        {
            await tags.UnassignAsync(id, tagId, cancellationToken).ConfigureAwait(false);
        }

        foreach (var tagId in desired.Except(current))
        {
            await tags.AssignAsync(id, tagId, cancellationToken).ConfigureAwait(false);
        }

        return TypedResults.Ok((await lookup.GetAsync(id, cancellationToken).ConfigureAwait(false))!);
    }
}
