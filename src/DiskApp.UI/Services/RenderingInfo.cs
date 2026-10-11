namespace DiskApp.UI.Services;

/// <summary>
/// С каким рендерингом запущено приложение (PLAN.md, этап 10.2; ADR-0009): оболочка задаёт значение до создания окон,
/// страница настроек по нему понимает, нужен ли перезапуск после смены настройки.
/// </summary>
public static class RenderingInfo
{
    /// <summary>Аппаратное ускорение включено; <c>null</c> — неизвестно (превью, тесты).</summary>
    public static bool? HardwareAccelerated { get; set; }
}
