using System.Net;
using System.Text;

using DiskApp.Contracts;
using DiskApp.Contracts.App;
using DiskApp.Contracts.Search;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Настройки через API (JSON Merge Patch с проверкой), сведения о приложении, OpenAPI только при разработке.
/// </summary>
public sealed class SettingsApiTests
{
    [Fact]
    public async Task Get_ReturnsDefaults()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();

        // Act
        var settings = await context.GetAsync(ApiRoutes.V1.Settings, DiskAppJsonContext.Default.SettingsDto);

        // Assert
        settings.Search.PageSize.ShouldBe(50);
        settings.Search.CandidateLimit.ShouldBe(2_000);
        settings.Search.RankingPreset.ShouldBe(RankingPreset.Balanced);
        settings.Hotkeys.QuickSearch.ShouldBe("Ctrl+Alt+D");
        settings.Indexing.Exclusions.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Patch_PartialChange_SavesAndKeepsOtherValues()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();

        // Act
        using var response = await PatchAsync(context, """{"search":{"pageSize":100,"rankingPreset":"Recent"},"Appearance":{"theme":"Dark"}}""");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var settings = await context.GetAsync(ApiRoutes.V1.Settings, DiskAppJsonContext.Default.SettingsDto);
        var file = await File.ReadAllTextAsync(context.Data.Resolve("settings.json"), TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        settings.Search.PageSize.ShouldBe(100);
        settings.Search.RankingPreset.ShouldBe(RankingPreset.Recent);
        settings.Appearance.Theme.ShouldBe(ThemeMode.Dark);
        settings.Search.HistoryLimit.ShouldBe(100);
        settings.Tray.CloseToTray.ShouldBeTrue();
        file.ShouldContain("\"pageSize\": 100");
        file.ShouldContain("\"rankingPreset\": \"recent\"");
    }

    [Fact]
    public async Task Patch_Null_RestoresDefault()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        (await PatchAsync(context, """{"search":{"pageSize":200}}""")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        using var response = await PatchAsync(context, """{"search":{"pageSize":null}}""");
        var settings = await context.GetAsync(ApiRoutes.V1.Settings, DiskAppJsonContext.Default.SettingsDto);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        settings.Search.PageSize.ShouldBe(50);
    }

    [Fact]
    public async Task Patch_StageNineFields_IncludingNullableOnes_AreSaved()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.FullPath.Replace(@"\", @"\\", StringComparison.Ordinal);

        // Act: поля без значения по умолчанию (accentColor, inboxPath) тоже принимаются.
        using var response = await PatchAsync(context, $$$"""
            {"appearance":{"useSystemAccent":false,"accentColor":"#E74856","fontScale":1.25,"compact":true},
             "search":{"fuzzy":false,"showSuggestionsOnEmpty":false},
             "popup":{"monitor":"Primary","hideOnDeactivate":false,"showSize":false},
             "add":{"inboxPath":"{{{inbox}}}"},
             "experimental":{"quickSwitch":true},
             "hotkeys":{"quickSwitch":"Ctrl+Alt+Q"},
             "diagnostics":{"logLevel":"Debug"}}
            """);
        var settings = await context.GetAsync(ApiRoutes.V1.Settings, DiskAppJsonContext.Default.SettingsDto);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        settings.Appearance.AccentColor.ShouldBe("#E74856");
        settings.Appearance.FontScale.ShouldBe(1.25);
        settings.Appearance.Compact.ShouldBeTrue();
        settings.Search.Fuzzy.ShouldBeFalse();
        settings.Search.ShowSuggestionsOnEmpty.ShouldBeFalse();
        settings.Popup.Monitor.ShouldBe(DiskApp.Contracts.App.PopupMonitor.Primary);
        settings.Popup.HideOnDeactivate.ShouldBeFalse();
        settings.Popup.ShowSize.ShouldBeFalse();
        settings.Popup.ShowPath.ShouldBeTrue();
        settings.Add.InboxPath.ShouldBe(context.Files.FullPath);
        settings.Experimental.QuickSwitch.ShouldBeTrue();
        settings.Hotkeys.QuickSwitch.ShouldBe("Ctrl+Alt+Q");
        settings.Diagnostics.LogLevel.ShouldBe("Debug");
    }

    [Theory]
    [InlineData("""{"search":{"pageSiz":10}}""", "search.pageSiz")]
    [InlineData("""{"search":{"pageSize":5}}""", "search.pageSize")]
    [InlineData("""{"search":{"pageSize":"много"}}""", "")]
    [InlineData("""{"indexing":{"exclusions":[{"kind":"Size","pattern":"abc"}]}}""", "indexing.exclusions[0].pattern")]
    [InlineData("""{"appearance":{"fontScale":3}}""", "appearance.fontScale")]
    [InlineData("""{"appearance":{"accentColor":"red"}}""", "appearance.accentColor")]
    [InlineData("""{"diagnostics":{"logLevel":"Verbose"}}""", "diagnostics.logLevel")]
    [InlineData("""{"add":{"inboxPath":"relative\\path"}}""", "add.inboxPath")]
    public async Task Patch_InvalidInput_Returns400AndKeepsSettings(string json, string field)
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();

        // Act
        using var response = await PatchAsync(context, json);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var settings = await context.GetAsync(ApiRoutes.V1.Settings, DiskAppJsonContext.Default.SettingsDto);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldContain(field);
        settings.Search.PageSize.ShouldBe(50);
        File.Exists(context.Data.Resolve("settings.json")).ShouldBeFalse("неверный запрос ничего не сохраняет");
    }

    [Fact]
    public async Task AppInfo_DescribesInstallation()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();

        // Act
        var info = await context.GetAsync(ApiRoutes.V1.AppInfo, DiskAppJsonContext.Default.AppInfoDto);

        // Assert
        info.Name.ShouldBe("DiskApp");
        info.DataDirectory.ShouldBe(context.Data.FullPath);
        info.Runtime.ShouldStartWith(".NET");
        info.IsPortable.ShouldBeFalse();
    }

    [Fact]
    public async Task OpenApi_OnlyInDevelopment_DescribesEndpoints()
    {
        // Arrange
        await using var development = await ServerTestContext.StartAsync(withPlatform: true, environment: "Development");
        await using var production = await ServerTestContext.StartAsync();

        // Act
        var document = await development.Client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var scalar = await development.Client.GetAsync(new Uri("/scalar/v1", UriKind.Relative), TestContext.Current.CancellationToken);
        var hidden = await production.Client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);

        // Assert
        document.ShouldContain("/api/v1/search");
        document.ShouldContain("/api/v1/roots/{id}");
        document.ShouldContain("Пересобрать индекс");
        scalar.StatusCode.ShouldBe(HttpStatusCode.OK);
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static Task<HttpResponseMessage> PatchAsync(ServerTestContext context, string json)
        => context.Client.PatchAsync(
            new Uri(ApiRoutes.V1.Settings, UriKind.Relative),
            new StringContent(json, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
}
