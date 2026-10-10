using Avalonia;
using Avalonia.Markup.Xaml;

namespace DiskApp.UI;

/// <summary>
/// Общее приложение Avalonia: тема FluentAvalonia, ресурсы и стили.
/// Окна и платформенные службы (трей, хоткеи, IPC) добавляет головной проект, например <c>DiskApp.Desktop</c>.
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
