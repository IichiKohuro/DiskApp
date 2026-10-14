using System.Buffers.Binary;
using System.Text;

using DiskApp.Infrastructure.Platform.Windows;

namespace DiskApp.Infrastructure.Tests.Platform;

/// <summary>
/// Корзина Windows для истории файлов (PLAN.md, этап 20.2) на синтетической папке корзины: записи <c>$I…</c> обоих форматов
/// читаются, из нескольких удалений одного пути находится последнее, запись без <c>$R…</c> и чужой файл пропускаются;
/// «Восстановить» возвращает файл на место и убирает запись.
/// </summary>
public sealed class WindowsRecycleBinTests : IDisposable
{
    private static readonly DateTimeOffset Deleted = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _directory = new("recycle-bin");

    private string Bin => _directory.Resolve("bin");

    [Fact]
    public void Read_BothRecordVersions_PathSizeAndTime()
    {
        // Arrange
        var original = _directory.Resolve("договор.docx");
        var legacy = Record("$I111111.docx", original, 1234, Deleted, version: 1);
        var current = Record("$I222222.docx", original, 5678, Deleted, version: 2);
        var broken = _directory.CreateFile(Path.Combine("bin", "$I333333.txt"), "мусор");

        // Act & Assert
        WindowsRecycleBin.Read(legacy).ShouldBe(new(original, string.Empty, Deleted, 1234));
        WindowsRecycleBin.Read(current).ShouldBe(new(original, string.Empty, Deleted, 5678));
        WindowsRecycleBin.Read(broken).ShouldBeNull();
    }

    [Fact]
    public void Find_LatestWithStoredFile_SkipsOrphanRecords()
    {
        // Arrange: удаляли дважды; самая свежая запись — без $R (корзину частично очистили)
        var original = _directory.Resolve("акт.pdf");
        Record("$IAAAAAA.pdf", original, 1, Deleted.AddDays(-2), version: 2);
        Record("$IBBBBBB.pdf", original, 2, Deleted, version: 2);
        Record("$ICCCCCC.pdf", original, 3, Deleted.AddDays(1), version: 2, stored: false);
        Record("$IDDDDDD.pdf", _directory.Resolve("другой.pdf"), 4, Deleted.AddDays(2), version: 2);

        // Act
        var found = RecycleBin(original).Find(original.ToUpperInvariant());

        // Assert
        found.ShouldNotBeNull();
        (found.StoredPath, found.Size).ShouldBe((Path.Combine(Bin, "$RBBBBBB.pdf"), 2L));
        RecycleBin(original).Find(_directory.Resolve("нет.pdf")).ShouldBeNull();
    }

    [Fact]
    public void Restore_MovesBackAndRemovesRecord_GoneIsFailure()
    {
        // Arrange
        var original = _directory.Resolve("отчёт.txt");
        var record = Record("$IEEEEEE.txt", original, 5, Deleted, version: 2);
        var recycleBin = RecycleBin(original);
        var stored = recycleBin.Find(original)!.StoredPath;

        // Act
        var restored = recycleBin.Restore(stored, original);
        var again = recycleBin.Restore(stored, original);

        // Assert
        restored.IsSuccess.ShouldBeTrue();
        File.ReadAllText(original).ShouldBe("$R");
        File.Exists(record).ShouldBeFalse();
        again.IsSuccess.ShouldBeFalse();
    }

    public void Dispose() => _directory.Dispose();

    private WindowsRecycleBin RecycleBin(string original) => new(path => string.Equals(path, original, StringComparison.OrdinalIgnoreCase) ? [Bin] : []);

    // Пишет запись $I… и, если нужно, сам удалённый файл $R… с тем же хвостом.
    private string Record(string name, string original, long size, DateTimeOffset deleted, int version, bool stored = true)
    {
        var path = Encoding.Unicode.GetBytes(original + "\0");
        var data = new byte[24 + (version == 1 ? 520 : 4 + path.Length)];
        BinaryPrimitives.WriteInt64LittleEndian(data, version);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), size);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), deleted.ToFileTime());
        if (version == 1)
        {
            path.CopyTo(data, 24);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), path.Length / 2);
            path.CopyTo(data, 28);
        }

        var record = _directory.CreateFile(Path.Combine("bin", name), data);
        if (stored)
        {
            _directory.CreateFile(Path.Combine("bin", "$R" + name[2..]), "$R");
        }

        return record;
    }
}
