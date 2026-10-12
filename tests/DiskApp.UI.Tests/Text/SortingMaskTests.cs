using DiskApp.UI.Text;

namespace DiskApp.UI.Tests.Text;

/// <summary>
/// Маска шаблона по именам выбранных файлов (PLAN.md, этап 17.5): общее начало до разделителя, у одного файла — первое
/// слово, без общего начала — общее расширение.
/// </summary>
public sealed class SortingMaskTests
{
    [Theory]
    [InlineData("417_ТЗ v3.docx", "417_*")]
    [InlineData("Пароли Brave.csv", "Пароли *")]
    [InlineData("TCI2-отчёт.pdf", "TCI2-*")]
    [InlineData("invoice123.pdf", "invoice*")]
    [InlineData("_черновик план.txt", "_черновик *")]
    [InlineData("a.txt", "a*")]
    public void Suggest_OneFile_FirstWordWithSeparator(string name, string expected)
        => SortingMask.Suggest([name]).ShouldBe(expected);

    [Theory]
    [InlineData("417_ТЗ.pdf", "417_акт.docx", "417_*")]
    [InlineData("Счёт 12.pdf", "счёт 13.pdf", "Счёт *")]
    [InlineData("417.pdf", "417_акт.pdf", "417*")]
    [InlineData("отчёт.pdf", "фото.pdf", "*.pdf")]
    [InlineData("отчёт.pdf", "фото.jpg", "отчёт*")]
    public void Suggest_SeveralFiles_CommonStartOrExtension(string first, string second, string expected)
        => SortingMask.Suggest([first, second]).ShouldBe(expected);

    [Fact]
    public void Suggest_NoNames_IsEmpty() => SortingMask.Suggest([]).ShouldBeEmpty();
}
