using System.Text;

using DiskApp.Core.Domain;

namespace DiskApp.Core.Search;

/// <summary>
/// План исполнения запроса (PLAN.md, этап 3.2; ADR-0005): термы, разложенные по стратегии поиска, и типизированные
/// фильтры. Термы свёрнуты (<see cref="SearchText.Fold"/>). Значения одного ключа объединяются через «или»,
/// разные ключи — через «и»; отрицательные условия исключают.
/// </summary>
public sealed record SearchPlan
{
    /// <summary>Пустой план: подборка недавних и избранных.</summary>
    public static SearchPlan Empty { get; } = new();

    /// <summary>Термы и фразы от трёх символов: ищутся FTS5 trigram <c>MATCH</c>, все обязательны.</summary>
    public IReadOnlyList<string> FtsTerms { get; init; } = [];

    /// <summary>Термы из одного–двух символов: префикс имени (если нет длинных термов) или подстрока среди кандидатов.</summary>
    public IReadOnlyList<string> ShortTerms { get; init; } = [];

    /// <summary>Термы с отрицанием: имя их не содержит.</summary>
    public IReadOnlyList<string> ExcludedTerms { get; init; } = [];

    /// <summary>Регулярное выражение по имени.</summary>
    public string? RegexPattern { get; init; }

    /// <summary>Расширения: только эти.</summary>
    public IReadOnlyList<string> IncludeExtensions { get; init; } = [];

    /// <summary>Расширения: кроме этих.</summary>
    public IReadOnlyList<string> ExcludeExtensions { get; init; } = [];

    /// <summary>Виды: только эти.</summary>
    public IReadOnlyList<FileKind> IncludeKinds { get; init; } = [];

    /// <summary>Виды: кроме этих.</summary>
    public IReadOnlyList<FileKind> ExcludeKinds { get; init; } = [];

    /// <summary>Внутри одной из папок.</summary>
    public IReadOnlyList<FolderScope> IncludeFolders { get; init; } = [];

    /// <summary>Не внутри этих папок.</summary>
    public IReadOnlyList<FolderScope> ExcludeFolders { get; init; } = [];

    /// <summary>Только эти корни; содержит −1, если фильтр корня ничего не нашёл (результат пуст).</summary>
    public IReadOnlyList<long> IncludeRoots { get; init; } = [];

    /// <summary>Кроме этих корней.</summary>
    public IReadOnlyList<long> ExcludeRoots { get; init; } = [];

    /// <summary>Размер в одном из диапазонов (только файлы).</summary>
    public IReadOnlyList<SizeRange> IncludeSizes { get; init; } = [];

    /// <summary>Размер ни в одном из диапазонов.</summary>
    public IReadOnlyList<SizeRange> ExcludeSizes { get; init; } = [];

    /// <summary>Изменён в одном из периодов.</summary>
    public IReadOnlyList<DateRange> IncludeModified { get; init; } = [];

    /// <summary>Изменён вне этих периодов.</summary>
    public IReadOnlyList<DateRange> ExcludeModified { get; init; } = [];

    /// <summary>Создан в одном из периодов.</summary>
    public IReadOnlyList<DateRange> IncludeCreated { get; init; } = [];

    /// <summary>Создан вне этих периодов.</summary>
    public IReadOnlyList<DateRange> ExcludeCreated { get; init; } = [];

    /// <summary>Признаки, которые должны быть все.</summary>
    public IReadOnlyList<AttributeFilter> IncludeAttributes { get; init; } = [];

    /// <summary>Признаки, которых не должно быть.</summary>
    public IReadOnlyList<AttributeFilter> ExcludeAttributes { get; init; } = [];

    /// <summary>Теги (свёрнутые), которые должны быть все.</summary>
    public IReadOnlyList<string> IncludeTags { get; init; } = [];

    /// <summary>Теги, которых не должно быть.</summary>
    public IReadOnlyList<string> ExcludeTags { get; init; } = [];

    /// <summary>Только избранное (<c>true</c>), кроме избранного (<c>false</c>) или без условия.</summary>
    public bool? Favorite { get; init; }

