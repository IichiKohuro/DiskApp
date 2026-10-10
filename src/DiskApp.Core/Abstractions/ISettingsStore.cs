namespace DiskApp.Core.Abstractions;

/// <summary>
/// Хранилище настроек одного типа с уведомлением об изменениях (аналог <c>IOptionsMonitor</c>):
/// значение в памяти, запись атомарная, подписчики узнают о каждом сохранении.
/// </summary>
/// <typeparam name="T">Тип настроек; неизменяемая запись.</typeparam>
public interface ISettingsStore<T>
    where T : class
{
    /// <summary>Текущие настройки.</summary>
    T Current { get; }

    /// <summary>
    /// Сохраняет настройки и оповещает подписчиков.
    /// </summary>
    Task SaveAsync(T settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Подписка на изменения; освобождение возвращённого объекта отписывает.
    /// </summary>
    IDisposable Subscribe(Action<T> onChanged);
}
