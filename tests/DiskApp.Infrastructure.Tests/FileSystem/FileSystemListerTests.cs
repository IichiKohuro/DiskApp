using DiskApp.Core.FileSystem;
using DiskApp.Infrastructure.FileSystem;

namespace DiskApp.Infrastructure.Tests.FileSystem;

/// <summary>
/// Листинг и сведения о путях: метаданные, отсутствие против ошибки доступа, идентичность папок, тип накопителя, фоновый приоритет.
/// </summary>
public sealed class FileSystemListerTests
{
    [Fact]
    public void List_Folder_ReturnsChildrenWithMetadata()
    {
        // Arrange
        using var folder = new TemporaryDirectory("lister");
        var file = folder.CreateFile("a.txt", "12345");
        folder.CreateDirectory("sub");
        var modified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, modified);

        // Act
        var listing = FileSystemLister.Instance.List(folder.FullPath);

        // Assert
        listing.Succeeded.ShouldBeTrue();
        listing.Items.Select(static item => item.Name).ShouldBe(["a.txt", "sub"], ignoreOrder: true);
        var item = listing.Items.Single(static item => item.Name == "a.txt");
        item.IsDirectory.ShouldBeFalse();
        item.Size.ShouldBe(5);
        item.ModifiedUtc.ShouldBe(new DateTimeOffset(modified));
        item.IsCloudPlaceholder.ShouldBeFalse();
        listing.Items.Single(static item => item.Name == "sub").Size.ShouldBe(0);
    }

    [Fact]
    public void List_MissingFolder_ReturnsError()
    {
        // Arrange
        using var folder = new TemporaryDirectory("lister");

        // Act
        var listing = FileSystemLister.Instance.List(folder.Resolve("missing"));

        // Assert
        listing.Succeeded.ShouldBeFalse();
        listing.Items.ShouldBeEmpty();
        listing.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Stat_FileFolderMissing_DistinguishesCases()
    {
        // Arrange
        using var folder = new TemporaryDirectory("lister");
        var file = folder.CreateFile("a.txt", "abc");
        var directory = folder.CreateDirectory("sub");

        // Act
        var fileStat = FileSystemLister.Instance.Stat(file);
        var directoryStat = FileSystemLister.Instance.Stat(directory);
        var missing = FileSystemLister.Instance.Stat(folder.Resolve("nope.txt"));

        // Assert
        fileStat.Exists.ShouldBeTrue();
        fileStat.Item!.Value.Size.ShouldBe(3);
        fileStat.Item.Value.Name.ShouldBe("a.txt");
        directoryStat.Item!.Value.IsDirectory.ShouldBeTrue();
        missing.IsMissing.ShouldBeTrue();
        missing.Exists.ShouldBeFalse();
    }

    [Fact]
    public void StatAndList_SameFile_AgreeOnMetadata()
    {
        // Arrange: наблюдатель берёт сведения через Stat, сканер — через List; расхождение дало бы лишние обновления индекса
        using var folder = new TemporaryDirectory("lister");
        var file = folder.CreateFile("same.bin", new byte[1234]);

        // Act
        var fromStat = FileSystemLister.Instance.Stat(file).Item!.Value;
        var fromList = FileSystemLister.Instance.List(folder.FullPath).Items.Single();

        // Assert
        fromStat.ShouldBe(fromList);
    }

    [Fact]
    public void GetDirectoryIdentity_JunctionAndTarget_AreEqual()
    {
        // Arrange
        using var folder = new TemporaryDirectory("lister");
        folder.CreateDirectory("target");
        folder.CreateDirectory("other");
        if (!TestTree.TryCreateJunction(folder, "link", "target"))
        {
            Assert.Skip("Junction недоступны на этой ОС.");
        }

        // Act
        var target = FileSystemLister.Instance.GetDirectoryIdentity(folder.Resolve("target"));
        var link = FileSystemLister.Instance.GetDirectoryIdentity(folder.Resolve("link"));
        var other = FileSystemLister.Instance.GetDirectoryIdentity(folder.Resolve("other"));
        var missing = FileSystemLister.Instance.GetDirectoryIdentity(folder.Resolve("missing"));

        // Assert
        target.ShouldNotBeNull();
        link.ShouldBe(target);
        other.ShouldNotBe(target);
        missing.ShouldBeNull();
    }

    [Fact]
    public void DirectoryExists_ExistingAndMissing()
    {
        // Arrange
        using var folder = new TemporaryDirectory("lister");

        // Act & Assert
        FileSystemLister.Instance.DirectoryExists(folder.FullPath).ShouldBeTrue();
        FileSystemLister.Instance.DirectoryExists(folder.Resolve("missing")).ShouldBeFalse();
    }

    [Fact]
    public void DriveInfoProvider_TempFolderAndUnc_DescribeDrive()
    {
        // Arrange
        var provider = new DriveInfoProvider();

        // Act
        var temp = provider.Describe(Path.GetTempPath());
        var again = provider.Describe(Path.GetTempPath());
        var unc = provider.Describe(@"\\NAS\media\movies");

        // Assert
        temp.DiskKey.ShouldNotBeNullOrEmpty();
        again.ShouldBe(temp);
        temp.RecommendedParallelism.ShouldBeInRange(1, 4);
        unc.ShouldBe(new DriveDescription("net:nas", DriveMedia.Network));
        unc.RecommendedParallelism.ShouldBe(2);
        if (OperatingSystem.IsWindows())
        {
            temp.Media.ShouldBeOneOf(DriveMedia.SolidState, DriveMedia.Rotational, DriveMedia.Unknown);
        }
    }

    [Fact]
    public async Task ThreadBackgroundPriority_EnterAndDispose_RestoresPriority()
    {
        // Arrange
        ThreadPriority inside = default;
        ThreadPriority after = default;

        // Act
        await Task.Factory.StartNew(
            () =>
            {
                using (ThreadBackgroundPriority.Instance.Enter())
                {
                    inside = Thread.CurrentThread.Priority;
                }

                after = Thread.CurrentThread.Priority;
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Assert
        inside.ShouldBe(ThreadPriority.BelowNormal);
        after.ShouldBe(ThreadPriority.Normal);
    }

    [Theory]
    [InlineData(FileAttributes.Normal, false)]
    [InlineData(FileAttributes.Offline, true)]
    [InlineData((FileAttributes)0x00400000, true)]
    [InlineData((FileAttributes)0x00040000, true)]
    public void IsCloudPlaceholder_Attributes_DetectsRecallBits(FileAttributes attributes, bool expected)
    {
        // Act
        var item = new FileSystemItem("a", false, 0, default, default, attributes);

        // Assert
        item.IsCloudPlaceholder.ShouldBe(expected);
    }
}
