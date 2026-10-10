using DiskApp.Desktop.SingleInstance;

namespace DiskApp.Desktop.Tests.SingleInstance;

/// <summary>
/// Протокол передачи аргументов между экземплярами: JSON-строка с переводом строки, UTF-8.
/// </summary>
public sealed class InstanceProtocolTests
{
    [Fact]
    public async Task Encode_ThenDecode_RoundTripsArgumentsWithCyrillicAndSpaces()
    {
        // Arrange
        string[] args = ["--search", "отчёт за квартал", "--add", @"C:\Users\Иван\Documents\Договор аренды.pdf"];

        // Act
        var bytes = InstanceProtocol.Encode(args);
        using var stream = new MemoryStream(bytes);
        var decoded = await InstanceProtocol.DecodeAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        bytes[^1].ShouldBe((byte)'\n');
        decoded.ShouldNotBeNull();
        decoded.ShouldBe(args);
    }

    [Fact]
    public async Task DecodeAsync_EmptyStream_ReturnsNull()
    {
        // Arrange
        using var stream = new MemoryStream();

        // Act
        var decoded = await InstanceProtocol.DecodeAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        decoded.ShouldBeNull();
    }

    [Fact]
    public async Task DecodeAsync_MalformedJson_ReturnsNull()
    {
        // Arrange
        using var stream = new MemoryStream("not json\n"u8.ToArray());

        // Act
        var decoded = await InstanceProtocol.DecodeAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        decoded.ShouldBeNull();
    }

    [Fact]
    public async Task DecodeAsync_StreamWithTrailingData_ReadsOnlyFirstLine()
    {
        // Arrange
        var first = InstanceProtocol.Encode(["--show"]);
        var second = InstanceProtocol.Encode(["--settings"]);
        using var stream = new MemoryStream([.. first, .. second]);

        // Act
        var decoded = await InstanceProtocol.DecodeAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        decoded.ShouldBe(["--show"]);
    }
}
