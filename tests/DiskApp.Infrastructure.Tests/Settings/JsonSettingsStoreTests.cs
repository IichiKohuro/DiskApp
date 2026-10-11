using System.Text.Json;
using System.Text.Json.Nodes;

using DiskApp.Core.Domain;
using DiskApp.Core.Settings;
using DiskApp.Infrastructure.Settings;

namespace DiskApp.Infrastructure.Tests.Settings;

/// <summary>
/// Файл настроек: значения по умолчанию без файла, запись и чтение, атомарность, миграция старой версии,
/// карантин повреждённого файла и восстановление из резервной копии, уведомления подписчиков.
/// </summary>
public sealed class JsonSettingsStoreTests
{
    [Fact]
    public void Load_MissingFile_UsesDefaultsWithoutWriting()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var store = AppSettingsStore.Create(folder.Resolve("settings.json"));

        // Act
        var result = store.Load();

        // Assert
        result.Outcome.ShouldBe(SettingsLoadOutcome.Defaults);
        store.Current.ShouldBe(AppSettings.Default);
        store.Current.Hotkeys.QuickSearch.ShouldBe("Ctrl+Alt+D");
        store.Current.Indexing.Exclusions.ShouldBe(ExclusionRule.Defaults);
        File.Exists(store.Path).ShouldBeFalse();
    }

    [Fact]
    public void Current_BeforeLoad_Throws()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var store = AppSettingsStore.Create(folder.Resolve("settings.json"));

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => store.Current);
    }

    [Fact]
    public async Task SaveAsync_ThenLoad_RoundTripsAndLeavesNoTemporaryFile()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var path = folder.Resolve(Path.Combine("nested", "settings.json"));
        var store = AppSettingsStore.Create(path);
        store.Load();
        var changed = AppSettings.Default with
        {
            Hotkeys = new HotkeySettings { QuickSearch = "Ctrl+Alt+F" },
            Appearance = new AppearanceSettings { Theme = ThemeMode.Dark, Backdrop = BackdropMode.Solid },
            Indexing = new IndexingSettings { Exclusions = [ExclusionRule.Glob("*.bak"), ExclusionRule.MaxSize(1_000)], BatchSize = 2_000 },
            Tray = new TraySettings { StartMinimized = false },
        };

        // Act
        await store.SaveAsync(changed, TestContext.Current.CancellationToken);
        var reloaded = AppSettingsStore.Create(path);
        var result = reloaded.Load();

        // Assert
        result.Outcome.ShouldBe(SettingsLoadOutcome.Loaded);
        result.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        reloaded.Current.ShouldBe(changed);
        store.Current.ShouldBe(changed);
        Directory.GetFiles(Path.GetDirectoryName(path)!).ShouldBe([path]);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["schemaVersion"]!.GetValue<int>().ShouldBe(AppSettings.CurrentSchemaVersion);
        json["appearance"]!["theme"]!.GetValue<string>().ShouldBe("Dark");
    }

    [Fact]
    public void Load_FileWithoutSchemaVersion_MigratesAndRewrites()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var path = folder.CreateFile("settings.json", """{ "hotkeys": { "quickSearch": "Ctrl+Alt+Space" } }""");
        var store = AppSettingsStore.Create(path);

        // Act
        var result = store.Load();

        // Assert
        result.Outcome.ShouldBe(SettingsLoadOutcome.Migrated);
        result.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        store.Current.Hotkeys.QuickSearch.ShouldBe("Ctrl+Alt+Space");
        store.Current.Hotkeys.Triage.ShouldBe("Ctrl+Alt+I", "отсутствующие поля получают значения по умолчанию");
        store.Current.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        JsonNode.Parse(File.ReadAllText(path))!["schemaVersion"]!.GetValue<int>().ShouldBe(AppSettings.CurrentSchemaVersion);
    }

    [Fact]
    public void Load_CustomMigration_IsAppliedBeforeDeserialization()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var path = folder.CreateFile("settings.json", """{ "schemaVersion": 1, "legacyTheme": "Dark" }""");
        var migrations = new Dictionary<int, Action<JsonObject>>
        {
            [1] = static json =>
            {
                var theme = json["legacyTheme"]?.GetValue<string>();
                json.Remove("legacyTheme");
                json["appearance"] = new JsonObject { ["theme"] = theme };
            },
        };
        var store = new JsonSettingsStore<AppSettings>(path, AppSettingsJsonContext.Default.AppSettings, static () => AppSettings.Default, currentSchemaVersion: 2, migrations);

        // Act
        var result = store.Load();

        // Assert
        result.Outcome.ShouldBe(SettingsLoadOutcome.Migrated);
        result.SchemaVersion.ShouldBe(2);
        store.Current.Appearance.Theme.ShouldBe(ThemeMode.Dark);
        store.Current.SchemaVersion.ShouldBe(2);
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "schemaVersion": 1, "search": { "pageSize": "много" } }""")]
    [InlineData("""{ "schemaVersion": "два" }""")]
    public void Load_CorruptFile_QuarantinesAndUsesDefaults(string content)
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var path = folder.CreateFile("settings.json", content);
        var store = AppSettingsStore.Create(path);

        // Act
        var result = store.Load();

        // Assert
        result.Outcome.ShouldBe(SettingsLoadOutcome.Corrupt);
        result.Error.ShouldNotBeNullOrEmpty();
        result.CorruptFilePath.ShouldNotBeNull();
        Path.GetFileName(result.CorruptFilePath).ShouldStartWith("settings.corrupt-");
        File.ReadAllText(result.CorruptFilePath).ShouldBe(content);
        File.Exists(path).ShouldBeFalse();
        store.Current.ShouldBe(AppSettings.Default);
    }

    [Fact]
    public async Task SaveAsync_Twice_KeepsPreviousVersionAsBackup()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var store = AppSettingsStore.Create(folder.Resolve("settings.json"));
        store.Load();
        var first = AppSettings.Default with { Hotkeys = new HotkeySettings { QuickSearch = "Ctrl+Alt+F" } };
        var second = AppSettings.Default with { Hotkeys = new HotkeySettings { QuickSearch = "Ctrl+Alt+G" } };

        // Act
        await store.SaveAsync(first, TestContext.Current.CancellationToken);
        var backupAfterFirst = File.Exists(store.BackupPath);
        await store.SaveAsync(second, TestContext.Current.CancellationToken);

        // Assert
        backupAfterFirst.ShouldBeFalse("до первой записи копировать нечего");
        var backup = JsonNode.Parse(File.ReadAllText(store.BackupPath))!.AsObject();
        backup["hotkeys"]!["quickSearch"]!.GetValue<string>().ShouldBe("Ctrl+Alt+F");
        Directory.GetFiles(folder.FullPath).Order(StringComparer.Ordinal).ShouldBe([store.Path, store.BackupPath]);
    }

    [Fact]
    public async Task Load_CorruptFileWithBackup_RestoresFromBackupAndRewritesFile()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var path = folder.Resolve("settings.json");
        var writer = AppSettingsStore.Create(path);
        writer.Load();
        var saved = AppSettings.Default with { Hotkeys = new HotkeySettings { QuickSearch = "Ctrl+Alt+F" } };
        await writer.SaveAsync(saved, TestContext.Current.CancellationToken);
        await writer.SaveAsync(saved with { Tray = new TraySettings { StartMinimized = false } }, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "{ обрыв записи", TestContext.Current.CancellationToken);
        var store = AppSettingsStore.Create(path);

        // Act
        var result = store.Load();

        // Assert
        result.Outcome.ShouldBe(SettingsLoadOutcome.RestoredFromBackup);
        result.CorruptFilePath.ShouldNotBeNull();
        result.Error.ShouldNotBeNullOrEmpty();
        File.ReadAllText(result.CorruptFilePath).ShouldBe("{ обрыв записи");
        store.Current.ShouldBe(saved);
        AppSettingsStore.Create(path).Load().Outcome.ShouldBe(SettingsLoadOutcome.Loaded);
        File.Exists(store.BackupPath).ShouldBeTrue();
    }

    [Fact]
    public async Task Load_CorruptFileAndBackup_UsesDefaults()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var path = folder.CreateFile("settings.json", "{ испорчен");
        await File.WriteAllTextAsync(path + ".bak", "[]", TestContext.Current.CancellationToken);
        var store = AppSettingsStore.Create(path);

        // Act
        var result = store.Load();

        // Assert
        result.Outcome.ShouldBe(SettingsLoadOutcome.Corrupt);
        store.Current.ShouldBe(AppSettings.Default);
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task Subscribe_OnSave_NotifiesUntilDisposed()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var store = AppSettingsStore.Create(folder.Resolve("settings.json"));
        store.Load();
        var received = new List<string>();
        var subscription = store.Subscribe(settings => received.Add(settings.Hotkeys.QuickSearch));

        // Act
        await store.SaveAsync(AppSettings.Default with { Hotkeys = new HotkeySettings { QuickSearch = "Ctrl+Alt+F" } }, TestContext.Current.CancellationToken);
        subscription.Dispose();
        subscription.Dispose();
        await store.SaveAsync(AppSettings.Default with { Hotkeys = new HotkeySettings { QuickSearch = "Ctrl+Alt+Space" } }, TestContext.Current.CancellationToken);

        // Assert
        received.ShouldBe(["Ctrl+Alt+F"]);
    }

    [Fact]
    public void Serialization_Defaults_UseCamelCaseStringEnumsAndRules()
    {
        // Act
        var json = JsonSerializer.Serialize(AppSettings.Default, AppSettingsJsonContext.Default.AppSettings);
        var node = JsonNode.Parse(json)!.AsObject();

        // Assert
        node["appearance"]!["backdrop"]!.GetValue<string>().ShouldBe("Mica");
        node["indexing"]!["exclusions"]!.AsArray().Count.ShouldBe(ExclusionRule.Defaults.Count);
        node["indexing"]!["exclusions"]![0]!["kind"]!.GetValue<string>().ShouldBe("Glob");
        node["tray"]!["startMinimized"]!.GetValue<bool>().ShouldBeTrue();
    }
}
