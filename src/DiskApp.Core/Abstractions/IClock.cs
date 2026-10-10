namespace DiskApp.Core.Abstractions;

/// <summary>
/// Источник текущего времени, чтобы frecency, сверки и напоминания проверялись в тестах с фиксированным «сейчас».
/// </summary>
public interface IClock
{
    /// <summary>Текущий момент в UTC.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Системные часы.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <summary>Единственный экземпляр: состояния нет.</summary>
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
