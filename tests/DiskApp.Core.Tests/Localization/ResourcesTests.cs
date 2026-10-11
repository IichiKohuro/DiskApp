using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;

namespace DiskApp.Core.Tests.Localization;

/// <summary>
/// Строки сообщений (PLAN.md, этап 9.2): у каждой русской строки есть английская с теми же подстановками.
/// </summary>
public sealed partial class ResourcesTests
{
    [Fact]
    public void EveryRussianStringHasEnglishWithSamePlaceholders()
    {
        // Arrange
        var resources = new ResourceManager("DiskApp.Core.Localization.Strings", typeof(DiskApp.Core.Localization.L).Assembly);

        // Act
        var russian = Entries(resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!);
        var english = Entries(resources.GetResourceSet(CultureInfo.GetCultureInfo("en"), createIfNotExists: true, tryParents: false)!);
        var problems = russian
            .Where(pair => !english.TryGetValue(pair.Key, out var translated) || Placeholders(translated) != Placeholders(pair.Value))
            .Select(static pair => pair.Key)
            .ToList();

        // Assert
        russian.Count.ShouldBeGreaterThan(0);
        problems.ShouldBeEmpty();
    }

    private static Dictionary<string, string> Entries(ResourceSet set)
        => set.Cast<DictionaryEntry>().ToDictionary(static entry => (string)entry.Key, static entry => entry.Value as string ?? string.Empty, StringComparer.Ordinal);

    private static int Placeholders(string text)
        => Placeholder().Matches(text.Replace("{{", string.Empty, StringComparison.Ordinal)).Select(static match => match.Groups[1].Value).Distinct().Count();

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();
}
