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

    /// <inheritdoc />
    public async Task<string?> PromptAsync(string title, string label, string text, string primaryText, int selectionLength = -1)
    {
        var window = mainWindow();
        var input = new TextBox { Text = text, MinWidth = 360 };
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = label }, input } },
            PrimaryButtonText = primaryText,
            CloseButtonText = "Отмена",
            DefaultButton = FAContentDialogButton.Primary,
        };

        // Поле получает фокус после показа; выделяется имя без расширения, как в Проводнике.
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectionStart = 0;
            input.SelectionEnd = selectionLength < 0 ? text.Length : Math.Min(selectionLength, text.Length);
        };
        return await dialog.ShowAsync(window) == FAContentDialogResult.Primary ? input.Text ?? string.Empty : null;
    }
}
