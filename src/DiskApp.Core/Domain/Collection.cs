using System.Diagnostics.CodeAnalysis;

namespace DiskApp.Core.Domain;

/// <summary>
/// Коллекция записей: собранная вручную или сохранённый запрос, который выполняется при открытии.
/// </summary>
/// <param name="Id">Идентификатор в базе; 0 до первого сохранения.</param>
/// <param name="Name">Имя коллекции.</param>
/// <param name="Kind">Ручная или сохранённый запрос.</param>
/// <param name="Query">Текст запроса для <see cref="CollectionKind.SavedQuery"/>; у ручной <c>null</c>.</param>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "«Коллекция» — термин продукта (README, PLAN.md); тип не является коллекцией .NET.")]
public sealed record Collection(long Id, string Name, CollectionKind Kind, string? Query = null)
{
    /// <summary>Коллекция — сохранённый запрос с непустым текстом.</summary>
    public bool IsSavedQuery => Kind == CollectionKind.SavedQuery && !string.IsNullOrWhiteSpace(Query);
}

/// <summary>
/// Вид коллекции.
/// </summary>
public enum CollectionKind
{
    /// <summary>Записи добавляет пользователь.</summary>
    Manual,

    /// <summary>Содержимое — результат сохранённого запроса.</summary>
    SavedQuery,
}
