using System.Globalization;

using DiskApp.UI.Text;

namespace DiskApp.UI.Tests.Text;

/// <summary>
/// Подписи интерфейса: размеры, относительное время, склонение количеств.
/// </summary>
public sealed class FormattingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(512, "512 Б")]
    [InlineData(1536, "1,5 КБ")]
    [InlineData(10 * 1024 * 1024, "10 МБ")]
    [InlineData(1_300_000_000, "1,2 ГБ")]
    public void Size_Bytes_UsesUnitsAndRussianDecimalComma(long bytes, string expected)
    {
        // Arrange
        using var culture = new CultureScope("ru-RU");

        // Act
        var text = Formatting.Size(bytes);

        // Assert
        text.ShouldBe(expected);
    }

    [Theory]
    [InlineData(30, "только что")]
    [InlineData(60, "1 минуту назад")]
    [InlineData(2 * 60, "2 минуты назад")]
    [InlineData(5 * 60, "5 минут назад")]
    [InlineData(60 * 60, "1 час назад")]
    [InlineData(2 * 60 * 60, "2 часа назад")]
    [InlineData(5 * 60 * 60, "5 часов назад")]
    [InlineData(14 * 60 * 60, "14 часов назад")]
    [InlineData(16 * 60 * 60, "вчера")]
    [InlineData(24 * 60 * 60, "вчера")]
    [InlineData(3 * 24 * 60 * 60, "3 дня назад")]
    [InlineData(20 * 24 * 60 * 60, "20 сен")]
    [InlineData(300 * 24 * 60 * 60, "14.12.2025")]
    public void RelativeTime_Age_ProducesHumanText(int secondsAgo, string expected)
    {
        // Arrange
        using var culture = new CultureScope("ru-RU");
        var value = Now - TimeSpan.FromSeconds(secondsAgo);

        // Act
        var text = Formatting.RelativeTime(value, Now);

        // Assert
        text.ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, "1 файл")]
    [InlineData(2, "2 файла")]
    [InlineData(5, "5 файлов")]
    [InlineData(11, "11 файлов")]
    [InlineData(21, "21 файл")]
    [InlineData(112, "112 файлов")]
    [InlineData(128_412, "128 412 файлов")]
    public void Count_Number_DeclinesWordAndGroupsDigits(long count, string expected)
    {
        // Act
        var text = Formatting.Count(count, "файл", "файла", "файлов");

        // Assert
        text.ShouldBe(expected);
    }

    [Theory]
    [InlineData(0.434, "43 %")]
    [InlineData(1.2, "100 %")]
    [InlineData(-0.5, "0 %")]
    public void Percent_Fraction_ClampsAndRounds(double fraction, string expected)
    {
        // Arrange
        using var culture = new CultureScope("ru-RU");

        // Act
        var text = Formatting.Percent(fraction);

        // Assert
        text.ShouldBe(expected);
    }

    /// <summary>
    /// Временно переключает культуру текущего потока и возвращает прежнюю при освобождении.
    /// </summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous;

        public CultureScope(string name)
        {
            _previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
