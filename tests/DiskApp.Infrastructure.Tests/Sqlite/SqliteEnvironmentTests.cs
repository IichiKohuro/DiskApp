using Dapper;

using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Tests.Sqlite;

/// <summary>
/// Проверяет, что поставляемая с Microsoft.Data.Sqlite нативная библиотека SQLite пригодна для решения 4 из PLAN.md:
/// доступен FTS5 с токенизатором <c>trigram</c> (SQLite 3.34+), а Dapper работает поверх соединения.
/// </summary>
public sealed class SqliteEnvironmentTests
{
    private const string InMemoryConnectionString = "Data Source=:memory:";

    [Fact]
    public void SqliteVersion_IsAtLeast_3_34()
    {
        // Arrange
        using var connection = new SqliteConnection(InMemoryConnectionString);
        connection.Open();

        // Act
        var versionText = connection.ExecuteScalar<string>("SELECT sqlite_version()");

        // Assert
        var version = Version.Parse(versionText!);
        version.ShouldBeGreaterThanOrEqualTo(new Version(3, 34), $"Токенизатор trigram появился в SQLite 3.34; найдена {versionText}.");
    }

    [Fact]
    public void CompileOptions_IncludeFts5()
    {
        // Arrange
        using var connection = new SqliteConnection(InMemoryConnectionString);
        connection.Open();

        // Act
        var options = connection.Query<string>("PRAGMA compile_options").ToArray();

        // Assert
        options.ShouldContain("ENABLE_FTS5");
    }

    [Fact]
    public void Fts5TrigramTable_SubstringQuery_FindsNameCaseInsensitively()
    {
        // Arrange
        using var connection = new SqliteConnection(InMemoryConnectionString);
        connection.Open();
        connection.Execute("CREATE VIRTUAL TABLE entries USING fts5(name, tokenize = 'trigram')");
        connection.Execute(
            "INSERT INTO entries(name) VALUES (@First), (@Second), (@Third)",
            new { First = "Quarterly Report 2026.pdf", Second = "holiday-photo.jpg", Third = "report_draft.docx" });

        // Act
        var names = connection.Query<string>("SELECT name FROM entries WHERE entries MATCH @Query ORDER BY name", new { Query = "\"port\"" }).ToArray();

        // Assert
        names.ShouldBe(["Quarterly Report 2026.pdf", "report_draft.docx"]);
    }
}
