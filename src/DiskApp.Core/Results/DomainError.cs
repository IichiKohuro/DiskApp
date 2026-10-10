namespace DiskApp.Core.Results;

/// <summary>
/// Ошибка домена: машинный код для клиентов и API (<c>область.причина</c>) и сообщение для человека.
/// Коды стабильны и документируются рядом с типом, который их порождает.
/// </summary>
/// <param name="Code">Код вида <c>root.path_not_absolute</c>: латиница, точка отделяет область.</param>
/// <param name="Message">Описание на языке пользователя; без путей к секретам и содержимого файлов.</param>
public sealed record DomainError(string Code, string Message)
{
    /// <summary>
    /// Ошибка «результат не инициализирован»: её возвращает <c>default(Result&lt;T&gt;)</c>, чтобы необработанное значение не выглядело успехом.
    /// </summary>
    public static DomainError Uninitialized { get; } = new("core.result_uninitialized", "Результат операции не был заполнен.");

    /// <inheritdoc />
    public override string ToString() => $"{Code}: {Message}";
}
