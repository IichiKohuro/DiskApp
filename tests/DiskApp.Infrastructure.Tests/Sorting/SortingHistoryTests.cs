using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Operations;
using DiskApp.Core.Paths;
using DiskApp.Core.Settings;
using DiskApp.Core.Sorting;
using DiskApp.Infrastructure.Sqlite;
using DiskApp.Infrastructure.Sqlite.Repositories;
using DiskApp.Infrastructure.Tests.Sqlite.Repositories;

namespace DiskApp.Infrastructure.Tests.Sorting;

/// <summary>
/// Подсказки рабочей папки по истории решений (PLAN.md, этап 17.4) на настоящей базе и папках: после нескольких однотипных
/// переносов нужная папка — первая, объяснение соответствует источнику (слово, расширение, «недавно»), «оставить здесь»
/// гасит подсказку, старые решения уступают новым, пропавшая папка и папка, где файл уже лежит, не предлагаются; история
/// переживает перезапуск и следует за переименованием папки.
/// </summary>
public sealed class SortingHistoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _folder = new("history");
    private readonly TestClock _clock = new(Now);
    private readonly MemorySettingsStore _settings = new();
    private SqliteDatabase _database = null!;

    private string Inbox => Resolve("Загрузки");

    private string Acts => Resolve(Path.Combine("417", "Акты"));

    private string Work417 => Resolve("417");

    private string Tci2 => Resolve("TCI2");

    private string Photos => Resolve("Фото");

    public async ValueTask InitializeAsync()
    {
        foreach (var path in new[] { Inbox, Acts, Tci2, Photos })
        {
            Directory.CreateDirectory(path);
        }

        _settings.Current = new AppSettings { Sorting = new SortingSettings { Inboxes = [new SortingFolder { Path = Inbox }] } };
        _database = new SqliteDatabase(_folder.Resolve("index.db"));
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        _folder.Dispose();
    }

    [Fact]
    public async Task Suggest_AfterThreeMovesWithSameWord_FolderFirstWithWordReason()
    {
        // Arrange
        using var history = History();
        await MoveAsync(history, Acts, "Акт сверки 417.pdf", "Акт приёмки 05.pdf", "акт выполненных работ.docx");
        await MoveAsync(history, Tci2, "схема.pdf");

        // Act
        var suggestion = await SuggestAsync(history, "АКТ 12.pdf");

        // Assert
        suggestion.ShouldBe(new SortingSuggestion(Acts, SortingSuggestionReason.Word, 3, "акт"));
    }

    [Fact]
    public async Task Suggest_SameExtensionOnly_ExplainedByExtension()
    {
        // Arrange
        using var history = History();
        await MoveAsync(history, Photos, "море.jpg", "горы.jpg", "лес.jpg");

        // Act
        var suggestion = await SuggestAsync(history, "закат.jpg");

        // Assert
        suggestion.ShouldBe(new SortingSuggestion(Photos, SortingSuggestionReason.Extension, 3, "jpg"));
    }

    [Fact]
    public async Task Suggest_OneMove_OnlyRecentFromSameInboxForThreeDays()
    {
        // Arrange: одного переноса мало для слова, но это недавняя папка накопителя
        using var history = History();
        await MoveAsync(history, Tci2, "смета.xlsx");

        // Act
        var recent = await SuggestAsync(history, "смета v2.xlsx");
        _clock.UtcNow = Now + SortingHistory.RecentWindow + TimeSpan.FromHours(1);
        var later = await SuggestAsync(history, "смета v2.xlsx");

        // Assert
        recent.ShouldBe(new SortingSuggestion(Tci2, SortingSuggestionReason.Recent, 1, Inbox));
        later.ShouldBeNull();
    }

    [Fact]
    public async Task Suggest_SimilarFilesKeptHere_SuggestsNothingEvenRecent()
    {
        // Arrange: два чека перенесли, три — оставили в «Загрузках»
        using var history = History();
        await MoveAsync(history, Work417, "чек 1.pdf", "чек 2.pdf");
        await history.RecordKeptAsync([Path.Combine(Inbox, "чек магазин.pdf"), Path.Combine(Inbox, "чек кафе.pdf"), Path.Combine(Inbox, "чек такси.pdf")], TestContext.Current.CancellationToken);

        // Act
        var receipt = await SuggestAsync(history, "чек аптека.pdf");
        var other = await SuggestAsync(history, "договор.docx");

        // Assert: похожий файл — без подсказки; непохожий — недавняя папка
        receipt.ShouldBeNull();
        other.ShouldBe(new SortingSuggestion(Work417, SortingSuggestionReason.Recent, 2, Inbox));
    }

    [Fact]
    public async Task Suggest_OldDecisionsFade_FreshFolderWins()
    {
        // Arrange: три отчёта в TCI2 год назад, два в 417 сейчас
        using var history = History();
        _clock.UtcNow = Now.AddYears(-1);
        await MoveAsync(history, Tci2, "отчет 1.docx", "отчет 2.docx", "отчет 3.docx");
        _clock.UtcNow = Now;
        await MoveAsync(history, Work417, "отчет 4.docx", "отчет 5.docx");

        // Act
        var suggestion = await SuggestAsync(history, "отчет 6.docx");

        // Assert
        suggestion.ShouldBe(new SortingSuggestion(Work417, SortingSuggestionReason.Word, 2, "отчет"));
    }

    [Fact]
    public async Task Suggest_FolderGoneOrFileAlreadyThere_NotSuggested()
    {
        // Arrange
        using var history = History();
        await MoveAsync(history, Photos, "море.jpg", "горы.jpg", "лес.jpg");
        await MoveAsync(history, Tci2, "схема 1.pdf", "схема 2.pdf");
        Directory.Delete(Photos);

        // Act
        var gone = await SuggestAsync(history, "закат.jpg");
        var inside = (await history.SuggestAsync([Item(Path.Combine(Tci2, "схема 3.pdf"))], TestContext.Current.CancellationToken))[0];

        // Assert: папки «Фото» нет — недавняя TCI2; схема уже в TCI2 — подсказки нет
        gone.ShouldBe(new SortingSuggestion(Tci2, SortingSuggestionReason.Recent, 2, Inbox));
        inside.ShouldBeNull();
    }

    [Fact]
    public async Task History_SurvivesRestart_AndFollowsFolderRename()
    {
        // Arrange
        using (var first = History())
        {
            await MoveAsync(first, Acts, "акт 1.pdf", "акт 2.pdf");
        }

        var renamed = Resolve(Path.Combine("417 ТЗ", "Акты"));
        Directory.Move(Work417, Resolve("417 ТЗ"));

        // Act
        using var second = History();
        var stale = await SuggestAsync(second, "акт 3.pdf");
        await second.MovePathsAsync([(Work417, Resolve("417 ТЗ"))], TestContext.Current.CancellationToken);
        var moved = await SuggestAsync(second, "акт 3.pdf");
        using var third = History();
        var reloaded = await SuggestAsync(third, "акт 3.pdf");

        // Assert: до обновления путей папки нет — подсказки тоже; после — новый путь, в том числе после перезапуска
        stale.ShouldBeNull();
        moved.ShouldBe(new SortingSuggestion(renamed, SortingSuggestionReason.Word, 2, "акт"));
        reloaded.ShouldBe(moved);
    }

    // Пути в истории нормализованы — так их и сравнивают.
    private string Resolve(string relative) => PathNormalizer.Current.Normalize(_folder.Resolve(relative));

    private SortingHistory History()
        => new(new SqliteSortingStatsRepository(_database), _settings, _clock, PathNormalizer.Current, new System.IO.Abstractions.FileSystem());

    private Task MoveAsync(SortingHistory history, string destination, params string[] names)
        => history.RecordMovesAsync([.. names.Select(name => new MovedItem(Path.Combine(Inbox, name), Path.Combine(destination, name)))], TestContext.Current.CancellationToken);

    private async Task<SortingSuggestion?> SuggestAsync(SortingHistory history, string name)
        => (await history.SuggestAsync([Item(Path.Combine(Inbox, name))], TestContext.Current.CancellationToken))[0];

    private InboxItem Item(string fullPath)
    {
        var entry = FileEntry.Create(1, null, Path.GetFileName(fullPath), false, 100, Now, Now, FileAttributes.Normal);
        return new InboxItem(entry, fullPath, Inbox, InboxItemState.New, Now);
    }

    private sealed class MemorySettingsStore : ISettingsStore<AppSettings>
    {
        public AppSettings Current { get; set; } = new();

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Action<AppSettings> onChanged) => new Unsubscriber();

        private sealed class Unsubscriber : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
