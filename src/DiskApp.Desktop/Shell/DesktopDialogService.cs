using Avalonia.Controls;

using DiskApp.UI.Services;

using FluentAvalonia.UI.Controls;

namespace DiskApp.Desktop.Shell;

/// <summary>
/// Диалоги подтверждения <c>ContentDialog</c> над главным окном: если оно скрыто в трее, оно сначала показывается.
/// </summary>
internal sealed class DesktopDialogService(Func<Window> mainWindow) : IDialogService
{
    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, string primaryText, string closeText = "Отмена")
    {
        var window = mainWindow();
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = FAContentDialogButton.Close,
        };

        return await dialog.ShowAsync(window) == FAContentDialogResult.Primary;
    }
}
