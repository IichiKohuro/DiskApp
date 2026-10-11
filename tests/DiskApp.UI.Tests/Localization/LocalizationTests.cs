using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;

using DiskApp.UI.Localization;
using DiskApp.UI.Text;

namespace DiskApp.UI.Tests.Localization;

/// <summary>
/// Локализация интерфейса (PLAN.md, этап 9.2): у каждой русской строки есть английская с теми же подстановками,
/// в разметке AXAML нет захардкоженной кириллицы, язык переключается на лету вместе с числами, датами и формами слов.
/// </summary>
[Collection(LanguageTests.Name)]
public sealed partial class LocalizationTests
{
    private static readonly string[] Folders = ["src/DiskApp.UI", "src/DiskApp.Desktop"];

    [Fact]
    public void Resources_EveryRussianStringHasEnglishWithSamePlaceholders()
        => AssertComplete(new ResourceManager("DiskApp.UI.Localization.Strings", typeof(Localizer).Assembly));

    [Fact]
    public void Axaml_HasNoHardcodedCyrillic()
    {
        // Arrange
        var root = RepositoryRoot();
        var files = Folders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.axaml", SearchOption.AllDirectories))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        // Act: кириллица в значениях атрибутов и в тексте между тегами; комментарии не в счёт.
        var problems = new List<string>();
        foreach (var file in files)
        {
            var text = Comment().Replace(File.ReadAllText(file), string.Empty);
            foreach (Match match in Attribute().Matches(text))
            {
                problems.Add($"{Path.GetFileName(file)}: {match.Value}");
            }

            foreach (Match match in Content().Matches(text))
            {
                problems.Add($"{Path.GetFileName(file)}: >{match.Groups[1].Value}<");
            }
        }

        // Assert
        files.Length.ShouldBeGreaterThan(10);
        problems.ShouldBeEmpty();
    }

    [Fact]
    public void English_SwitchesStringsNumbersDatesAndPlurals()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        var changed = 0;
        void OnChanged(object? sender, EventArgs e) => changed++;
        Localizer.Instance.LanguageChanged += OnChanged;

        try
        {
            // Act
            Localizer.Instance.SetLanguage("en");
            var open = L.T("Common.Open");
            var files = Formatting.Count(21, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many"));
            var one = Formatting.Count(1, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many"));
            var number = Formatting.Number(128_412);
            var ago = Formatting.RelativeTime(now.AddMinutes(-5), now);
            var size = Formatting.Size(1536);
            var formatted = L.F("Common.CountTo", "3 files", "Docs");

            // Assert
            open.ShouldBe("Open");
            files.ShouldBe("21 files");
            one.ShouldBe("1 file");
            number.ShouldBe("128,412");
            ago.ShouldBe("5 minutes ago");
            size.ShouldBe("1.5 KB");
            formatted.ShouldBe("3 files to “Docs”.");
            CultureInfo.CurrentUICulture.Name.ShouldBe("en-US");
            changed.ShouldBe(1);
        }
        finally
        {
            Localizer.Instance.LanguageChanged -= OnChanged;
            Localizer.Instance.SetLanguage("ru");
        }

        L.T("Common.Open").ShouldBe("Открыть");
        Formatting.Count(21, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many")).ShouldBe("21 файл");
    }

    [Fact]
    public void Resolve_SystemLanguage_FollowsWindows()
    {
        // Act
        var russian = Localizer.Resolve("ru");
        var english = Localizer.Resolve("en");
        var system = Localizer.Resolve("system");

        // Assert
        russian.Name.ShouldBe("ru-RU");
        english.Name.ShouldBe("en-US");
        system.Name.ShouldBe(CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ru" ? "ru-RU" : "en-US");
    }

    [Fact]
    public void UnknownKey_IsReturnedAsIs()
        => L.T("No.Such.Key").ShouldBe("No.Such.Key");

    /// <summary>Проверяет полноту английских ресурсов сборки: тот же набор ключей и то же число подстановок.</summary>
    internal static void AssertComplete(ResourceManager resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var russian = Entries(resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!);
        var english = Entries(resources.GetResourceSet(CultureInfo.GetCultureInfo("en"), createIfNotExists: true, tryParents: false)!);
        var problems = new List<string>();
        foreach (var (key, value) in russian)
        {
            if (!english.TryGetValue(key, out var translated) || translated.Length == 0)
            {
                problems.Add("нет перевода: " + key);
            }
            else if (Placeholders(value) != Placeholders(translated))
            {
                problems.Add("подстановки не совпадают: " + key);
            }
        }

        russian.Count.ShouldBeGreaterThan(0);
        problems.ShouldBeEmpty();
    }

    private static Dictionary<string, string> Entries(ResourceSet set)
        => set.Cast<DictionaryEntry>().ToDictionary(static entry => (string)entry.Key, static entry => entry.Value as string ?? string.Empty, StringComparer.Ordinal);

    private static int Placeholders(string text)
        => PlaceholderPattern().Matches(text.Replace("{{", string.Empty, StringComparison.Ordinal)).Select(static match => match.Groups[1].Value).Distinct().Count();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DiskApp.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден.");
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex("[\\w.:]+=\"(?!\\{)[^\"]*[\\u0400-\\u04FF][^\"]*\"")]
    private static partial Regex Attribute();

    [GeneratedRegex(">([^<>]*[\\u0400-\\u04FF][^<>]*)<")]
    private static partial Regex Content();

    [GeneratedRegex("\\{(\\d+)[^}]*\\}")]
    private static partial Regex PlaceholderPattern();
}

/// <summary>
/// Тесты, меняющие язык всего процесса, идут отдельно от остальных: иначе параллельные тесты увидели бы чужой язык.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageTests
{
    /// <summary>Имя коллекции.</summary>
    public const string Name = "Language";
}
