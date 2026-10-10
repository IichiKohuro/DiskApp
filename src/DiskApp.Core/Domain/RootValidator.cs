using DiskApp.Core.Paths;
using DiskApp.Core.Results;

namespace DiskApp.Core.Domain;

/// <summary>
/// Проверка пути нового корня (PLAN.md, этап 1.1): путь должен быть абсолютным и ещё не добавленным;
/// вложенность в существующий корень или поверх него — предупреждение, а не ошибка: пользователь может так хотеть.
/// К диску не обращается — существование папки проверяет вызывающая сторона через <c>IFileSystem</c>.
/// </summary>
public static class RootValidator
{
    /// <summary>Код ошибки: путь не задан.</summary>
    public const string PathEmptyCode = "root.path_empty";

    /// <summary>Код ошибки: путь относительный или некорректный.</summary>
    public const string PathNotAbsoluteCode = "root.path_not_absolute";

    /// <summary>Код ошибки: такой корень уже есть.</summary>
    public const string DuplicateCode = "root.duplicate";

    /// <summary>
    /// Проверяет путь и готовит данные нового корня: нормализованный путь, подпись по умолчанию и предупреждения.
    /// </summary>
    /// <param name="path">Путь, введённый пользователем или выбранный в диалоге.</param>
    /// <param name="existingRoots">Уже добавленные корни.</param>
    /// <param name="normalizer">Правила путей; обычно <see cref="PathNormalizer.Current"/>.</param>
    public static Result<RootCandidate> Validate(string? path, IEnumerable<Root> existingRoots, PathNormalizer normalizer)
    {
        ArgumentNullException.ThrowIfNull(existingRoots);
        ArgumentNullException.ThrowIfNull(normalizer);

        if (string.IsNullOrWhiteSpace(path))
        {
            return Result.Fail<RootCandidate>(new DomainError(PathEmptyCode, "Укажите папку или диск."));
        }

        if (!normalizer.TryNormalize(path, out var normalized))
        {
            return Result.Fail<RootCandidate>(new DomainError(PathNotAbsoluteCode, $"Путь «{path.Trim()}» должен быть абсолютным, например D:\\Projects или \\\\server\\share."));
        }

        var warnings = new List<RootWarning>();
        foreach (var root in existingRoots)
        {
            if (normalizer.Comparer.Equals(root.Path, normalized))
            {
                return Result.Fail<RootCandidate>(new DomainError(DuplicateCode, $"Корень «{root.Label}» ({root.Path}) уже добавлен."));
            }

            if (normalizer.IsNestedIn(normalized, root.Path))
            {
                warnings.Add(new RootWarning(RootWarningKind.InsideExistingRoot, root));
            }
            else if (normalizer.IsNestedIn(root.Path, normalized))
            {
                warnings.Add(new RootWarning(RootWarningKind.ContainsExistingRoot, root));
            }
        }

        return Result.Ok(new RootCandidate(normalized, normalizer.GetFileName(normalized), warnings));
    }
}

/// <summary>
/// Проверенный путь нового корня с подписью по умолчанию и предупреждениями о вложенности.
/// </summary>
/// <param name="Path">Нормализованный абсолютный путь.</param>
/// <param name="Label">Подпись по умолчанию: имя папки или <c>D:\</c> для диска.</param>
/// <param name="Warnings">Предупреждения; пусто, если корень ни с кем не пересекается.</param>
public sealed record RootCandidate(string Path, string Label, IReadOnlyList<RootWarning> Warnings)
{
    /// <summary>Есть ли предупреждения, которые стоит показать пользователю.</summary>
    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>
/// Предупреждение о пересечении нового корня с существующим.
/// </summary>
/// <param name="Kind">Как именно пересекаются.</param>
/// <param name="Related">Существующий корень.</param>
public sealed record RootWarning(RootWarningKind Kind, Root Related);

/// <summary>
/// Виды пересечения корней.
/// </summary>
public enum RootWarningKind
{
    /// <summary>Новый корень лежит внутри существующего: файлы будут индексироваться дважды.</summary>
    InsideExistingRoot,

    /// <summary>Новый корень содержит существующий: его файлы попадут в оба корня.</summary>
    ContainsExistingRoot,
}
