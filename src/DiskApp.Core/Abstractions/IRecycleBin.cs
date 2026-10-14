using DiskApp.Core.Results;

namespace DiskApp.Core.Abstractions;

/// <summary>
/// Корзина Windows для истории файлов (PLAN.md, этап 20.2): найти, что удалено в корзину из этого места, и вернуть на место.
/// </summary>
public interface IRecycleBin
{
    /// <summary>Самый поздний элемент корзины, удалённый из этого места; <c>null</c> — не найден.</summary>
    /// <param name="originalPath">Полный путь, где файл или папка лежали до удаления.</param>
    RecycledItem? Find(string originalPath);

    /// <summary>Возвращает элемент корзины на прежнее место.</summary>
    /// <param name="storedPath">Где элемент лежит в корзине.</param>
    /// <param name="originalPath">Куда вернуть.</param>
    Result Restore(string storedPath, string originalPath);
}

/// <summary>Элемент корзины.</summary>
/// <param name="OriginalPath">Где лежал до удаления.</param>
/// <param name="StoredPath">Где лежит в корзине.</param>
/// <param name="DeletedUtc">Когда удалён.</param>
/// <param name="Size">Размер.</param>
public sealed record RecycledItem(string OriginalPath, string StoredPath, DateTimeOffset DeletedUtc, long Size);
