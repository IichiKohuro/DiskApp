using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using DiskApp.Contracts;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Search;
using DiskApp.Core.Domain;
using DiskApp.Core.Settings;
using DiskApp.Server.Mapping;

using Contract = DiskApp.Contracts;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Контракты API (PLAN.md, этап 4.2): каждый DTO сериализуется генератором, перечисления совпадают с доменными,
/// JSON в camelCase с перечислениями строками, значения по умолчанию в запросах не теряются, маршруты согласованы.
/// </summary>
public sealed class ContractsTests
{
    public static TheoryData<Type, Type> EnumPairs { get; } = new()
    {
        { typeof(Core.Domain.RootStatus), typeof(Contract.Roots.RootStatus) },
        { typeof(Core.Indexing.IndexerState), typeof(Contract.Roots.IndexerState) },
        { typeof(Core.Indexing.IndexTaskKind), typeof(Contract.Roots.IndexTaskKind) },
        { typeof(Core.Events.IndexPhase), typeof(Contract.Roots.IndexPhase) },
        { typeof(Core.Events.EntryChangeKind), typeof(Contract.Entries.EntryChangeKind) },
        { typeof(Core.Domain.FileKind), typeof(Contract.Entries.FileKind) },
        { typeof(Core.Search.MatchKind), typeof(Contract.Entries.MatchKind) },
        { typeof(Core.Search.QueryTokenKind), typeof(Contract.Search.QueryTokenKind) },
        { typeof(Core.Search.SuggestionKind), typeof(Contract.Search.SuggestionKind) },
        { typeof(Core.Search.SearchSortField), typeof(Contract.Search.SearchSort) },
        { typeof(Core.Search.RankingPreset), typeof(Contract.Search.RankingPreset) },
        { typeof(Core.Domain.CollectionKind), typeof(Contract.Library.CollectionKind) },
        { typeof(Core.Settings.ThemeMode), typeof(Contract.App.ThemeMode) },
        { typeof(Core.Settings.BackdropMode), typeof(Contract.App.BackdropMode) },
        { typeof(Core.Domain.ExclusionKind), typeof(Contract.App.ExclusionKind) },
        { typeof(Core.Settings.SortingRuleMode), typeof(Contract.App.SortingRuleMode) },
        { typeof(Core.Sorting.SortingDecision), typeof(Contract.Operations.SortingDecision) },
        { typeof(Core.Operations.OperationOrigin), typeof(Contract.Operations.OperationOrigin) },
        { typeof(Core.History.FileEventKind), typeof(Contract.History.FileEventKind) },
        { typeof(Core.History.FileEventSource), typeof(Contract.History.FileEventSource) },
    };

