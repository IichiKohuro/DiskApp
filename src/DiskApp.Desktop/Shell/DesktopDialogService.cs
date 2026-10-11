using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

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

    /// <inheritdoc />
    public async Task<bool> ShowAddFilesAsync(DiskApp.UI.ViewModels.AddFilesViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        var window = mainWindow();
        var content = new DiskApp.UI.Views.AddFilesView { DataContext = dialog };
        var box = new FAContentDialog
        {
            Title = "Добавить",
            Content = content,
            PrimaryButtonText = "Добавить",
            CloseButtonText = "Отмена",
            DefaultButton = FAContentDialogButton.Primary,
            IsPrimaryButtonEnabled = dialog.CanAdd,
        };

        // Кнопка доступна, когда выбрано место.
        dialog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiskApp.UI.ViewModels.AddFilesViewModel.CanAdd))
            {
                box.IsPrimaryButtonEnabled = dialog.CanAdd;
            }
        };
        return await box.ShowAsync(window) == FAContentDialogResult.Primary;
    }

    /// <inheritdoc />
    public async Task<bool> ShowTagPickerAsync(DiskApp.UI.ViewModels.TagPickerViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        var box = new FAContentDialog
        {
            Title = "Теги",
            Content = new DiskApp.UI.Views.TagPickerView { DataContext = dialog },
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            DefaultButton = FAContentDialogButton.Primary,
        };
        return await box.ShowAsync(mainWindow()) == FAContentDialogResult.Primary;
    }

    /// <inheritdoc />
    public async Task<(string Name, string? Color)?> EditTagAsync(string title, string name, string? color, string primaryText)
    {
        var input = new TextBox { Text = name, MinWidth = 320, PlaceholderText = "Имя тега" };
        var palette = new ListBox
        {
            ItemsSource = TagPalette.Colors,
            SelectedItem = TagPalette.Colors.FirstOrDefault(candidate => string.Equals(candidate, color, StringComparison.OrdinalIgnoreCase)) ?? TagPalette.Colors[0],
            ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(static () => new WrapPanel()),
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>(static (value, _) => new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(value)),
            }),
            Background = Avalonia.Media.Brushes.Transparent,
        };
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { new TextBlock { Text = "Имя" }, input, new TextBlock { Text = "Цвет" }, palette },
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = "Отмена",
            DefaultButton = FAContentDialogButton.Primary,
            IsPrimaryButtonEnabled = name.Trim().Length > 0,
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = (input.Text ?? string.Empty).Trim().Length > 0;
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return await dialog.ShowAsync(mainWindow()) == FAContentDialogResult.Primary
            ? ((input.Text ?? string.Empty).Trim(), palette.SelectedItem as string)
            : null;
    }

    /// <inheritdoc />
    public async Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        var file = await mainWindow().StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Текстовый файл") { Patterns = ["*.txt"] }],
        });
        return file?.TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<string?> PickOpenFileAsync(string title)
    {
        var files = await mainWindow().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Текстовый файл") { Patterns = ["*.txt"] }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await mainWindow().StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static TextBlock Describe(string label, long size, DateTimeOffset modified)
        => new() { Text = $"{label}: {DiskApp.UI.Text.Formatting.Size(size)}, изменён {modified.ToLocalTime():dd.MM.yyyy HH:mm}" };
}
