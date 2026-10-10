namespace DiskApp.Core.FileSystem;

/// <summary>
/// Сведения о накопителе, на котором лежит корень: тип носителя задаёт степень параллелизма сканера,
/// ключ физического диска — правило «одна тяжёлая задача на диск» (PLAN.md, этапы 2.1 и 2.4).
/// </summary>
public interface IDriveInfoProvider
{
    /// <summary>Описание накопителя для пути; при ошибке — <see cref="DriveMedia.Unknown"/> с ключом по корню пути.</summary>
    DriveDescription Describe(string fullPath);
}

/// <summary>
/// Накопитель.
/// </summary>
/// <param name="DiskKey">Ключ физического диска: <c>disk:0</c>, корень тома или сервер для сетевых путей.</param>
/// <param name="Media">Тип носителя.</param>
public sealed record DriveDescription(string DiskKey, DriveMedia Media)
{
    /// <summary>
    /// Рекомендуемое число параллельных потоков обхода: SSD — 4, сеть — 2, HDD и съёмные — 1, неизвестный — 2.
    /// </summary>
    public int RecommendedParallelism => Media switch
    {
        DriveMedia.SolidState => 4,
        DriveMedia.Network => 2,
        DriveMedia.Rotational or DriveMedia.Removable => 1,
        _ => 2,
    };
}

/// <summary>
/// Тип носителя.
/// </summary>
public enum DriveMedia
{
    /// <summary>Не удалось определить.</summary>
    Unknown,

    /// <summary>Твердотельный, без задержки позиционирования.</summary>
    SolidState,

    /// <summary>Жёсткий диск с механикой.</summary>
    Rotational,

    /// <summary>Сетевой путь.</summary>
    Network,

    /// <summary>Съёмный носитель.</summary>
    Removable,
}
