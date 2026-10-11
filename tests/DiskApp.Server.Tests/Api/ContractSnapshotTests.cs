using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using DiskApp.Contracts;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Hub;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Operations;
using DiskApp.Contracts.Roots;
using DiskApp.Contracts.Search;
using DiskApp.Core.Settings;
using DiskApp.Server.Mapping;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Снимки JSON-контрактов (PLAN.md, этап 4.7): форма ответов API и событий хаба не меняется незаметно.
/// Изменили контракт намеренно — перезапишите снимки: <c>DISKAPP_UPDATE_SNAPSHOTS=1 dotnet test</c>, проверьте разницу в git.
/// </summary>
public sealed class ContractSnapshotTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 11, 9, 30, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly EntryDto Entry = new()
    {
        Id = 42,
        RootId = 1,
        ParentId = 7,
        Name = "Отчёт за 2026.pdf",
        FullPath = @"D:\Документы\Отчёты\Отчёт за 2026.pdf",
        RelPath = @"Отчёты\Отчёт за 2026.pdf",
        Extension = "pdf",
        Size = 1_234_567,
        CreatedUtc = Stamp.AddDays(-10),
        ModifiedUtc = Stamp.AddDays(-1),
        Kind = FileKind.Pdf,
        IconKey = "ext:pdf",
        IsFavorite = true,
        OpenCount = 3,
        LastOpenedUtc = Stamp,
        Tags = [new TagDto(5, "работа", "#3366FF")],
        Score = 0.92,
        Match = MatchKind.Prefix,
        NameHighlights = [new TextRangeDto(0, 5)],
        PathHighlights = [],
    };

    [Fact]
    public void SearchResponse_MatchesSnapshot()
        => Verify(
            new SearchResponse([Entry], 120, false, "AAAAAAAA8D8qAAAAAAAAAA", 12.5, false, false, ["Корень «Работа» не найден."],
                [new QueryTokenDto(QueryTokenKind.Term, 0, 5, "отчет"), new QueryTokenDto(QueryTokenKind.Filter, 6, 7, "ext:pdf")]),
            DiskAppJsonContext.Default.SearchResponse);

    [Fact]
    public void BrowseResponse_PlainEntry_MatchesSnapshot()
        => Verify(
            new BrowseResponse(@"D:\Документы", 1, null, [Entry with { Score = null, Match = null, NameHighlights = null, PathHighlights = null, Tags = null, IsFavorite = false }], false),
            DiskAppJsonContext.Default.BrowseResponse);

    [Fact]
    public void Roots_MatchSnapshot()
        => Verify(
            new IndexStatusDto(IndexerState.Indexing, false,
            [
                new RootIndexStatusDto(1, @"D:\Документы", "Документы", RootStatus.Scanning, null, IndexTaskKind.FullScan, false,
                    new IndexProgressDto(1, IndexPhase.Writing, 15_000, 14_500, @"D:\Документы\Фото", 2_400.5, 6.25), Stamp),
                new RootIndexStatusDto(2, @"E:\Архив", "Архив", RootStatus.Offline, "Диск отключён.", null, true, null, null),
            ]),
            DiskAppJsonContext.Default.IndexStatusDto);

    [Fact]
    public void Operation_MatchesSnapshot()
        => Verify(
            new OperationDto(
                Guid.Parse("2f3b6c1e-9d7a-4b8e-a1f2-3c4d5e6f7a8b"),
                OperationKind.Copy,
                OperationState.WaitingDecision,
                [@"D:\Входящие\scan.png"],
                @"D:\Документы",
                Stamp,
                null,
                new OperationProgressDto(Guid.Parse("2f3b6c1e-9d7a-4b8e-a1f2-3c4d5e6f7a8b"), 1, 3, 1_048_576, 3_145_728, @"D:\Входящие\scan.png", 52_428_800, 0.04),
                null,
                false,
                new ConflictDto(Guid.Parse("2f3b6c1e-9d7a-4b8e-a1f2-3c4d5e6f7a8b"), @"D:\Входящие\scan.png", @"D:\Документы\scan.png", 1_048_576, Stamp, 900_000, Stamp.AddDays(-30))),
            DiskAppJsonContext.Default.OperationDto);

    [Fact]
    public void HubStatusAndChanges_MatchSnapshot()
        => Verify(
            new JsonObject
            {
                ["status"] = JsonSerializer.SerializeToNode(new HubStatusDto(new IndexStatusDto(IndexerState.Idle, false, []), []), DiskAppJsonContext.Default.HubStatusDto),
                ["changes"] = JsonSerializer.SerializeToNode(new EntriesChangedDto(1, [new EntryChangeDto(EntryChangeKind.Modified, "Отчёты", null, true)]), DiskAppJsonContext.Default.EntriesChangedDto),
                ["completed"] = JsonSerializer.SerializeToNode(new OperationCompletedDto(Guid.Empty, OperationState.Failed, "Нет места на диске."), DiskAppJsonContext.Default.OperationCompletedDto),
            },
            DiskAppJsonContext.Default.JsonObject);

    [Fact]
    public void AutoTags_MatchSnapshot()
        => Verify(
            new JsonObject
            {
                ["entry"] = JsonSerializer.SerializeToNode(
                    Entry with
                    {
                        Score = null,
                        Match = null,
                        NameHighlights = null,
                        PathHighlights = null,
                        InheritedTags = [new InheritedTagDto(9, "ТЗ", "#E74856", @"D:\Документы\417")],
                    },
                    DiskAppJsonContext.Default.EntryDto),
                ["folders"] = JsonSerializer.SerializeToNode<IReadOnlyList<FolderTagsDto>>(
                    [new FolderTagsDto(@"D:\Документы\417", [new TagDto(9, "ТЗ", "#E74856"), new TagDto(10, "417", null)])],
                    DiskAppJsonContext.Default.IReadOnlyListFolderTagsDto),
                ["request"] = JsonSerializer.SerializeToNode(
                    new SetFolderTagsRequest { Path = @"D:\Документы\417", Tags = ["ТЗ", "417"] },
                    DiskAppJsonContext.Default.SetFolderTagsRequest),
            },
            DiskAppJsonContext.Default.JsonObject);

    [Fact]
    public void DefaultSettings_MatchSnapshot()
        => Verify(AppSettings.Default.ToDto(), DiskAppJsonContext.Default.SettingsDto);

    private static void Verify<T>(T value, JsonTypeInfo<T> typeInfo, [CallerMemberName] string name = "", [CallerFilePath] string sourceFile = "")
    {
        var compact = JsonSerializer.Serialize(value, typeInfo);
        var actual = JsonNode.Parse(compact)!.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine(Path.GetDirectoryName(sourceFile)!, "Snapshots", name + ".json");
        if (Environment.GetEnvironmentVariable("DISKAPP_UPDATE_SNAPSHOTS") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }

        var expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        actual.ShouldBe(expected, $"Контракт {name} изменился. Если так задумано — перезапишите снимок (DISKAPP_UPDATE_SNAPSHOTS=1).");
    }
}
