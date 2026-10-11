namespace DiskApp.Core.Domain;

/// <summary>
/// Тег пользователя для записей индекса.
/// </summary>
/// <param name="Id">Идентификатор в базе; 0 до первого сохранения.</param>
/// <param name="Name">Имя тега; уникально без учёта регистра.</param>
/// <param name="Color">Цвет в виде <c>#RRGGBB</c> или <c>null</c> для цвета по умолчанию.</param>
public sealed record Tag(long Id, string Name, string? Color = null);

/// <summary>
/// Авто-теги папки (PLAN.md, этап 19, ADR-0012): действуют на всё внутри неё на любой глубине — на текущие файлы и новые.
/// </summary>
/// <param name="Path">Полный нормализованный путь папки.</param>
/// <param name="Tags">Теги по имени.</param>
public sealed record FolderTags(string Path, IReadOnlyList<Tag> Tags);
