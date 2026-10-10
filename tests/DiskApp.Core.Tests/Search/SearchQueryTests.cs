using DiskApp.Core.Domain;
using DiskApp.Core.Search;

namespace DiskApp.Core.Tests.Search;

/// <summary>
/// Модель запроса и результатов: пустой запрос, положительные термы, выборка фильтров по ключу, диапазоны подсветки, страница.
/// </summary>
public sealed class SearchQueryTests
{
    [Fact]
    public void Empty_Query_HasNoTermsFiltersOrRegex()
    {
        // Act
        var query = SearchQuery.Empty;

        // Assert
        query.IsEmpty.ShouldBeTrue();
        query.HasPositiveTerms.ShouldBeFalse();
        query.Limit.ShouldBe(SearchQuery.DefaultLimit);
        query.SortField.ShouldBe(SearchSortField.Relevance);
        query.Cursor.ShouldBeNull();
    }

    [Fact]
    public void HasPositiveTerms_OnlyNegatedTerms_ReturnsFalse()
    {
        // Arrange
        var query = new SearchQuery { Terms = [new SearchTerm("tmp", IsNegated: true)] };

        // Act & Assert
        query.IsEmpty.ShouldBeFalse();
        query.HasPositiveTerms.ShouldBeFalse();
    }

    [Fact]
    public void FiltersOf_Key_ReturnsMatchingFiltersInOrder()
    {
        // Arrange
        var query = new SearchQuery
        {
            Terms = [new SearchTerm("отчёт"), new SearchTerm("годовой отчёт", IsPhrase: true)],
            Filters =
            [
                new SearchFilter(SearchFilterKey.Extension, "pdf"),
                new SearchFilter(SearchFilterKey.Modified, "week"),
                new SearchFilter(SearchFilterKey.Extension, "tmp", IsNegated: true),
            ],
        };

        // Act
        var extensions = query.FiltersOf(SearchFilterKey.Extension).ToArray();

        // Assert
        extensions.Select(static filter => filter.Value).ShouldBe(["pdf", "tmp"]);
        extensions[1].IsNegated.ShouldBeTrue();
        query.FiltersOf(SearchFilterKey.Tag).ShouldBeEmpty();
        query.HasPositiveTerms.ShouldBeTrue();
    }

    [Fact]
    public void TextRange_Overlaps_DetectsIntersectionAndAdjacency()
    {
        // Arrange
        var range = new TextRange(2, 3);

        // Act & Assert
        range.End.ShouldBe(5);
        range.Overlaps(new TextRange(4, 2)).ShouldBeTrue();
        range.Overlaps(new TextRange(5, 1)).ShouldBeFalse();
        range.Overlaps(new TextRange(0, 2)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    public void TextRange_InvalidArguments_Throw(int start, int length)
    {
        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => new TextRange(start, length));
    }

    [Fact]
    public void SearchPage_WithCursor_HasMore()
    {
        // Arrange
        var entry = new FileEntry { RootId = 1, Name = "a.txt", RelPath = "a.txt" };
        var hit = SearchHit.Plain(entry, 1.5);

        // Act
        var page = new SearchPage([hit], 120, new SearchCursor(1.5, entry.Id), TimeSpan.FromMilliseconds(3));

        // Assert
        page.HasMore.ShouldBeTrue();
        page.Hits.ShouldHaveSingleItem().NameHighlights.ShouldBeEmpty();
        SearchPage.Empty.HasMore.ShouldBeFalse();
    }
}
