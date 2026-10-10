using System.Diagnostics.CodeAnalysis;

namespace DiskApp.Core.Results;

/// <summary>
/// Фабрика результатов: домен сообщает об ошибках значением, а не исключением (PLAN.md, этап 1.1),
/// чтобы горячие пути поиска и индексации не платили за раскрутку стека.
/// </summary>
public static class Result
{
    /// <summary>
    /// Успех с значением.
    /// </summary>
    public static Result<T> Ok<T>(T value) => new(value, null);

    /// <summary>
    /// Ошибка.
    /// </summary>
    public static Result<T> Fail<T>(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}

/// <summary>
/// Результат операции: либо значение, либо <see cref="DomainError"/>. Структура, чтобы не выделять память на каждый вызов.
/// <c>default</c> считается ошибкой <see cref="DomainError.Uninitialized"/>, а не успехом.
/// </summary>
/// <typeparam name="T">Тип значения при успехе.</typeparam>
public readonly record struct Result<T>
{
    private readonly T? _value;
    private readonly DomainError? _error;
    private readonly bool _initialized;

    internal Result(T? value, DomainError? error)
    {
        _value = value;
        _error = error;
        _initialized = true;
    }

    /// <summary>
    /// Операция завершилась успешно и <see cref="Value"/> доступно.
    /// </summary>
    public bool IsSuccess => _initialized && _error is null;

    /// <summary>
    /// Операция завершилась ошибкой; подробности в <see cref="Error"/>.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Ошибка; <c>null</c> при успехе.
    /// </summary>
    public DomainError? Error => _initialized ? _error : DomainError.Uninitialized;

    /// <summary>
    /// Значение при успехе.
    /// </summary>
    /// <exception cref="InvalidOperationException">Результат — ошибка; проверьте <see cref="IsSuccess"/> или используйте <see cref="TryGetValue"/>.</exception>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Результат содержит ошибку, а не значение: {Error}.");

    /// <summary>
    /// Значение при успехе без исключения.
    /// </summary>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = IsSuccess ? _value : default;
        return IsSuccess;
    }

    /// <summary>
    /// Преобразует значение, сохраняя ошибку как есть.
    /// </summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? Result.Ok(map(_value!)) : Result.Fail<TOut>(Error!);
    }

    /// <summary>
    /// Сводит обе ветви к одному типу.
    /// </summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<DomainError, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value!) : onFailure(Error!);
    }

    /// <inheritdoc />
    public override string ToString() => IsSuccess ? $"Ok({_value})" : $"Fail({Error})";
}
