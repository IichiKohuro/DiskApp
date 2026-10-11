namespace DiskApp.Core.Abstractions;

/// <summary>
/// На диске с базой индекса закончилось место (PLAN.md, этап 10.3). Хранилище переводит в него ошибку своего движка
/// (у SQLite — <c>SQLITE_FULL</c>), чтобы индексатор и интерфейс показали понятную причину, а не «database or disk is full».
/// </summary>
public sealed class DiskFullException : IOException
{
    /// <summary>Создаёт исключение без пояснения.</summary>
    public DiskFullException()
    {
    }

    /// <summary>Создаёт исключение с пояснением.</summary>
    public DiskFullException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт исключение с пояснением и исходной ошибкой хранилища.</summary>
    public DiskFullException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
