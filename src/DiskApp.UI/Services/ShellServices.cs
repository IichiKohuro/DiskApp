using Avalonia.Threading;

using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Services;

/// <summary>
/// Навигация по страницам главного окна (PLAN.md, этап 6.1).
/// </summary>
public interface INavigationService
{
    /// <summary>Ключ открытой страницы (<see cref="PageKeys"/>).</summary>
    string? CurrentPage { get; }

    /// <summary>Открыть страницу; <c>false</c> — такой страницы нет.</summary>
    bool NavigateTo(string pageKey);
}

/// <summary>
/// Уведомления в полосе <c>InfoBar</c> главного окна; если окно ещё не открыто, уведомление покажется при открытии.
/// </summary>
public interface IInfoBarService
{
    /// <summary>Показать уведомление.</summary>
    void Show(string title, string message, FAInfoBarSeverity severity = FAInfoBarSeverity.Informational);

    /// <summary>Скрыть уведомление.</summary>
    void Close();
}

/// <summary>
/// Диалоги подтверждения (<c>ContentDialog</c>). Реализацию даёт оболочка: ей известно окно, над которым показывать.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Спросить подтверждение; <c>true</c> — пользователь выбрал основное действие.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string primaryText, string closeText = "Отмена");
}

/// <summary>
/// Поток интерфейса: события сервера приходят на фоновых потоках, а ViewModel меняются только на потоке UI.
/// В тестах подменяется синхронной реализацией.
/// </summary>
public interface IDispatcher
{
    /// <summary>Вызов уже на потоке интерфейса.</summary>
    bool CheckAccess();

    /// <summary>Поставить действие в очередь потока интерфейса.</summary>
    void Post(Action action);

    /// <summary>Выполнить действие на потоке интерфейса и дождаться его.</summary>
    Task InvokeAsync(Func<Task> action);
}

/// <summary>
/// <see cref="INavigationService"/> поверх <see cref="MainViewModel"/>; модель создаётся при первом обращении,
/// поэтому страницы могут зависеть от навигации без цикла в контейнере.
/// </summary>
public sealed class NavigationService(Lazy<MainViewModel> main) : INavigationService
{
    /// <inheritdoc />
    public string? CurrentPage => main.Value.SelectedPage?.Key;

    /// <inheritdoc />
    public bool NavigateTo(string pageKey) => main.Value.NavigateTo(pageKey);
}

/// <summary>
/// <see cref="IInfoBarService"/> поверх полосы уведомлений <see cref="MainViewModel"/>.
/// </summary>
public sealed class InfoBarService(Lazy<MainViewModel> main) : IInfoBarService
{
    /// <inheritdoc />
    public void Show(string title, string message, FAInfoBarSeverity severity = FAInfoBarSeverity.Informational) => main.Value.ShowNotice(title, message, severity);

    /// <inheritdoc />
    public void Close() => main.Value.IsNoticeOpen = false;
}

/// <summary>
/// <see cref="IDispatcher"/> на <see cref="Dispatcher.UIThread"/>.
/// </summary>
public sealed class AvaloniaDispatcher : IDispatcher
{
    /// <inheritdoc />
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    /// <inheritdoc />
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <inheritdoc />
    public Task InvokeAsync(Func<Task> action) => Dispatcher.UIThread.InvokeAsync(action);
}
