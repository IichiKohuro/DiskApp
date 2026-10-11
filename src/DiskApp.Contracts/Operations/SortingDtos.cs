namespace DiskApp.Contracts.Operations;

/// <summary>
/// Разбор из накопителя: <c>POST /api/v1/sorting/move</c>. Перенос встаёт в общую очередь операций (ответ — операция,
/// отменяется как любая другая); после него перенесённые элементы получают прежние теги и <see cref="Tags"/>.
/// </summary>
public sealed record SortFilesRequest
{
    /// <summary>Полные пути файлов и папок из накопителя.</summary>
    public IReadOnlyList<string> Sources { get; set; } = [];

    /// <summary>Рабочая папка или её подпапка.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Имя новой подпапки внутри <see cref="Destination"/>; пусто — переносить прямо в неё.</summary>
    public string? NewFolder { get; set; }

    /// <summary>Теги для перенесённых элементов.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>
    /// Новые имена по порядку <see cref="Sources"/> (с расширением); пусто или <c>null</c> — прежнее имя.
    /// </summary>
    public IReadOnlyList<string?>? Names { get; set; }
}
