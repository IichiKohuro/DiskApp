using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

using FluentAvalonia.Styling;

namespace DiskApp.UI.Services;

/// <summary>
/// Тема интерфейса, выбранная пользователем.
/// </summary>
public enum AppTheme
{
    /// <summary>Как в системе: светлая или тёмная вслед за Windows.</summary>
    System,

    /// <summary>Всегда светлая.</summary>
    Light,

    /// <summary>Всегда тёмная.</summary>
    Dark,
}

/// <summary>
/// Переключение темы приложения. Сохранение выбора в настройках появится в этапе 9.
/// </summary>
public interface IThemeService
{
    /// <summary>Текущая выбранная тема.</summary>
    AppTheme Current { get; }

    /// <summary>Применяет тему ко всему приложению немедленно.</summary>
    void Apply(AppTheme theme);
}

/// <summary>
/// Применяет тему через <see cref="Application.RequestedThemeVariant"/>: FluentAvalonia перекрашивает все окна сразу.
/// Высокая контрастность Windows важнее выбранной темы (PLAN.md, этап 9.4): пока она включена, действует тема
/// высокой контрастности FluentAvalonia, а при её включении и выключении тема переключается сама.
/// </summary>
public sealed class AppThemeService : IThemeService
{
    /// <summary>Переменная окружения для проверки вида в высокой контрастности без смены настроек Windows.</summary>
    public const string ForceHighContrastVariable = "DISKAPP_HIGH_CONTRAST";

    private IPlatformSettings? _platform;

    /// <inheritdoc />
    public AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>Включена высокая контрастность Windows (или задана переменной окружения для проверки).</summary>
    public static bool IsHighContrast(IPlatformSettings? platform)
        => Environment.GetEnvironmentVariable(ForceHighContrastVariable) == "1"
            || platform?.GetColorValues().ContrastPreference == ColorContrastPreference.High;

    /// <summary>Вариант темы для выбора пользователя с учётом высокой контрастности.</summary>
    public static ThemeVariant VariantFor(AppTheme theme, bool highContrast) => highContrast
        ? FluentAvaloniaTheme.HighContrastTheme
        : theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

    /// <inheritdoc />
    public void Apply(AppTheme theme)
    {
        Current = theme;

        if (Application.Current is { } application)
        {
            if (_platform is null && application.PlatformSettings is { } platform)
            {
                _platform = platform;
                platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() => Apply(Current));
            }

            application.RequestedThemeVariant = VariantFor(theme, IsHighContrast(application.PlatformSettings));
        }
    }
}
