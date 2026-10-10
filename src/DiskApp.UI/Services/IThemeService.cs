using Avalonia;
using Avalonia.Styling;

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
/// </summary>
public sealed class AppThemeService : IThemeService
{
    /// <inheritdoc />
    public AppTheme Current { get; private set; } = AppTheme.System;

    /// <inheritdoc />
    public void Apply(AppTheme theme)
    {
        Current = theme;

        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }
}
