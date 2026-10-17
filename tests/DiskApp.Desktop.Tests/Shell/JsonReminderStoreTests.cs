using DiskApp.Desktop.Shell;
using DiskApp.UI.Services;

namespace DiskApp.Desktop.Tests.Shell;

/// <summary>
/// Очередь напоминаний между запусками (PLAN.md, этап 21.2): заявки читаются такими, какими сохранены; испорченный
/// файл или его отсутствие — пустая очередь, без исключений.
/// </summary>
public sealed class JsonReminderStoreTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new("reminders");

    [Fact]
    public void SaveThenLoad_RoundTrip_CorruptOrMissingIsEmpty()
    {
        // Arrange
        var path = _directory.Resolve("reminders.json");
        var due = new DateTimeOffset(2026, 10, 15, 18, 0, 0, TimeSpan.FromHours(3));
        var requests = new[]
        {
            new ReminderRequest("sorting", "sorting", due) { Snoozes = 2 },
            new ReminderRequest("note-1", "reminder", due.AddDays(1), ReminderTiming.Exact) { Title = "Позвонить", Text = "по смете", Payload = @"D:\417\смета.xlsx" },
        };
        var missing = new JsonReminderStore(_directory.Resolve("нет.json")).Load();

        // Act
        new JsonReminderStore(path).Save(requests);
        var loaded = new JsonReminderStore(path).Load();
        File.WriteAllText(path, "{ не json");
        var corrupt = new JsonReminderStore(path).Load();

        // Assert
        loaded.ShouldBe(requests);
        corrupt.ShouldBeEmpty();
        missing.ShouldBeEmpty();
    }

    public void Dispose() => _directory.Dispose();
}
