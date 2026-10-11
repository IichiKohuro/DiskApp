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

/// <summary>
/// Бросок файлов в папку-накопитель из окна Drag&amp;Drop: <c>POST /api/v1/sorting/drop</c> (PLAN.md, этап 18.2, ADR-0011).
/// Источники могут лежать вне добавленных корней (файл с рабочего стола, из мессенджера), назначение — только накопитель
/// из настроек; запрос принимается только с этого компьютера. Конфликт имён — «оставить оба».
/// </summary>
public sealed record DropFilesRequest
{
    /// <summary>Полные пути брошенных файлов и папок.</summary>
    public IReadOnlyList<string> Sources { get; set; } = [];

    /// <summary>Папка-накопитель.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Копировать; иначе — перенести (как в Проводнике: на том же диске — перенос, с другого — копия).</summary>
    public bool Copy { get; set; }
}
