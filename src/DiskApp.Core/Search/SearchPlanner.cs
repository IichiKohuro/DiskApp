using System.Globalization;

using DiskApp.Core.Domain;
using DiskApp.Core.Localization;
using DiskApp.Core.Paths;

namespace DiskApp.Core.Search;

/// <summary>
/// Превращает разобранный запрос в план исполнения (PLAN.md, этап 3.2; ADR-0005): термы от трёх символов — в FTS,
/// короче — в префикс или подстроку; значения фильтров — в типизированные условия с границами дат в местном поясе;
/// <c>in:</c> и <c>root:</c> сопоставляются с корнями (по подписи, имени папки или абсолютному пути).
/// </summary>
public static class SearchPlanner
{
    /// <summary>Наименьшая длина терма для FTS5 trigram.</summary>
    public const int MinFullTextLength = 3;

    /// <summary>
    /// Строит план.
    /// </summary>
    /// <param name="query">Разобранный запрос.</param>
    /// <param name="roots">Корни индекса для <c>in:</c> и <c>root:</c>.</param>
    /// <param name="paths">Правила путей.</param>
    /// <param name="now">Текущий момент для относительных дат.</param>
    /// <param name="zone">Часовой пояс пользователя для границ дней.</param>
    public static SearchPlan Build(SearchQuery query, IReadOnlyList<Root> roots, PathNormalizer paths, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(zone);

        if (query.IsEmpty)
        {
            return query.Warnings.Count == 0 ? SearchPlan.Empty : SearchPlan.Empty with { Warnings = query.Warnings };
        }

        var fts = new List<string>();
        var shortTerms = new List<string>();
        var excluded = new List<string>();
        foreach (var term in query.Terms)
        {
            var folded = SearchText.Fold(SearchText.Compose(term.Text.Trim()));
            if (folded.Length == 0)
            {
                continue;
            }

            if (term.IsNegated)
            {
                AddDistinct(excluded, folded);
            }
            else if (folded.Length >= MinFullTextLength)
            {
                AddDistinct(fts, folded);
            }
            else
            {
                AddDistinct(shortTerms, folded);
            }
        }

        var warnings = new List<string>(query.Warnings);
        var builder = new FilterBuilder();
        foreach (var filter in query.Filters)
        {
            builder.Add(filter, roots, paths, now, zone, warnings);
        }

        return new SearchPlan
        {
            FtsTerms = fts,
            ShortTerms = shortTerms.OrderByDescending(static term => term.Length).ToArray(),
            ExcludedTerms = excluded,
            RegexPattern = query.RegexPattern,
            IncludeExtensions = builder.IncludeExtensions,
            ExcludeExtensions = builder.ExcludeExtensions,
            IncludeKinds = builder.IncludeKinds,
            ExcludeKinds = builder.ExcludeKinds,
            IncludeFolders = builder.IncludeFolders,
            ExcludeFolders = builder.ExcludeFolders,
            IncludeRoots = builder.IncludeRoots,
            ExcludeRoots = builder.ExcludeRoots,
            IncludeSizes = builder.IncludeSizes,
            ExcludeSizes = builder.ExcludeSizes,
            IncludeModified = builder.IncludeModified,
            ExcludeModified = builder.ExcludeModified,
            IncludeCreated = builder.IncludeCreated,
            ExcludeCreated = builder.ExcludeCreated,
            IncludeAttributes = builder.IncludeAttributes,
            ExcludeAttributes = builder.ExcludeAttributes,
            IncludeTags = builder.IncludeTags,
            ExcludeTags = builder.ExcludeTags,
            Favorite = builder.Favorite,
            Warnings = warnings,
        };
    }

    private static void AddDistinct<T>(List<T> list, T value)
    {
        if (!list.Contains(value))
        {
            list.Add(value);
        }
    }

    private sealed class FilterBuilder
    {
        public List<string> IncludeExtensions { get; } = [];

        public List<string> ExcludeExtensions { get; } = [];

        public List<FileKind> IncludeKinds { get; } = [];

        public List<FileKind> ExcludeKinds { get; } = [];

        public List<FolderScope> IncludeFolders { get; } = [];

        public List<FolderScope> ExcludeFolders { get; } = [];

        public List<long> IncludeRoots { get; } = [];

        public List<long> ExcludeRoots { get; } = [];

        public List<SizeRange> IncludeSizes { get; } = [];

        public List<SizeRange> ExcludeSizes { get; } = [];

        public List<DateRange> IncludeModified { get; } = [];

        public List<DateRange> ExcludeModified { get; } = [];

        public List<DateRange> IncludeCreated { get; } = [];

        public List<DateRange> ExcludeCreated { get; } = [];

        public List<AttributeFilter> IncludeAttributes { get; } = [];

        public List<AttributeFilter> ExcludeAttributes { get; } = [];

        public List<string> IncludeTags { get; } = [];

        public List<string> ExcludeTags { get; } = [];

        public bool? Favorite { get; private set; }

