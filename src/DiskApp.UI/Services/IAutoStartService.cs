namespace DiskApp.UI.Services;

/// <summary>
/// Запуск вместе с операционной системой (PLAN.md, этап 6.6). Состояние читается из системы, а не из настроек:
/// пользователь мог выключить автозапуск в диспетчере задач.
/// </summary>
public interface IAutoStartService
{
    /// <summary>Платформа поддерживает автозапуск.</summary>
    bool IsSupported { get; }

    /// <summary>Автозапуск включён и указывает на этот исполняемый файл.</summary>
    bool IsEnabled { get; }

    /// <summary>Предупреждение для настроек (портативная копия и т. п.); пусто, если предупреждать не о чем.</summary>
    string Warning { get; }

    /// <summary>Включает или выключает автозапуск.</summary>
    /// <exception cref="InvalidOperationException">Система не дала изменить автозапуск.</exception>
    void SetEnabled(bool enabled);
}

/// <summary>
/// Автозапуск не поддерживается: платформы без реализации и тесты.
/// </summary>
public sealed class NullAutoStartService : IAutoStartService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public string Warning => "Автозапуск на этой платформе пока не поддерживается.";

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
    }
}
