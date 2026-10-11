using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;


namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Файловые операции «Обзора» (PLAN.md, этап 8.4): вырезать, копировать и вставить, файлы извне, новая папка
/// и файл по шаблону, защита от переноса папки в саму себя.
/// </summary>
public sealed class BrowseOperationsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));
    private const string DiskApp = @"D:\Projects\DiskApp";
    private const string Portfolio = @"D:\Projects\Portfolio";

    private readonly SampleOperationsService _queue = new();
    private readonly SampleFileActions _actions = new();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "DiskApp.BrowseOps", Guid.NewGuid().ToString("N"));

    public BrowseOperationsTests() => Directory.CreateDirectory(_outside);

    [Fact]
    public async Task CopyThenPaste_InsideRoots_EnqueuesCopy()
    {
        // Arrange
        var browse = await CreateAsync(DiskApp);
        var plan = browse.Items.Single(static hit => hit.Entry.Name == "PLAN.md");
        browse.UpdateSelection([plan]);
        await browse.CopyFilesCommand.ExecuteAsync(null);
        await browse.NavigateAsync(Portfolio);

        // Act
        await browse.PasteAsync();

        // Assert
        var operation = (await _queue.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        operation.Kind.ShouldBe(FileOperationKind.Copy);
        operation.Sources.ShouldBe([plan.Entry.FullPath]);
        operation.Destination.ShouldBe(Portfolio);
        browse.HasClipboard.ShouldBeTrue("скопированное можно вставить ещё раз");
    }

    [Fact]
    public async Task CutThenPaste_MovesAndClearsClipboard()
    {
        // Arrange
        var browse = await CreateAsync(DiskApp);
        browse.UpdateSelection([browse.Items.Single(static hit => hit.Entry.Name == "README.md")]);
        await browse.CutCommand.ExecuteAsync(null);
        await browse.NavigateAsync(Portfolio);

        // Act
        await browse.PasteAsync();

        // Assert
        (await _queue.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem().Kind.ShouldBe(FileOperationKind.Move);
        browse.HasClipboard.ShouldBeFalse();
    }

    [Fact]
    public async Task Paste_SystemFilesFromOutside_CopiesByUpload()
    {
        // Arrange
        var browse = await CreateAsync(Portfolio);
        var file = Path.Combine(_outside, "скан.pdf");
        await File.WriteAllTextAsync(file, "pdf", TestContext.Current.CancellationToken);

        // Act
        await browse.PasteAsync([file, _outside]);

        // Assert
        _actions.Calls.ShouldHaveSingleItem().ShouldBe($@"CopyInto: {Portfolio} ← {file}");
        (await _queue.GetAllAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        _notices.Received(1).Show("Скопировано", Arg.Is<string>(text => text.Contains("Папки извне пока не копируются: 1")), FAInfoBarSeverity.Warning);
    }

    [Fact]
    public async Task Paste_Nothing_Explains()
    {
        // Arrange
        var browse = await CreateAsync(Portfolio);

        // Act
        await browse.PasteAsync();

        // Assert
        _notices.Received(1).Show("Нечего вставить", Arg.Any<string>(), FAInfoBarSeverity.Informational);
    }

    [Fact]
    public async Task Transfer_FolderIntoItselfOrSameFolder_IsRefused()
    {
        // Arrange
        var browse = await CreateAsync(DiskApp);

        // Act
        await browse.TransferAsync([DiskApp], DiskApp + @"\src", move: true);
        await browse.TransferAsync([DiskApp + @"\PLAN.md"], DiskApp, move: true);

        // Assert
        (await _queue.GetAllAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        _notices.Received(2).Show("Некуда переносить", Arg.Any<string>(), FAInfoBarSeverity.Warning);
    }

    [Fact]
    public async Task NewFolder_Name_EnqueuesCreateFolder()
    {
        // Arrange
        var browse = await CreateAsync(DiskApp);
        _dialogs.PromptAsync(default!, default!, default!, default!, default).ReturnsForAnyArgs(Task.FromResult<string?>("Сканы 2026"));

        // Act
        await browse.NewFolderCommand.ExecuteAsync(null);

        // Assert
        await _dialogs.Received(1).PromptAsync("Новая папка", "Имя в папке «DiskApp»", "Новая папка", "Создать", "Новая папка".Length);
        var operation = (await _queue.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        operation.Kind.ShouldBe(FileOperationKind.CreateFolder);
        operation.Destination.ShouldBe(DiskApp);
        operation.Message.ShouldBe("Сканы 2026", "макет очереди хранит имя в сообщении");
    }

    [Fact]
    public async Task NewFile_Template_SuggestsExtensionAndValidatesName()
    {
        // Arrange
        var browse = await CreateAsync(DiskApp);
        _dialogs.PromptAsync(default!, default!, default!, default!, default).ReturnsForAnyArgs(Task.FromResult<string?>("плохое:имя.md"), Task.FromResult<string?>("План.md"));
        var markdown = browse.FileTemplates.Single(static template => template.Extension == ".md");

        // Act
        await browse.NewFileCommand.ExecuteAsync(markdown);
        await browse.NewFileCommand.ExecuteAsync(markdown);

        // Assert
        await _dialogs.Received(2).PromptAsync("Новый файл", "Имя в папке «DiskApp»", "Заметка Markdown.md", "Создать", "Заметка Markdown".Length);
        _notices.Received(1).Show("Такое имя не подходит", Arg.Any<string>(), FAInfoBarSeverity.Warning);
        var operation = (await _queue.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        operation.Kind.ShouldBe(FileOperationKind.CreateFile);
        operation.Message.ShouldBe("План.md");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_outside, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка теста.
        }
    }

    private async Task<BrowseViewModel> CreateAsync(string folder)
    {
        var coordinator = new OperationsViewModel(_queue, new ImmediateDispatcher(), _dialogs, _notices);
        var browse = new BrowseViewModel(new SampleBrowseService(static () => Now), new SampleRootsService(), new SampleSearchService(static () => Now), _actions, _dialogs, _notices, coordinator, static () => Now);
        await browse.Initialization;
        await browse.NavigateAsync(folder);
        return browse;
    }
}