    [Fact]
    public void JsonContext_EveryContractType_HasGeneratedMetadata()
    {
        // Arrange
        var types = typeof(DiskAppJsonContext).Assembly.GetExportedTypes()
            .Where(static type => type.Namespace?.StartsWith("DiskApp.Contracts.", StringComparison.Ordinal) == true)
            // Text — общие помощники клиента и сервера (шаблон имени), а не DTO.
            .Where(static type => type.Namespace != "DiskApp.Contracts.Text")
            .Where(static type => type is { IsClass: true, IsAbstract: false } || type.IsEnum)
            .ToArray();

        // Act
        var missing = types.Where(static type => DiskAppJsonContext.Default.GetTypeInfo(type) is null).Select(static type => type.FullName).ToArray();

        // Assert
        types.Length.ShouldBeGreaterThan(30);
        missing.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(EnumPairs))]
    public void Enums_MatchDomainByNameAndOrder(Type domain, Type contract)
    {
        // Act & Assert: перевод приведением полагается на совпадение порядка
        Enum.GetNames(contract).ShouldBe(Enum.GetNames(domain));
        Enum.GetValues(contract).Cast<int>().ShouldBe(Enum.GetValues(domain).Cast<int>());
    }

    [Fact]
    public void Serialize_Entry_UsesCamelCaseStringEnumsAndOmitsNulls()
    {
        // Arrange
        var entry = new EntryDto { Id = 7, Name = "Отчёт.pdf", Kind = Contract.Entries.FileKind.Pdf, IconKey = "ext:pdf" };

        // Act
        var json = JsonSerializer.Serialize(entry, DiskAppJsonContext.Default.EntryDto);

        // Assert
        json.ShouldContain("\"id\":7");
        json.ShouldContain("\"kind\":\"Pdf\"");
        json.ShouldContain("\"iconKey\":\"ext:pdf\"");
        json.ShouldNotContain("score");
        json.ShouldNotContain("nameHighlights");
        json.ShouldNotContain("lastOpenedUtc");
    }

    [Fact]
    public void Deserialize_PartialSearchRequest_KeepsDefaults()
    {
        // Act
        var request = JsonSerializer.Deserialize("""{"q":"отчет ext:pdf"}""", DiskAppJsonContext.Default.SearchRequest);

        // Assert
        request.ShouldNotBeNull();
        request.Q.ShouldBe("отчет ext:pdf");
        request.Limit.ShouldBe(50);
        request.Descending.ShouldBeTrue();
        request.Sort.ShouldBe(SearchSort.Relevance);
        request.Preset.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_EnumByName_IsCaseInsensitive()
    {
        // Act
        var request = JsonSerializer.Deserialize("""{"sort":"modified","preset":"ByName"}""", DiskAppJsonContext.Default.SearchRequest);

        // Assert
        request!.Sort.ShouldBe(SearchSort.Modified);
        request.Preset.ShouldBe(Contract.Search.RankingPreset.ByName);
    }

    [Fact]
    public void Routes_AllVersionedConstants_StartWithPrefix()
    {
        // Arrange
        var routes = typeof(ApiRoutes.V1).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral)
            .Select(static field => (string)field.GetRawConstantValue()!)
            .ToArray();

        // Assert
        routes.Length.ShouldBeGreaterThan(40);
        routes.ShouldAllBe(route => route.StartsWith(ApiRoutes.V1.Prefix, StringComparison.Ordinal));
        routes.Distinct(StringComparer.Ordinal).Count().ShouldBe(routes.Length);
    }

    [Fact]
    public void Routes_ClientHelpers_MatchServerTemplates()
    {
        // Act & Assert
        ApiRoutes.V1.Root(5).ShouldBe("/api/v1/roots/5");
        ApiRoutes.V1.Entry(42).ShouldBe("/api/v1/entries/42");
        ApiRoutes.V1.Tag(3).ShouldBe("/api/v1/tags/3");
        ApiRoutes.V1.Collection(9).ShouldBe("/api/v1/collections/9");
        ApiRoutes.V1.Operation(Guid.Empty).ShouldBe("/api/v1/operations/00000000-0000-0000-0000-000000000000");
        ApiRoutes.V1.RootById.ShouldBe("/api/v1/roots/{id:long}");
    }

    [Fact]
    public void Settings_RoundTripThroughDto_PreservesValues()
    {
        // Arrange
        var settings = AppSettings.Default with
        {
            Appearance = AppSettings.Default.Appearance with { Theme = Core.Settings.ThemeMode.Dark, ReduceMotion = true },
            Search = AppSettings.Default.Search with { RankingPreset = "recent", CandidateLimit = 3_000 },
            Indexing = AppSettings.Default.Indexing with { Exclusions = [ExclusionRule.Glob("**/node_modules"), ExclusionRule.MaxSize(1_000)] },
        };

        // Act
        var dto = settings.ToDto();
        var json = JsonSerializer.Serialize(dto, DiskAppJsonContext.Default.SettingsDto);
        var restored = JsonSerializer.Deserialize(json, DiskAppJsonContext.Default.SettingsDto)!.ToDomain(AppSettings.Default);

        // Assert
        restored.ShouldBe(settings);
        json.ShouldContain("\"theme\":\"Dark\"");
        json.ShouldContain("\"rankingPreset\":\"Recent\"");
    }

    [Theory]
    [InlineData("docs", true, "", "folder")]
    [InlineData("report.PDF", false, "pdf", "ext:pdf")]
    [InlineData("setup.exe", false, "exe", "entry:11")]
    [InlineData("Makefile", false, "", "file")]
    public void IconKey_DependsOnKindAndExtension(string name, bool isDirectory, string extension, string expected)
    {
        // Arrange
        var entry = new FileEntry { Id = 11, RootId = 1, Name = name, RelPath = name, IsDirectory = isDirectory, Extension = extension };

        // Act & Assert
        DtoMapper.IconKey(entry).ShouldBe(expected);
    }

    [Fact]
    public void JsonContext_Options_AreWebDefaults()
    {
        // Act
        var options = DiskAppJsonContext.Default.Options;

        // Assert
        options.PropertyNamingPolicy.ShouldBe(JsonNamingPolicy.CamelCase);
        options.PropertyNameCaseInsensitive.ShouldBeTrue();
        options.DefaultIgnoreCondition.ShouldBe(JsonIgnoreCondition.WhenWritingNull);
    }
}