    /// <summary>Замечания разбора и планирования.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Положительные термы для ранжирования и подсветки: длинные, затем короткие.</summary>
    public IReadOnlyList<string> PositiveTerms => [.. FtsTerms, .. ShortTerms];

    /// <summary>Источник кандидатов.</summary>
    public CandidateSource Source => FtsTerms.Count > 0 ? CandidateSource.FullText : ShortTerms.Count > 0 ? CandidateSource.NamePrefix : CandidateSource.Filters;

    /// <summary>Нет ни термов, ни фильтров, ни выражения: показывается подборка.</summary>
    public bool IsEmpty => FtsTerms.Count == 0 && ShortTerms.Count == 0 && ExcludedTerms.Count == 0 && RegexPattern is null && !HasFilters;

    /// <summary>Есть фильтры.</summary>
    public bool HasFilters =>
        IncludeExtensions.Count + ExcludeExtensions.Count + IncludeKinds.Count + ExcludeKinds.Count
        + IncludeFolders.Count + ExcludeFolders.Count + IncludeRoots.Count + ExcludeRoots.Count
        + IncludeSizes.Count + ExcludeSizes.Count + IncludeModified.Count + ExcludeModified.Count
        + IncludeCreated.Count + ExcludeCreated.Count + IncludeAttributes.Count + ExcludeAttributes.Count
        + IncludeTags.Count + ExcludeTags.Count > 0
        || Favorite is not null;

    /// <summary>
    /// Подпись всего, кроме положительных термов: два плана с одной подписью отличаются только термами,
    /// и результат более длинного запроса — подмножество результата более короткого (кэш уточнения).
    /// </summary>
    public string ConditionsSignature
    {
        get
        {
            var builder = new StringBuilder();
            Append(builder, "x", ExcludedTerms);
            Append<string>(builder, "re", RegexPattern is null ? [] : [RegexPattern]);
            Append(builder, "ie", IncludeExtensions);
            Append(builder, "ee", ExcludeExtensions);
            Append(builder, "ik", IncludeKinds);
            Append(builder, "ek", ExcludeKinds);
            Append(builder, "if", IncludeFolders);
            Append(builder, "ef", ExcludeFolders);
            Append(builder, "ir", IncludeRoots);
            Append(builder, "er", ExcludeRoots);
            Append(builder, "is", IncludeSizes);
            Append(builder, "es", ExcludeSizes);
            Append(builder, "im", IncludeModified);
            Append(builder, "em", ExcludeModified);
            Append(builder, "ic", IncludeCreated);
            Append(builder, "ec", ExcludeCreated);
            Append(builder, "ia", IncludeAttributes);
            Append(builder, "ea", ExcludeAttributes);
            Append(builder, "it", IncludeTags);
            Append(builder, "et", ExcludeTags);
            builder.Append("fav=").Append(Favorite?.ToString() ?? "-");
            return builder.ToString();
        }
    }

    private static void Append<T>(StringBuilder builder, string key, IEnumerable<T> values)
    {
        builder.Append(key).Append('=');
        foreach (var value in values)
        {
            builder.Append(value).Append('\u001f');
        }

        builder.Append(';');
    }
}

/// <summary>
/// Папка для фильтра <c>in:</c>: конкретная папка корня или любая папка с таким именем.
/// </summary>
/// <param name="RootId">Корень; <c>null</c> — любой.</param>
/// <param name="RelPath">Путь папки от корня; пустая строка — весь корень; <c>null</c> — искать по имени.</param>
/// <param name="FolderName">Свёрнутое имя папки, если путь не задан.</param>
public sealed record FolderScope(long? RootId, string? RelPath, string? FolderName);

/// <summary>
/// Откуда берутся кандидаты.
/// </summary>
public enum CandidateSource
{
    /// <summary>FTS5 trigram по свёрнутому имени.</summary>
    FullText,

    /// <summary>Префикс свёрнутого имени по B-tree индексу.</summary>
    NamePrefix,

    /// <summary>Только фильтры, без термов: свежие изменения первыми.</summary>
    Filters,
}
