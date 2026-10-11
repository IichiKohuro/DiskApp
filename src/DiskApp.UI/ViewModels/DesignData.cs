using DiskApp.UI.Services;

using FluentAvalonia.UI.Controls;

using FluentIcons.Common;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Готовые ViewModel с макетными данными для XAML-превью в Visual Studio и Rider (<c>Design.DataContext</c>).
/// Каждое обращение создаёт новый экземпляр.
/// </summary>
public static class DesignData
{
    private static readonly Lazy<SampleSearchService> SearchService = new(static () => new SampleSearchService());
    private static readonly NullAppCommands Commands = new();

    private static readonly HotkeyStatus Hotkeys = new() { IsRegistered = true };

    /// <summary>Главная страница.</summary>
    public static HomeViewModel Home => new(SearchService.Value, Commands, Hotkeys);

    /// <summary>Быстрый поиск.</summary>
    public static QuickSearchViewModel QuickSearch => new(SearchService.Value);

    /// <summary>Настройки.</summary>
    public static SettingsViewModel Settings => new(new AppThemeService(), SearchService.Value, Hotkeys);

    /// <summary>Оболочка главного окна.</summary>
    public static MainViewModel Main => new(Home, QuickSearch, Settings, SearchService.Value);

    /// <summary>Заглушка страницы.</summary>
    public static PlaceholderPageViewModel Placeholder => new(
        PageKeys.Browse, "Обзор", FASymbol.Folder, Symbol.FolderOpen,
        "Дерево папок, список и плитка, адресная строка и панель сведений — как в Проводнике, только быстрее.",
        "Этап 8");

    /// <summary>
    /// Команды оболочки, которые ничего не делают: для превью и тестов.
    /// </summary>
    internal sealed class NullAppCommands : IAppCommands
    {
        /// <inheritdoc />
        public void ShowQuickSearch()
        {
        }

        /// <inheritdoc />
        public void ShowMainWindow()
        {
        }

        /// <inheritdoc />
        public void NavigateTo(string pageKey)
        {
        }

        /// <inheritdoc />
        public Task AddFilesAsync(IReadOnlyList<string> paths) => Task.CompletedTask;

        /// <inheritdoc />
        public void BrowseFolder(string path)
        {
        }

        /// <inheritdoc />
        public void StartOnboarding()
        {
        }
    }
}
