namespace DiskApp.UI.ViewModels;

/// <summary>
/// Страница, которой важно, видна ли она: перечитать данные при входе, остановить автообновление при уходе.
/// </summary>
public interface IPageLifecycle
{
    /// <summary>Страница стала текущей.</summary>
    void OnNavigatedTo();

    /// <summary>Страница перестала быть текущей.</summary>
    void OnNavigatedFrom();
}
