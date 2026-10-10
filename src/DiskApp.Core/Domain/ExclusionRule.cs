using System.Globalization;

namespace DiskApp.Core.Domain;

/// <summary>
/// Правило исключения из индекса. Смысл <see cref="Pattern"/> зависит от вида: glob-шаблон имени или пути,
/// порог размера в байтах, предельная глубина; для скрытых и системных объектов шаблон не нужен.
/// Сопоставление с файлами выполняет сканер (этап 2.1); здесь только модель.
/// </summary>
/// <param name="Kind">Вид правила.</param>
/// <param name="Pattern">Шаблон, число байтов или глубина в виде строки; пусто для <see cref="ExclusionKind.Hidden"/> и <see cref="ExclusionKind.System"/>.</param>
public sealed record ExclusionRule(ExclusionKind Kind, string Pattern)
{
    /// <summary>
    /// Стандартный набор исключений (PLAN.md, этапы 2.1 и 2.3): служебные папки Windows, артефакты сборки, временные файлы
    /// и недокачанные загрузки (<c>~$</c> Office, <c>.crdownload</c> Chrome, <c>.part</c> Firefox) — их же пропускает наблюдатель.
    /// Пользователь редактирует его в настройках.
    /// </summary>
    public static IReadOnlyList<ExclusionRule> Defaults { get; } =
    [
        Glob("$RECYCLE.BIN"),
        Glob("System Volume Information"),
        Glob("$Recycle.Bin"),
        Glob("node_modules"),
        Glob(".git"),
        Glob("bin"),
        Glob("obj"),
        Glob("*.tmp"),
        Glob("~$*"),
        Glob("*.crdownload"),
        Glob("*.part"),
    ];

    /// <summary>Исключить по glob-шаблону имени (<c>*.tmp</c>) или пути (<c>**/node_modules</c>).</summary>
    public static ExclusionRule Glob(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        return new ExclusionRule(ExclusionKind.Glob, pattern);
    }

    /// <summary>Исключить скрытые файлы и папки.</summary>
    public static ExclusionRule Hidden() => new(ExclusionKind.Hidden, string.Empty);

    /// <summary>Исключить системные файлы и папки.</summary>
    public static ExclusionRule System() => new(ExclusionKind.System, string.Empty);

    /// <summary>Исключить файлы крупнее порога.</summary>
    public static ExclusionRule MaxSize(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return new ExclusionRule(ExclusionKind.Size, bytes.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Не заходить глубже указанного уровня (1 — только верхний уровень корня).</summary>
    public static ExclusionRule MaxDepth(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        return new ExclusionRule(ExclusionKind.Depth, depth.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Порог для <see cref="ExclusionKind.Size"/> и <see cref="ExclusionKind.Depth"/>; <c>null</c>, если шаблон не число.</summary>
    public long? Threshold => long.TryParse(Pattern, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>
/// Виды правил исключения.
/// </summary>
public enum ExclusionKind
{
    /// <summary>Glob-шаблон имени или пути.</summary>
    Glob,

    /// <summary>Скрытые объекты.</summary>
    Hidden,

    /// <summary>Системные объекты.</summary>
    System,

    /// <summary>Файлы крупнее порога в байтах.</summary>
    Size,

    /// <summary>Глубина вложенности.</summary>
    Depth,
}
