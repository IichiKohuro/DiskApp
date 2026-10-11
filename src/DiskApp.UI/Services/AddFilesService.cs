using System.Globalization;
using System.Text;

using DiskApp.UI.Models;
using DiskApp.UI.Text;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Services;

/// <summary>
/// Сценарий «Добавить» (PLAN.md, этап 8.5): файлы, брошенные на главную, переданные через «Отправить» или контекстное
/// меню Проводника, и содержимое буфера обмена кладутся в выбранную папку — копированием или перемещением.
/// </summary>
public interface IAddFilesService
{
    /// <summary>Спрашивает место и добавляет пути.</summary>
    Task AddAsync(IReadOnlyList<string> paths);

    /// <summary>Сохраняет текст из буфера обмена в файл (<c>.md</c>, если похоже на Markdown, иначе <c>.txt</c>) и добавляет его.</summary>
    Task AddTextAsync(string text);

    /// <summary>Сохраняет картинку из буфера обмена (<c>.png</c>) и добавляет её.</summary>
    Task AddImageAsync(Func<Stream, Task> savePng);
}

/// <summary>
/// <see cref="IAddFilesService"/> поверх диалога выбора места, очереди операций сервера и загрузки файлов.
/// </summary>
public sealed class AddFilesService(
    ISearchService search,
    IRootsService roots,
    IAddPreferences preferences,
    IFileActions actions,
    IOperationsCoordinator operations,
    IDialogService dialogs,
    IInfoBarService notices,
    Func<DateTimeOffset>? clock = null) : IAddFilesService
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (static () => DateTimeOffset.Now);

    /// <inheritdoc />
    public async Task AddAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var sources = paths.Where(static path => File.Exists(path) || Directory.Exists(path)).ToList();
        if (sources.Count == 0)
        {
            notices.Show("Нечего добавлять", "Файлы не найдены на диске.", FAInfoBarSeverity.Warning);
            return;
        }

        IReadOnlyList<string> rootPaths;
        try
        {
            rootPaths = [.. (await roots.GetRootsAsync()).Select(static root => root.Path)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            notices.Show("Не удалось получить корни", exception.Message, FAInfoBarSeverity.Error);
            return;
        }

        var dialog = new AddFilesViewModel(sources, search, preferences, rootPaths);
        await dialog.LoadAsync();
        if (!await dialogs.ShowAddFilesAsync(dialog) || dialog.SelectedTarget is not { } target)
        {
            return;
        }

        await PutAsync(sources, target.Path, dialog.Move && dialog.CanMove);
        await preferences.RememberTargetAsync(target.Path);
    }

    /// <inheritdoc />
    public async Task AddTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Trim().Length == 0)
        {
            notices.Show("Буфер обмена пуст", "Скопируйте текст, картинку или файлы.", FAInfoBarSeverity.Informational);
            return;
        }

        var extension = LooksLikeMarkdown(text) ? ".md" : ".txt";
        var file = TemporaryFile(TitleOf(text), extension);
        await File.WriteAllTextAsync(file, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await AddTemporaryAsync(file);
    }

    /// <inheritdoc />
    public async Task AddImageAsync(Func<Stream, Task> savePng)
    {
        ArgumentNullException.ThrowIfNull(savePng);
        var file = TemporaryFile("изображение", ".png");
        await using (var stream = File.Create(file))
        {
            await savePng(stream);
        }

        await AddTemporaryAsync(file);
    }

    /// <summary>
    /// Похоже ли на Markdown: заголовки, списки, блоки кода, ссылки.
    /// </summary>
    public static bool LooksLikeMarkdown(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n').Select(static line => line.TrimStart()).ToArray();
        var markers = lines.Count(static line => line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal)
            || line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)
            || line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("> ", StringComparison.Ordinal));
        return markers > 0 || (text.Contains("](", StringComparison.Ordinal) && text.Contains('['));
    }

    /// <summary>
    /// Имя файла по шаблону: <c>{date}</c> — 2026-10-11, <c>{time}</c> — 05-07-31, <c>{title}</c> — начало текста.
    /// </summary>
    public static string FileName(string template, string title, string extension, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(template);
        var name = template
            .Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{time}", now.ToString("HH-mm-ss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{title}", title, StringComparison.Ordinal);
        var clean = new string([.. name.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]).Trim(' ', '.', '_');
        return (clean.Length == 0 ? "буфер" : clean) + extension;
    }

    private static string TitleOf(string text)
    {
        var line = text.Split('\n').Select(static item => item.Trim().TrimStart('#', '-', '*', '>', ' ')).FirstOrDefault(static item => item.Length > 0) ?? "текст";
        return line.Length <= 40 ? line : line[..40].TrimEnd();
    }

    private string TemporaryFile(string title, string extension)
    {
        var folder = Path.Combine(Path.GetTempPath(), "DiskApp", "clipboard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, FileName(preferences.ClipboardNameTemplate, title, extension, _clock()));
    }

    // Файл из буфера создаётся во временной папке и копируется в выбранное место; временная копия удаляется.
    private async Task AddTemporaryAsync(string file)
    {
        try
        {
            await AddAsync([file]);
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
            }
            catch (IOException)
            {
                // Временная папка: удалится вместе с остальным %TEMP%.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task PutAsync(IReadOnlyList<string> sources, string target, bool move)
    {
        var rootPaths = (await roots.GetRootsAsync()).Select(static root => root.Path.TrimEnd('\\', '/')).ToList();
        bool Inside(string path) => rootPaths.Any(root => path.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + '/', StringComparison.OrdinalIgnoreCase));

        var inside = sources.Where(Inside).ToList();
        var outside = sources.Except(inside).ToList();
        if (inside.Count > 0)
        {
            await operations.EnqueueAsync(new NewOperation(move ? FileOperationKind.Move : FileOperationKind.Copy, inside, target));
        }

        if (outside.Count == 0)
        {
            return;
        }

        var files = outside.Where(File.Exists).ToList();
        var skipped = outside.Count - files.Count;
        try
        {
            var saved = files.Count == 0 ? 0 : await actions.CopyIntoAsync(files, new FileEntry { FullPath = target, Kind = FileKind.Folder, Root = string.Empty });
            notices.Show(
                "Добавлено",
                $"{Formatting.Count(saved, "файл", "файла", "файлов")} в «{PathText.FileName(target)}»." + (skipped > 0 ? $" Папки извне пока не копируются: {skipped}." : string.Empty),
                skipped > 0 ? FAInfoBarSeverity.Warning : FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            notices.Show($"Не удалось добавить в «{PathText.FileName(target)}»", exception.Message, FAInfoBarSeverity.Error);
        }
    }
}
