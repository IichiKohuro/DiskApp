using DiskApp.Core.Search;

namespace DiskApp.Core.Tests.Search;

/// <summary>
/// Разбор строки поиска: таблица случаев с канонической формой и round-trip, фильтры и синонимы на RU и EN,
/// отрицания, фразы, регулярные выражения, устойчивость к ошибкам ввода, позиции токенов.
/// </summary>
public sealed class QueryParserTests
{
    /// <summary>Вход → каноническая строка (<see cref="SearchQuery.ToString"/>).</summary>
    public static TheoryData<string, string> Cases { get; } = new()
    {
        { "отчет", "отчет" },
        { "  отчет  ", "отчет" },
        { "годовой отчет", "годовой отчет" },
        { "\"годовой отчет\"", "\"годовой отчет\"" },
        { "\"годовой отчет", "\"годовой отчет\"" },
        { "\"  \"", string.Empty },
        { "\"\"", string.Empty },
        { "-draft", "-draft" },
        { "-\"старая версия\"", "-\"старая версия\"" },
        { "-", "-" },
        { "a - b", "a - b" },
        { "--x", "--x" },
        { "ext:pdf", "ext:pdf" },
        { "EXT:PDF", "ext:PDF" },
        { "ext:.pdf", "ext:.pdf" },
        { "ext:jpg,png", "ext:jpg,png" },
        { "расширение:docx", "ext:docx" },
        { "расш:xlsx", "ext:xlsx" },
        { "-ext:tmp", "-ext:tmp" },
        { "type:image", "type:image" },
        { "type:картинки", "type:картинки" },
        { "тип:видео", "type:видео" },
        { "вид:документы", "type:документы" },
        { "type:image,video", "type:image,video" },
        { "type:unknown", "type:unknown" },
        { "in:Projects", "in:Projects" },
        { "in:\"My Documents\"", "in:\"My Documents\"" },
        { "папка:Загрузки", "in:Загрузки" },
        { "в:Документы", "in:Документы" },
        { @"in:D:\Projects\DiskApp", @"in:D:\Projects\DiskApp" },
        { "root:Work", "root:Work" },
        { "корень:Фото", "root:Фото" },
        { "size:>10mb", "size:>10mb" },
        { "size:<=2гб", "size:<=2гб" },
        { "size:1mb..5mb", "size:1mb..5mb" },
        { "размер:пустые", "size:пустые" },
        { "size:abc", "size:abc" },
        { "modified:week", "modified:week" },
        { "modified:неделя", "modified:неделя" },
        { "изменено:сегодня", "modified:сегодня" },
        { "modified:2026-03", "modified:2026-03" },
        { "modified:>2026-01-01", "modified:>2026-01-01" },
        { "modified:2026-01..2026-03", "modified:2026-01..2026-03" },
        { "mod:3d", "modified:3d" },
        { "created:2025", "created:2025" },
        { "создано:вчера", "created:вчера" },
        { "modified:позавчера", "modified:позавчера" },
        { "is:dir", "is:dir" },
        { "is:папка", "is:папка" },
        { "is:hidden", "is:hidden" },
        { "is:nonsense", "is:nonsense" },
        { "tag:работа", "tag:работа" },
        { "тег:\"важное срочно\"", "tag:\"важное срочно\"" },
        { "fav:", string.Empty },
        { "fav:yes", "fav:yes" },
        { "избранное:да", "fav:да" },
        { "-fav:yes", "-fav:yes" },
        { "ext:", string.Empty },
        { "unknown:value", "unknown:value" },
        { @"D:\Projects", @"D:\Projects" },
        { "http://example.com", "http://example.com" },
        { "/^inv.*\\.pdf$/", "/^inv.*\\.pdf$/" },
        { "/[unclosed/", "/[unclosed/" },
        { "//", "//" },
        { "отчет ext:pdf modified:month -draft", "отчет -draft ext:pdf modified:month" },
        { "\"q1 2026\" type:sheet in:Финансы -tag:архив", "\"q1 2026\" type:sheet in:Финансы -tag:архив" },
        { "ext:pdf ext:docx", "ext:pdf ext:docx" },
        { "Отчёт", "Отчёт" },
        { "фото 🎉", "фото 🎉" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parse_Case_ProducesCanonicalStringAndRoundTrips(string input, string expected)
    {
        // Act
        var query = QueryParser.Parse(input);
        var canonical = query.ToString();
        var reparsed = QueryParser.Parse(canonical);

        // Assert
        canonical.ShouldBe(expected);
        reparsed.Terms.ShouldBe(query.Terms);
        reparsed.Filters.ShouldBe(query.Filters);
        reparsed.RegexPattern.ShouldBe(query.RegexPattern);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrWhitespace_ReturnsEmptyQuery(string? text)
    {
        // Act
        var query = QueryParser.Parse(text);

        // Assert
        query.IsEmpty.ShouldBeTrue();
        query.ShouldBeSameAs(SearchQuery.Empty);
    }

    [Fact]
    public void Parse_MixedQuery_SplitsTermsFiltersAndNegations()
    {
        // Act
        var query = QueryParser.Parse("\"годовой отчет\" 2026 -черновик ext:pdf -ext:tmp in:\"Мои документы\"");

        // Assert
        query.Terms.ShouldBe([
            new SearchTerm("годовой отчет", IsPhrase: true),
            new SearchTerm("2026"),
            new SearchTerm("черновик", IsNegated: true),
        ]);
        query.Filters.ShouldBe([
            new SearchFilter(SearchFilterKey.Extension, "pdf"),
            new SearchFilter(SearchFilterKey.Extension, "tmp", IsNegated: true),
            new SearchFilter(SearchFilterKey.Folder, "Мои документы"),
        ]);
        query.HasPositiveTerms.ShouldBeTrue();
        query.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("size:abc")]
    [InlineData("type:unknown")]
    [InlineData("is:nonsense")]
    [InlineData("modified:позавчера")]
    [InlineData("fav:maybe")]
    public void Parse_InvalidFilterValue_BecomesTermWithWarning(string text)
    {
        // Act
        var query = QueryParser.Parse(text);

        // Assert
        query.Filters.ShouldBeEmpty();
        query.Terms.ShouldHaveSingleItem().Text.ShouldBe(text);
        query.Warnings.ShouldHaveSingleItem().ShouldContain(text);
    }

    [Fact]
    public void Parse_InvalidRegex_BecomesTermWithWarning()
    {
        // Act
        var query = QueryParser.Parse("/[unclosed/");

        // Assert
        query.RegexPattern.ShouldBeNull();
        query.Terms.ShouldHaveSingleItem().Text.ShouldBe("/[unclosed/");
        query.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public void Parse_ValidRegex_StoresPatternWithoutSlashes()
    {
        // Act
        var query = QueryParser.Parse("invoice /^inv.*\\.pdf$/");

        // Assert
        query.RegexPattern.ShouldBe("^inv.*\\.pdf$");
        query.Terms.ShouldHaveSingleItem().Text.ShouldBe("invoice");
        query.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void Parse_IncompleteFilter_IgnoredButTokenKept()
    {
        // Act
        var query = QueryParser.Parse("отчет ext:");

        // Assert
        query.Filters.ShouldBeEmpty();
        query.Terms.ShouldHaveSingleItem().Text.ShouldBe("отчет");
        query.Tokens.Select(static token => token.Kind).ShouldBe([QueryTokenKind.Term, QueryTokenKind.IncompleteFilter]);
        query.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_Tokens_KeepPositionsIncludingNegationAndQuotes()
    {
        // Arrange
        const string text = "  -draft \"two words\" ext:pdf /x+/";

        // Act
        var tokens = QueryParser.Parse(text).Tokens;

        // Assert
        tokens.Select(static token => (token.Kind, token.Start, token.Length)).ShouldBe([
            (QueryTokenKind.Term, 2, 6),
            (QueryTokenKind.Phrase, 9, 11),
            (QueryTokenKind.Filter, 21, 7),
            (QueryTokenKind.Regex, 29, 4),
        ]);
        tokens.ShouldAllBe(token => text.Substring(token.Start, token.Length) == token.Text);
        tokens[1].End.ShouldBe(20);
    }

    [Fact]
    public void Parse_QuotedFilterValueUnclosed_TakesRestOfInput()
    {
        // Act
        var query = QueryParser.Parse("in:\"Мои документы");

        // Assert
        query.Filters.ShouldHaveSingleItem().ShouldBe(new SearchFilter(SearchFilterKey.Folder, "Мои документы"));
    }

    [Theory]
    [InlineData("ext", SearchFilterKey.Extension)]
    [InlineData("TYPE", SearchFilterKey.Kind)]
    [InlineData("Папка", SearchFilterKey.Folder)]
    [InlineData("изменён", SearchFilterKey.Modified)]
    [InlineData("избранное", SearchFilterKey.Favorite)]
    public void TryGetKey_Synonyms_MapToKey(string word, SearchFilterKey expected)
    {
        // Act & Assert
        QueryParser.TryGetKey(word, out var key).ShouldBeTrue();
        key.ShouldBe(expected);
        QueryParser.TryGetKey("http", out _).ShouldBeFalse();
    }

    [Fact]
    public void CanonicalKeys_CoverEveryFilterKey()
    {
        // Act
        var keys = Enum.GetValues<SearchFilterKey>().Select(QueryParser.CanonicalKey).ToArray();

        // Assert
        keys.ShouldBe(QueryParser.CanonicalKeys, ignoreOrder: true);
    }
}
