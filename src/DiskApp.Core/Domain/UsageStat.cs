namespace DiskApp.Core.Domain;

/// <summary>
/// Статистика использования записи: сколько раз и когда открывали. Основа frecency в ранжировании (PLAN.md, этап 3.3).
/// </summary>
/// <param name="EntryId">Запись индекса.</param>
/// <param name="OpenCount">Сколько раз открывали.</param>
/// <param name="LastOpenedUtc">Когда открывали последний раз; <c>null</c> — ни разу.</param>
public sealed record UsageStat(long EntryId, int OpenCount, DateTimeOffset? LastOpenedUtc)
{
    /// <summary>Период полураспада по умолчанию: через 30 дней без открытий вес падает вдвое.</summary>
    public static TimeSpan DefaultHalfLife { get; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Frecency: <c>log(1 + открытий) · 0,5^(дней / период полураспада)</c>. Ноль, если запись не открывали.
    /// </summary>
    /// <param name="now">Текущий момент.</param>
    /// <param name="halfLife">Период полураспада; по умолчанию <see cref="DefaultHalfLife"/>.</param>
    public double Frecency(DateTimeOffset now, TimeSpan? halfLife = null)
    {
        if (OpenCount <= 0 || LastOpenedUtc is null)
        {
            return 0;
        }

        var period = halfLife ?? DefaultHalfLife;
        var age = now - LastOpenedUtc.Value;
        var ageDays = Math.Max(0, age.TotalDays);
        var decay = Math.Pow(0.5, ageDays / period.TotalDays);
        return Math.Log(1 + OpenCount) * decay;
    }

    /// <summary>
    /// Статистика после ещё одного открытия.
    /// </summary>
    public UsageStat Opened(DateTimeOffset at) => this with { OpenCount = OpenCount + 1, LastOpenedUtc = at };
}
