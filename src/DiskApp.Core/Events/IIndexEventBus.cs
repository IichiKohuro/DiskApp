namespace DiskApp.Core.Events;

/// <summary>
/// Внутренняя шина событий ядра: индексация и операции публикуют, сервер транслирует в SignalR, интерфейс в одном процессе
/// может подписаться напрямую. Публикация синхронная и не бросает исключений подписчиков наружу.
/// </summary>
public interface IIndexEventBus
{
    /// <summary>
    /// Доставляет событие всем подписчикам его типа в порядке подписки.
    /// </summary>
    void Publish<TEvent>(TEvent indexEvent)
        where TEvent : class, IIndexEvent;

    /// <summary>
    /// Подписка на события одного типа; освобождение возвращённого объекта отписывает.
    /// </summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        where TEvent : class, IIndexEvent;
}
