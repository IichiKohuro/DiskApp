using Avalonia;
using Avalonia.Controls;

using DiskApp.UI.Models;

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

    /// <inheritdoc />
    public async Task<ConflictAnswer> ResolveConflictAsync(ConflictInfo conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        var window = mainWindow();
        var applyToAll = new CheckBox { Content = "Применить ко всем конфликтам этой операции" };
        var cancelled = false;
        var cancel = new HyperlinkButton { Content = "Отменить всю операцию", Padding = new Thickness(0) };
        var dialog = new FAContentDialog
        {
            Title = "Файл с таким именем уже есть",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = $"В папке «{Path.GetFileName(Path.GetDirectoryName(conflict.TargetPath))}» уже есть «{Path.GetFileName(conflict.TargetPath)}».", TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 460 },
                    Describe("Новый", conflict.SourceSize, conflict.SourceModified),
                    Describe("Уже есть", conflict.TargetSize, conflict.TargetModified),
                    new TextBlock { Text = "При замене прежний файл уходит в корзину.", Opacity = 0.7, FontSize = 12 },
                    applyToAll,
                    cancel,
                },
            },
            PrimaryButtonText = "Заменить",
            SecondaryButtonText = "Оставить оба",
            CloseButtonText = "Пропустить",
            DefaultButton = FAContentDialogButton.Secondary,
        };
        cancel.Click += (_, _) =>
        {
            cancelled = true;
            dialog.Hide();
        };

        var result = await dialog.ShowAsync(window);
        var choice = cancelled ? ConflictChoice.Cancel
            : result switch
            {
                FAContentDialogResult.Primary => ConflictChoice.Overwrite,
                FAContentDialogResult.Secondary => ConflictChoice.KeepBoth,
                _ => ConflictChoice.Skip,
            };
        return new ConflictAnswer(choice, applyToAll.IsChecked == true);
    }

    private static TextBlock Describe(string label, long size, DateTimeOffset modified)
        => new() { Text = $"{label}: {DiskApp.UI.Text.Formatting.Size(size)}, изменён {modified.ToLocalTime():dd.MM.yyyy HH:mm}" };
}
