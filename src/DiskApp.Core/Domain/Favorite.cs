namespace DiskApp.Core.Domain;

/// <summary>
/// Запись, закреплённая пользователем в избранном.
/// </summary>
/// <param name="EntryId">Запись индекса.</param>
/// <param name="Order">Порядок в списке избранного, с нуля.</param>
/// <param name="PinnedUtc">Когда добавлена.</param>
public sealed record Favorite(long EntryId, int Order, DateTimeOffset PinnedUtc);
