namespace DiskApp.Core.Paths;

/// <summary>
/// Правила записи путей. Задаются явно, а не берутся из ОС, чтобы индекс с путями Windows читался и проверялся на любой платформе.
/// </summary>
public enum PathStyle
{
    /// <summary>Буква диска или UNC, разделитель <c>\</c>, регистр не важен.</summary>
    Windows,

    /// <summary>Корень <c>/</c>, разделитель <c>/</c>, регистр важен.</summary>
    Posix,
}
