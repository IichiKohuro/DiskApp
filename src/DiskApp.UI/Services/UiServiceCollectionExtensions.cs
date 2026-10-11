using DiskApp.UI.ViewModels;

using Microsoft.Extensions.DependencyInjection;

namespace DiskApp.UI.Services;

/// <summary>
/// Регистрация сервисов и ViewModel интерфейса в общем контейнере приложения.
/// </summary>
public static class UiServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет сервисы интерфейса и ViewModel. Оболочка (десктоп, Android, браузер) дополнительно регистрирует
    /// свою реализацию <see cref="IAppCommands"/>.
    /// </summary>
    public static IServiceCollection AddDiskAppUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISearchService, SampleSearchService>();
        services.AddSingleton<IFileActions, SampleFileActions>();
        services.AddSingleton<IRootsService, SampleRootsService>();
        services.AddSingleton<IBrowseService, SampleBrowseService>();
        services.AddSingleton<IOperationsService, SampleOperationsService>();
        services.AddSingleton<ILibraryService, SampleLibraryService>();
        services.AddSingleton<ISettingsService, SampleSettingsService>();
        services.AddSingleton<ISortingSettings, SampleSortingSettings>();
        services.AddSingleton<IThemeService, AppThemeService>();
        services.AddSingleton<HotkeyStatus>();
        services.AddSingleton<IDispatcher, AvaloniaDispatcher>();
        services.AddSingleton(static provider => new Lazy<MainViewModel>(provider.GetRequiredService<MainViewModel>));
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IInfoBarService, InfoBarService>();
        services.AddSingleton<ITipsService, NoTips>();

        // ViewModel страниц живут столько же, сколько приложение: состояние поиска и настроек переживает закрытие окон.
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<BrowseViewModel>();
        services.AddSingleton<CollectionsViewModel>();
        services.AddSingleton(static provider => new SortingViewModel(
            provider.GetRequiredService<ISortingSettings>(),
            provider.GetRequiredService<IBrowseService>(),
            provider.GetRequiredService<IOperationsService>(),
            provider.GetRequiredService<ISearchService>(),
            provider.GetRequiredService<ILibraryService>(),
            provider.GetRequiredService<IRootsService>(),
            provider.GetService<IDialogService>(),
            provider.GetService<IInfoBarService>()));
        services.AddSingleton<TagsViewModel>();
        services.AddSingleton<OperationsViewModel>();
        services.AddSingleton<IOperationsCoordinator>(static provider => provider.GetRequiredService<OperationsViewModel>());
        services.AddSingleton<QuickSearchViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<OnboardingViewModel>();

        return services;
    }
}
