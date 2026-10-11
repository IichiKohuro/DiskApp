using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Диалог тегов (PLAN.md, этап 8.7): отметки по выбранным файлам, частичная отметка не трогает файлы, новый тег получает
/// свободный цвет палитры и сразу отмечается.
/// </summary>
public sealed class TagPickerViewModelTests
{
    private readonly SampleLibraryService _library = new(static () => new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(3)));

    [Fact]
    public async Task Load_TwoFiles_MarksAllSomeAndNone()
    {
        // Arrange
        var work = await _library.CreateTagAsync("работа-тест", null, TestContext.Current.CancellationToken);
        var home = await _library.CreateTagAsync("дом-тест", null, TestContext.Current.CancellationToken);
        await _library.CreateTagAsync("пусто-тест", null, TestContext.Current.CancellationToken);
        var a = File(1, "работа-тест", "дом-тест");
        var b = File(2, "работа-тест");
        var picker = new TagPickerViewModel([a, b], _library);

        // Act
        await picker.LoadAsync();

        // Assert
        Choice(picker, work.Name).IsChecked.ShouldBe(true);
        Choice(picker, home.Name).IsChecked.ShouldBeNull();
        Choice(picker, home.Name).IsThreeState.ShouldBeTrue();
        Choice(picker, "пусто-тест").IsChecked.ShouldBe(false);
        picker.TargetText.ShouldBe("Теги для 2 файлов");
    }

    [Fact]
    public async Task Apply_PartialLeftAsIs_OnlyChangedFilesWritten()
    {
        // Arrange
        await _library.CreateTagAsync("работа-тест", null, TestContext.Current.CancellationToken);
        await _library.CreateTagAsync("дом-тест", null, TestContext.Current.CancellationToken);
        var a = File(1, "работа-тест", "дом-тест");
        var b = File(2, "работа-тест");
        var picker = new TagPickerViewModel([a, b], _library);
        await picker.LoadAsync();

        // Act: «дом» у части файлов не трогаем, «работу» снимаем со всех.
        Choice(picker, "работа-тест").IsChecked = false;
        var updated = await picker.ApplyAsync();

        // Assert
        updated.Select(static entry => (entry.Id, string.Join(",", entry.Tags))).ShouldBe([(1L, "дом-тест"), (2L, string.Empty)]);
    }

    [Fact]
    public async Task Apply_NothingChanged_WritesNothing()
    {
        // Arrange
        await _library.CreateTagAsync("работа-тест", null, TestContext.Current.CancellationToken);
        var picker = new TagPickerViewModel([File(1, "работа-тест")], _library);
        await picker.LoadAsync();

        // Act
        var updated = await picker.ApplyAsync();

        // Assert
        updated.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddTag_New_CreatesWithFreePaletteColorAndChecks()
    {
        // Arrange
        var picker = new TagPickerViewModel([File(1)], _library);
        await picker.LoadAsync();
        var used = picker.Choices.Select(static choice => choice.Tag.Color).ToArray();
        picker.NewTagName = "  поездка  ";

        // Act
        await picker.AddTagCommand.ExecuteAsync(null);

        // Assert
        var added = Choice(picker, "поездка");
        added.IsChecked.ShouldBe(true);
        added.Tag.Color.ShouldBe(TagPalette.Next(used));
        picker.NewTagName.ShouldBeEmpty();
        (await picker.ApplyAsync()).ShouldHaveSingleItem().Tags.ShouldBe(["поездка"]);
    }

    [Fact]
    public async Task AddTag_ExistingName_ChecksItInsteadOfDuplicate()
    {
        // Arrange
        await _library.CreateTagAsync("Работа-тест", null, TestContext.Current.CancellationToken);
        var picker = new TagPickerViewModel([File(1)], _library);
        await picker.LoadAsync();
        var count = picker.Choices.Count;
        picker.NewTagName = "работа-тест";

        // Act
        await picker.AddTagCommand.ExecuteAsync(null);

        // Assert
        picker.Choices.Count.ShouldBe(count);
        Choice(picker, "Работа-тест").IsChecked.ShouldBe(true);
        picker.HasError.ShouldBeFalse();
    }

    [Fact]
    public void Palette_Next_SkipsUsedColorsCaseInsensitive()
    {
        // Act
        var next = TagPalette.Next([TagPalette.Colors[0].ToLowerInvariant(), null, TagPalette.Colors[1]]);

        // Assert
        next.ShouldBe(TagPalette.Colors[2]);
    }

    private static TagChoice Choice(TagPickerViewModel picker, string name)
        => picker.Choices.Single(choice => choice.Tag.Name == name);

    private static FileEntry File(long id, params string[] tags)
        => new() { Id = id, FullPath = $@"D:\Docs\file{id}.txt", Kind = FileKind.Text, Root = "Документы", Tags = tags };
}
