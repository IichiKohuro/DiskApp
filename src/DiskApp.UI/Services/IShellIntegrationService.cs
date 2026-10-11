namespace DiskApp.UI.Services;

/// <summary>Интеграция с Проводником.</summary>
public enum ShellIntegrationFeature
{
    /// <summary>Ярлык «DiskApp» в меню «Отправить».</summary>
    SendTo,

    /// <summary>Пункты контекстного меню Проводника: «Добавить в DiskApp», «Открыть в DiskApp», «Добавить в индекс».</summary>
    ContextMenu,

    /// <summary>Ссылки <c>diskapp://</c>.</summary>
    Protocol,
}

/// <summary>
/// Интеграции с Windows (PLAN.md, этап 8.5): включаются и выключаются из настроек, состояние читается из системы.
/// </summary>
public interface IShellIntegrationService
{
    /// <summary>Интеграции поддерживаются на этой платформе.</summary>
    bool IsSupported { get; }

    /// <summary>Включена ли интеграция именно для этой копии приложения.</summary>
    bool IsEnabled(ShellIntegrationFeature feature);

    /// <summary>Включает или выключает интеграцию; ошибка Windows — исключение с понятным текстом.</summary>
    void SetEnabled(ShellIntegrationFeature feature, bool enabled);
}

/// <summary>
/// Интеграции недоступны (не Windows, превью, тесты).
/// </summary>
public sealed class NullShellIntegrationService : IShellIntegrationService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public bool IsEnabled(ShellIntegrationFeature feature) => false;

    /// <inheritdoc />
    public void SetEnabled(ShellIntegrationFeature feature, bool enabled)
    {
    }
}
