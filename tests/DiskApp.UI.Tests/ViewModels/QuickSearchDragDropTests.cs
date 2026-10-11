using Avalonia.Input;

using DiskApp.Client;
using DiskApp.Contracts.Operations;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Tests.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Перетаскивание (PLAN.md, этап 7.6): эффект по модификатору, бросание только на доступную папку, копирование
/// брошенных файлов загрузкой через API и итог в уведомлении.
/// </summary>
public sealed class QuickSearchDragDropTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DiskApp.DragTests", Guid.NewGuid().ToString("N"));

    public QuickSearchDragDropTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(KeyModifiers.None, DragDropEffects.Copy)]
    [InlineData(KeyModifiers.Control, DragDropEffects.Copy)]
    [InlineData(KeyModifiers.Shift, DragDropEffects.Move)]
    [InlineData(KeyModifiers.Alt, DragDropEffects.Link)]
    public void DragEffectsFor_Modifier_CopiesByDefault(KeyModifiers modifiers, DragDropEffects expected)
    {
        // Act & Assert
        QuickSearchViewModel.DragEffectsFor(modifiers).ShouldBe(expected);
    }

    [Fact]
    public void CanDropInto_OnlyAvailableFolders()
    {
        // Arrange
        var folder = Hit(@"D:\Docs\Счета", FileKind.Folder);

        // Act & Assert
        QuickSearchViewModel.CanDropInto(folder).ShouldBeTrue();
        QuickSearchViewModel.CanDropInto(Hit(@"D:\Docs\a.pdf", FileKind.Pdf)).ShouldBeFalse();
        QuickSearchViewModel.CanDropInto(new SearchHit(folder.Entry with { IsOffline = true }, string.Empty, 0, Now)).ShouldBeFalse();
        QuickSearchViewModel.CanDropInto(null).ShouldBeFalse();
    }

    [Fact]
    public async Task CopyInto_Files_CopiesAndReportsCount()
    {
        // Arrange
        var actions = new SampleFileActions();
        using var viewModel = Create(actions);
        viewModel.ShowDropHint = true;
        var folder = Hit(@"D:\Docs\Счета", FileKind.Folder);

        // Act
        await viewModel.CopyIntoAsync(folder, [@"C:\Downloads\a.pdf", @"C:\Downloads\b.pdf"], skippedFolders: 0);

        // Assert
        actions.Calls.ShouldHaveSingleItem().ShouldStartWith(@"CopyInto: D:\Docs\Счета ← ");
        viewModel.ShowDropHint.ShouldBeFalse();
        viewModel.NoticeTitle.ShouldBe("Скопировано");
        viewModel.NoticeMessage.ShouldBe("2 файла в «Счета».");
        viewModel.NoticeSeverity.ShouldBe(FAInfoBarSeverity.Success);
    }

    [Fact]
    public async Task CopyInto_WithFolders_WarnsThatFoldersAreSkipped()
    {
        // Arrange
        var actions = new SampleFileActions();
        using var viewModel = Create(actions);

        // Act
        await viewModel.CopyIntoAsync(Hit(@"D:\Docs", FileKind.Folder), [@"C:\a.txt"], skippedFolders: 2);

        // Assert
        viewModel.NoticeSeverity.ShouldBe(FAInfoBarSeverity.Warning);
        viewModel.NoticeMessage.ShouldContain("2 папки пропущены");
    }

    [Fact]
    public async Task CopyInto_OnlyFolders_ExplainsAndCopiesNothing()
    {
        // Arrange
        var actions = new SampleFileActions();
        using var viewModel = Create(actions);

        // Act
        await viewModel.CopyIntoAsync(Hit(@"D:\Docs", FileKind.Folder), [], skippedFolders: 1);

        // Assert
        actions.Calls.ShouldBeEmpty();
        viewModel.NoticeTitle.ShouldBe("Нечего копировать");
    }

    [Fact]
    public async Task CopyInto_ServerFails_ShowsError()
    {
        // Arrange
        var actions = new SampleFileActions { FailWith = new InvalidOperationException("Папка вне корней.") };
        using var viewModel = Create(actions);

        // Act
        await viewModel.CopyIntoAsync(Hit(@"D:\Docs", FileKind.Folder), [@"C:\a.txt"], skippedFolders: 0);

        // Assert
        viewModel.NoticeSeverity.ShouldBe(FAInfoBarSeverity.Error);
        viewModel.NoticeTitle.ShouldBe("Не удалось скопировать в «Docs»");
    }

    [Fact]
    public async Task ApiCopyInto_ExistingFiles_UploadsWithKeepBoth()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        string? destination = null;
        IReadOnlyList<UploadFile>? uploaded = null;
        ConflictPolicy? policy = null;
        fake.Handlers[nameof(IDiskAppApi.UploadAsync)] = args =>
        {
            destination = (string)args[0]!;
            uploaded = (IReadOnlyList<UploadFile>)args[1]!;
            policy = (ConflictPolicy)args[2]!;
            return Task.FromResult(new UploadResponse([@"D:\Docs\a.txt"], []));
        };
        var file = Path.Combine(_directory, "a.txt");
        await File.WriteAllTextAsync(file, "данные", TestContext.Current.CancellationToken);
        var actions = new ApiFileActions(api, new NullClipboard());

        // Act
        var saved = await actions.CopyIntoAsync([file, Path.Combine(_directory, "missing.txt")], Hit(@"D:\Docs", FileKind.Folder).Entry, TestContext.Current.CancellationToken);

        // Assert
        saved.ShouldBe(1);
        destination.ShouldBe(@"D:\Docs");
        policy.ShouldBe(ConflictPolicy.KeepBoth);
        var upload = uploaded.ShouldHaveSingleItem();
        upload.FileName.ShouldBe("a.txt");
        upload.Length.ShouldBe(new FileInfo(file).Length);
        using var stream = upload.OpenRead();
        stream.Length.ShouldBe(upload.Length!.Value);
    }

    [Fact]
    public async Task ApiCopyInto_NoExistingFiles_SkipsServer()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        var actions = new ApiFileActions(api, new NullClipboard());

        // Act
        var saved = await actions.CopyIntoAsync([Path.Combine(_directory, "missing.txt")], Hit(@"D:\Docs", FileKind.Folder).Entry, TestContext.Current.CancellationToken);

        // Assert
        saved.ShouldBe(0);
        fake.Calls.ShouldBeEmpty();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка теста; удалится вместе с остальным %TEMP%.
        }
    }

    private static SearchHit Hit(string path, FileKind kind)
        => new(new FileEntry { Id = 1, FullPath = path, Kind = kind, Root = "Документы" }, string.Empty, 0, Now);

    private static QuickSearchViewModel Create(SampleFileActions actions)
        => new(new SampleSearchService(static () => Now), actions, static () => Now) { DebounceDelay = TimeSpan.Zero };

    private sealed class NullClipboard : IClipboardService
    {
        public Task SetTextAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
