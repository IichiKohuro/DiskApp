using Avalonia;
using Avalonia.Controls;

using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Controls;

/// <summary>
/// Цвет иконки пункта навигации из ресурса (Styles/IconColors.axaml): у каждого раздела свой, чтобы находить его
/// взглядом (замечание пользователя, 2026-10-17). Кисть берётся по ключу из ресурсов темы и меняется вместе с ней.
/// При высокой контрастности цвет не задаётся: иконка следует системным цветам, в том числе у выбранного пункта.
/// </summary>
public static class NavIconColor
{
    /// <summary>Ключ ресурса кисти, например <c>NavIconBrush.browse</c>.</summary>
    public static readonly AttachedProperty<string?> ResourceKeyProperty =
        AvaloniaProperty.RegisterAttached<FANavigationViewItem, string?>("ResourceKey", typeof(NavIconColor));

    private static readonly AttachedProperty<IDisposable?> SubscriptionProperty =
        AvaloniaProperty.RegisterAttached<FANavigationViewItem, IDisposable?>("Subscription", typeof(NavIconColor));

    static NavIconColor()
    {
        ResourceKeyProperty.Changed.AddClassHandler<FANavigationViewItem>(static (item, _) => Apply(item));
        FANavigationViewItem.IconSourceProperty.Changed.AddClassHandler<FANavigationViewItem>(static (item, _) => Apply(item));
        ThemeVariantScope.ActualThemeVariantProperty.Changed.AddClassHandler<FANavigationViewItem>(static (item, _) => Apply(item));
    }

    /// <summary>Ключ ресурса кисти иконки.</summary>
    public static string? GetResourceKey(FANavigationViewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.GetValue(ResourceKeyProperty);
    }

    /// <summary>Задаёт ключ ресурса кисти иконки.</summary>
    public static void SetResourceKey(FANavigationViewItem item, string? value)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.SetValue(ResourceKeyProperty, value);
    }

    private static void Apply(FANavigationViewItem item)
    {
        item.GetValue(SubscriptionProperty)?.Dispose();
        item.ClearValue(SubscriptionProperty);

        if (item.IconSource is not { } icon
            || item.GetValue(ResourceKeyProperty) is not { Length: > 0 } key
            || item.ActualThemeVariant == FluentAvaloniaTheme.HighContrastTheme)
        {
            return;
        }

        item.SetValue(SubscriptionProperty, icon.Bind(FAIconSource.ForegroundProperty, item.GetResourceObservable(key)));
    }
}