        public void Add(SearchFilter filter, IReadOnlyList<Root> roots, PathNormalizer paths, DateTimeOffset now, TimeZoneInfo zone, List<string> warnings)
        {
            var negated = filter.IsNegated;
            switch (filter.Key)
            {
                case SearchFilterKey.Extension:
                    foreach (var extension in FilterValues.ParseExtensions(filter.Value))
                    {
                        AddDistinct(negated ? ExcludeExtensions : IncludeExtensions, extension);
                    }

                    break;
                case SearchFilterKey.Kind when FilterValues.TryParseKinds(filter.Value, out var kinds):
                    foreach (var kind in kinds)
                    {
                        AddDistinct(negated ? ExcludeKinds : IncludeKinds, kind);
                    }

                    break;
                case SearchFilterKey.Size when FilterValues.TryParseSize(filter.Value, out var size):
                    AddDistinct(negated ? ExcludeSizes : IncludeSizes, size);
                    break;
                case SearchFilterKey.Modified when FilterValues.TryParseDate(filter.Value, now, zone, out var modified):
                    AddDistinct(negated ? ExcludeModified : IncludeModified, modified);
                    break;
                case SearchFilterKey.Created when FilterValues.TryParseDate(filter.Value, now, zone, out var created):
                    AddDistinct(negated ? ExcludeCreated : IncludeCreated, created);
                    break;
                case SearchFilterKey.Attribute when FilterValues.TryParseAttribute(filter.Value, out var attribute):
                    AddDistinct(negated ? ExcludeAttributes : IncludeAttributes, attribute);
                    break;
                case SearchFilterKey.Favorite when FilterValues.TryParseFavorite(filter.Value, out var favorite):
                    Favorite = negated ? !favorite : favorite;
                    break;
                case SearchFilterKey.Tag:
                    AddDistinct(negated ? ExcludeTags : IncludeTags, SearchText.Fold(SearchText.Compose(filter.Value.Trim())));
                    break;
                case SearchFilterKey.Folder:
                    AddFolder(filter.Value, negated, roots, paths, warnings);
                    break;
                case SearchFilterKey.Root:
                    AddRoot(filter.Value, negated, roots, paths, warnings);
                    break;
                default:
                    warnings.Add(L.F("FilterBuilder.FilterWasNotApplied", QueryParser.CanonicalKey(filter.Key), filter.Value));
                    break;
            }
        }

        private void AddFolder(string value, bool negated, IReadOnlyList<Root> roots, PathNormalizer paths, List<string> warnings)
        {
            var target = negated ? ExcludeFolders : IncludeFolders;
            if (paths.TryNormalize(value, out var absolute))
            {
                // Папка принадлежит самому глубокому корню; корни внутри неё — часть её области (ADR-0015).
                if (RootNesting.OwnerOf(absolute, roots, paths) is { } owner && paths.TryGetRelativePath(owner.Path, absolute, out var relative))
                {
                    AddDistinct(target, new FolderScope(owner.Id, relative, null));
                    AddNestedRoots(target, absolute, roots, paths);
                    return;
                }

                warnings.Add(L.F("FilterBuilder.FolderIsNotInAnyRoot", value));
                if (!negated)
                {
                    AddDistinct(IncludeRoots, -1L);
                }

                return;
            }

            var name = SearchText.Fold(SearchText.Compose(value.Trim().Trim(paths.Separator, '/')));
            foreach (var root in roots)
            {
                if (SearchText.Fold(root.Label) == name || SearchText.Fold(paths.GetFileName(root.Path)) == name)
                {
                    AddDistinct(target, new FolderScope(root.Id, string.Empty, null));
                    AddNestedRoots(target, root.Path, roots, paths);
                }
            }

            AddDistinct(target, new FolderScope(null, null, name));
        }

        private static void AddNestedRoots(List<FolderScope> target, string normalizedFolder, IReadOnlyList<Root> roots, PathNormalizer paths)
        {
            foreach (var nested in RootNesting.NestedIn(normalizedFolder, roots, paths))
            {
                AddDistinct(target, new FolderScope(nested.Id, string.Empty, null));
            }
        }

        private void AddRoot(string value, bool negated, IReadOnlyList<Root> roots, PathNormalizer paths, List<string> warnings)
        {
            var target = negated ? ExcludeRoots : IncludeRoots;
            var folded = SearchText.Fold(SearchText.Compose(value.Trim()));
            var matched = roots
                .Where(root => SearchText.Fold(root.Label) == folded
                    || SearchText.Fold(paths.GetFileName(root.Path)) == folded
                    || root.Id.ToString(CultureInfo.InvariantCulture) == folded
                    || (paths.TryNormalize(value, out var normalized) && paths.Comparer.Equals(normalized, root.Path)))
                .ToList();

            if (matched.Count == 0)
            {
                warnings.Add(L.F("FilterBuilder.RootNotFound", value));
                if (!negated)
                {
                    AddDistinct(IncludeRoots, -1L);
                }

                return;
            }

            foreach (var root in matched)
            {
                AddDistinct(target, root.Id);

                // Корни внутри найденного физически лежат в нём — они часть его области (ADR-0015).
                foreach (var nested in RootNesting.NestedIn(root.Path, roots, paths))
                {
                    AddDistinct(target, nested.Id);
                }
            }
        }
    }
}
