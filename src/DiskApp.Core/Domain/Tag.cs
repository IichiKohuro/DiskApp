namespace DiskApp.Core.Domain;

/// <summary>
/// Тег пользователя для записей индекса.
/// </summary>
/// <param name="Id">Идентификатор в базе; 0 до первого сохранения.</param>
/// <param name="Name">Имя тега; уникально без учёта регистра.</param>
/// <param name="Color">Цвет в виде <c>#RRGGBB</c> или <c>null</c> для цвета по умолчанию.</param>
public sealed record Tag(long Id, string Name, string? Color = null);
