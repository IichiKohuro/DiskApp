namespace DiskApp.UI.Services;

/// <summary>
/// Команды оболочки, которые интерфейс вызывает, но не реализует: показать окно, перейти на страницу.
/// На десктопе их выполняет <c>DesktopShell</c> (трей, окна, хоткеи); на других платформах — своя оболочка.
/// </summary>
public interface IAppCommands
{
    /// <summary>Показывает окно быстрого поиска и ставит фокус в поле ввода.</summary>
    void ShowQuickSearch();

    /// <summary>Показывает главное окно и выводит его на передний план.</summary>
    void ShowMainWindow();

    /// <summary>Открывает страницу главного окна по ключу (<see cref="ViewModels.PageKeys"/>).</summary>
    void NavigateTo(string pageKey);

    /// <summary>Сценарий «Добавить»: спросить место и положить туда файлы (PLAN.md, этап 8.5).</summary>
    Task AddFilesAsync(IReadOnlyList<string> paths);

    /// <summary>Пройти мастер первого запуска заново и снова показать обучающие подсказки (PLAN.md, этап 9.3).</summary>
    void StartOnboarding();
}
