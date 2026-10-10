using DiskApp.Core.Domain;
using DiskApp.Core.Search;

namespace DiskApp.Core.Tests.Search;

/// <summary>
/// Значения фильтров: размеры с единицами и сравнениями, даты в местном поясе, виды на RU и EN, признаки, избранное,
/// расширения, свёртка текста.
/// </summary>
public sealed class FilterValuesTests
{
    private const long Kb = 1024;
    private const long Mb = 1024 * 1024;
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 1, 30, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.CreateCustomTimeZone("MSK", TimeSpan.FromHours(3), "MSK", "MSK");

    [Theory]
    [InlineData("10mb", 10 * Mb, 11 * Mb - 1)]
    [InlineData("10MB", 10 * Mb, 11 * Mb - 1)]
    [InlineData("1,5мб", 3 * Mb / 2, 5 * Mb / 2 - 1)]
    [InlineData(">10mb", 10 * Mb + 1, long.MaxValue)]
    [InlineData(">=1kb", Kb, long.MaxValue)]
    [InlineData("<500кб", 0, 500 * Kb - 1)]
    [InlineData("<=2k", 0, 2 * Kb)]
    [InlineData("1mb..5mb", Mb, 5 * Mb)]
    [InlineData("100", 100, 100)]
    [InlineData("пустые", 0, 0)]
    [InlineData("empty", 0, 0)]
    public void TryParseSize_Valid_ReturnsRange(string value, long min, long max)
    {
        // Act
        var ok = FilterValues.TryParseSize(value, out var range);

        // Assert
        ok.ShouldBeTrue();
        range.ShouldBe(new SizeRange(min, max));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5mb")]
    [InlineData("5mb..1mb")]
    [InlineData("10xb")]
    public void TryParseSize_Invalid_ReturnsFalse(string value)
    {
        // Act & Assert
        FilterValues.TryParseSize(value, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParseDate_TodayAndYesterday_UseLocalDayBoundaries()
    {
        // Act
        FilterValues.TryParseDate("today", Now, Moscow, out var today).ShouldBeTrue();
        FilterValues.TryParseDate("вчера", Now, Moscow, out var yesterday).ShouldBeTrue();

        // Assert: 01:30 UTC = 04:30 МСК 11 октября; сутки МСК начинаются в 21:00 UTC предыдущего дня
        today.ShouldBe(new DateRange(new DateTimeOffset(2026, 10, 10, 21, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 11, 21, 0, 0, TimeSpan.Zero)));
        yesterday.ShouldBe(new DateRange(new DateTimeOffset(2026, 10, 9, 21, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 10, 21, 0, 0, TimeSpan.Zero)));
        today.Contains(Now).ShouldBeTrue();
        yesterday.Contains(Now).ShouldBeFalse();
    }

    [Theory]
    [InlineData("week", 7)]
    [InlineData("неделя", 7)]
    [InlineData("month", 30)]
    [InlineData("год", 365)]
    [InlineData("3d", 3)]
    [InlineData("2w", 14)]
    [InlineData("6m", 180)]
    [InlineData("1y", 365)]
    [InlineData("5дней", 5)]
    [InlineData("2нед", 14)]
    public void TryParseDate_Relative_StartsBeforeNow(string value, int days)
    {
        // Act
        var ok = FilterValues.TryParseDate(value, Now, Moscow, out var range);

        // Assert
        ok.ShouldBeTrue();
        range.ShouldBe(new DateRange(Now.AddDays(-days), null));
    }

    [Theory]
    [InlineData("2026", "2025-12-31T21:00:00Z", "2026-12-31T21:00:00Z")]
    [InlineData("2026-03", "2026-02-28T21:00:00Z", "2026-03-31T21:00:00Z")]
    [InlineData("2026-03-05", "2026-03-04T21:00:00Z", "2026-03-05T21:00:00Z")]
    [InlineData("05.03.2026", "2026-03-04T21:00:00Z", "2026-03-05T21:00:00Z")]
    [InlineData("2026-01..2026-03", "2025-12-31T21:00:00Z", "2026-03-31T21:00:00Z")]
    public void TryParseDate_Absolute_LocalPeriodInUtc(string value, string from, string to)
    {
        // Act
        var ok = FilterValues.TryParseDate(value, Now, Moscow, out var range);

        // Assert
        ok.ShouldBeTrue();
        range.ShouldBe(new DateRange(DateTimeOffset.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse(to, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void TryParseDate_Comparisons_OpenRanges()
    {
        // Act
        FilterValues.TryParseDate(">2026-01-01", Now, Moscow, out var after).ShouldBeTrue();
        FilterValues.TryParseDate(">=2026-01-01", Now, Moscow, out var since).ShouldBeTrue();
        FilterValues.TryParseDate("<2026-01-01", Now, Moscow, out var before).ShouldBeTrue();
        FilterValues.TryParseDate("<=2026-01-01", Now, Moscow, out var until).ShouldBeTrue();

        // Assert
        var dayStart = new DateTimeOffset(2025, 12, 31, 21, 0, 0, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);
        after.ShouldBe(new DateRange(dayEnd, null));
        since.ShouldBe(new DateRange(dayStart, null));
        before.ShouldBe(new DateRange(null, dayStart));
        until.ShouldBe(new DateRange(null, dayEnd));
    }

    [Theory]
    [InlineData("позавчера")]
    [InlineData("2026-13")]
    [InlineData("0d")]
    [InlineData("2026-03..2026-01")]
    [InlineData("")]
    public void TryParseDate_Invalid_ReturnsFalse(string value)
    {
        // Act & Assert
        FilterValues.TryParseDate(value, Now, Moscow, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("image", new[] { FileKind.Image })]
    [InlineData("Картинки", new[] { FileKind.Image })]
    [InlineData("фото,видео", new[] { FileKind.Image, FileKind.Video })]
    [InlineData("doc", new[] { FileKind.Document })]
    [InlineData("таблицы", new[] { FileKind.Spreadsheet })]
    [InlineData("exe", new[] { FileKind.Executable })]
    [InlineData("папки", new[] { FileKind.Folder })]
    [InlineData("pdf,pdf", new[] { FileKind.Pdf })]
    public void TryParseKinds_Synonyms_MapToKinds(string value, FileKind[] expected)
    {
        // Act
        var ok = FilterValues.TryParseKinds(value, out var kinds);

        // Assert
        ok.ShouldBeTrue();
        kinds.ShouldBe(expected);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("image,unknown")]
    [InlineData(",")]
    public void TryParseKinds_Unknown_ReturnsFalse(string value)
    {
        // Act & Assert
        FilterValues.TryParseKinds(value, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("dir", AttributeFilter.Directory)]
    [InlineData("Папка", AttributeFilter.Directory)]
    [InlineData("file", AttributeFilter.File)]
    [InlineData("скрытые", AttributeFilter.Hidden)]
    [InlineData("fav", AttributeFilter.Favorite)]
    [InlineData("облако", AttributeFilter.CloudPlaceholder)]
    public void TryParseAttribute_Synonyms(string value, AttributeFilter expected)
    {
        // Act & Assert
        FilterValues.TryParseAttribute(value, out var attribute).ShouldBeTrue();
        attribute.ShouldBe(expected);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("yes", true)]
    [InlineData("да", true)]
    [InlineData("no", false)]
    [InlineData("нет", false)]
    public void TryParseFavorite_Values(string value, bool expected)
    {
        // Act & Assert
        FilterValues.TryParseFavorite(value, out var favorite).ShouldBeTrue();
        favorite.ShouldBe(expected);
        FilterValues.TryParseFavorite("maybe", out _).ShouldBeFalse();
    }

    [Fact]
    public void ParseExtensions_CommaListWithDots_LowercaseDistinct()
    {
        // Act & Assert
        FilterValues.ParseExtensions(".JPG, png;jpg").ShouldBe(["jpg", "png"]);
        FilterValues.ParseExtensions(" , ").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Отчёт.DOCX", "отчет.docx")]
    [InlineData("ЁЛКА", "елка")]
    [InlineData("MixedCase", "mixedcase")]
    public void Fold_Text_LowercaseYoToYeSameLength(string text, string expected)
    {
        // Act
        var folded = SearchText.Fold(text);

        // Assert
        folded.ShouldBe(expected);
        folded.Length.ShouldBe(text.Length);
    }

    [Theory]
    [InlineData("QuarterlyReport", 9, true)]
    [InlineData("quarterly_report", 10, true)]
    [InlineData("report", 0, true)]
    [InlineData("report", 2, false)]
    [InlineData("file2026", 4, true)]
    [InlineData("ab", 5, false)]
    public void IsWordStart_Boundaries(string text, int index, bool expected)
    {
        // Act & Assert
        SearchText.IsWordStart(text, index).ShouldBe(expected);
    }
}
