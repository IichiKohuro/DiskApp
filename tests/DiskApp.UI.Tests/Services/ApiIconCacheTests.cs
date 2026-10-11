using System.Net;

using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using DiskApp.Client;
using DiskApp.UI.Controls;
using DiskApp.UI.Models;
using DiskApp.UI.Services;

using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace DiskApp.UI.Tests.Services;

/// <summary>
/// Иконки и миниатюры в интерфейсе (PLAN.md, этап 7.4): ключи кэша, одна загрузка на ключ, кэширование
/// «изображения нет», повтор после сбоя связи; элемент <see cref="FileIcon"/> с заглушкой.
/// </summary>
[Collection(FileIconStatics.Name)]
public sealed class ApiIconCacheTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly FileEntry Pdf = new()
    {
        Id = 5,
        FullPath = @"D:\Docs\a.pdf",
        Kind = FileKind.Pdf,
        Root = "Документы",
        IconKey = "ext:pdf",
        Modified = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Key_IconsShareServerKeyThumbnailsAreTiedToEntryVersion()
    {
        // Act & Assert
        ApiIconCache.Key(Pdf, 32, thumbnail: false).ShouldBe("ext:pdf|32");
        ApiIconCache.Key(Pdf with { Id = 6 }, 32, thumbnail: false).ShouldBe("ext:pdf|32", "иконка типа общая для всех PDF");
        ApiIconCache.Key(Pdf, 64, thumbnail: true).ShouldBe($"thumb:5:{Pdf.Modified.UtcTicks}|64");
        ApiIconCache.Key(Pdf with { Id = 0 }, 32, thumbnail: false).ShouldBeNull("у записи макета нет изображения на сервере");
        ApiIconCache.Key(Pdf with { IconKey = string.Empty }, 32, thumbnail: false).ShouldBeNull();
    }

    [Fact]
    public Task GetAsync_SameIconTwice_DownloadsOnce() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        var release = new TaskCompletionSource<byte[]>();
        fake.Handlers[nameof(IDiskAppApi.GetIconAsync)] = _ => release.Task;
        var cache = new ApiIconCache(api);

        // Act
        var first = cache.GetAsync(Pdf, 32, thumbnail: false);
        var second = cache.GetAsync(Pdf with { Id = 9 }, 32, thumbnail: false);
        release.SetResult(Png);
        var images = await Task.WhenAll(first, second);
        var third = await cache.GetAsync(Pdf, 32, thumbnail: false);

        // Assert
        fake.Calls.Count(static call => call == nameof(IDiskAppApi.GetIconAsync)).ShouldBe(1);
        images[0].ShouldNotBeNull();
        images[1].ShouldBeSameAs(images[0]);
        third.ShouldBeSameAs(images[0]);
        cache.TryGet(Pdf, 32, thumbnail: false, out var ready).ShouldBeTrue();
        ready.ShouldBeSameAs(images[0]);
    });

    [Fact]
    public Task GetAsync_NotFound_CachesAbsence() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Handlers[nameof(IDiskAppApi.GetThumbnailAsync)] = _ => Task.FromException<byte[]>(new DiskAppApiException(HttpStatusCode.NotFound, "Нет", null, null, null));
        var cache = new ApiIconCache(api);

        // Act
        var first = await cache.GetAsync(Pdf, 64, thumbnail: true);
        var second = await cache.GetAsync(Pdf, 64, thumbnail: true);

        // Assert
        first.ShouldBeNull();
        second.ShouldBeNull();
        fake.Calls.Count(static call => call == nameof(IDiskAppApi.GetThumbnailAsync)).ShouldBe(1);
        cache.TryGet(Pdf, 64, thumbnail: true, out var known).ShouldBeTrue("отсутствие тоже ответ");
        known.ShouldBeNull();
    });

    [Fact]
    public Task GetAsync_ConnectionError_TriesAgainLater() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        var fail = true;
        fake.Handlers[nameof(IDiskAppApi.GetIconAsync)] = _ => fail
            ? Task.FromException<byte[]>(new HttpRequestException("сервер ещё запускается"))
            : Task.FromResult(Png);
        var cache = new ApiIconCache(api);

        // Act
        var during = await cache.GetAsync(Pdf, 32, thumbnail: false);
        fail = false;
        var after = await cache.GetAsync(Pdf, 32, thumbnail: false);

        // Assert
        during.ShouldBeNull();
        after.ShouldNotBeNull();
        fake.Calls.Count(static call => call == nameof(IDiskAppApi.GetIconAsync)).ShouldBe(2);
    });

    [Theory]
    [InlineData(24, 1.0, 24)]
    [InlineData(24, 1.5, 40)]
    [InlineData(24, 2.0, 48)]
    [InlineData(16, 1.25, 20)]
    [InlineData(300, 1.0, 256)]
    public void PixelSizeFor_PicksServerSizeNotSmallerThanNeeded(double size, double scaling, int expected)
    {
        // Act & Assert
        FileIcon.PixelSizeFor(size, scaling).ShouldBe(expected);
    }

    [Fact]
    public void WantsThumbnail_OnlyLocalImagesAndVideos()
    {
        // Act & Assert
        FileIcon.WantsThumbnail(Pdf with { Kind = FileKind.Image }, preferThumbnail: true).ShouldBeTrue();
        FileIcon.WantsThumbnail(Pdf with { Kind = FileKind.Video }, preferThumbnail: true).ShouldBeTrue();
        FileIcon.WantsThumbnail(Pdf, preferThumbnail: true).ShouldBeFalse();
        FileIcon.WantsThumbnail(Pdf with { Kind = FileKind.Image }, preferThumbnail: false).ShouldBeFalse();
        FileIcon.WantsThumbnail(Pdf with { Kind = FileKind.Image, IsCloudOnly = true }, preferThumbnail: true).ShouldBeFalse("миниатюра скачала бы файл из облака");
        FileIcon.WantsThumbnail(Pdf with { Kind = FileKind.Image, IsOffline = true }, preferThumbnail: true).ShouldBeFalse();
    }

    [Fact]
    public Task FileIcon_WithCache_ReplacesFallbackWithImage() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Handlers[nameof(IDiskAppApi.GetIconAsync)] = _ => Task.FromResult(Png);
        var previous = FileIcon.Cache;
        FileIcon.Cache = new ApiIconCache(api);
        try
        {
            var icon = new FileIcon { Symbol = Symbol.DocumentPdf, FallbackBackground = Brushes.Gray, Entry = Pdf };
            var window = new Window { Width = 100, Height = 100, Content = icon };

            // Act
            window.Show();
            var fallbackFirst = icon.Background;
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();

            // Assert
            fallbackFirst.ShouldBe(Brushes.Gray, "пока иконка грузится — заглушка на подложке");
            var image = icon.GetVisualDescendants().OfType<Image>().Single();
            image.IsVisible.ShouldBeTrue();
            image.Source.ShouldNotBeNull();
            icon.Background.ShouldBeNull("у настоящей иконки подложки нет");
            icon.GetVisualDescendants().OfType<SymbolIcon>().Single().IsVisible.ShouldBeFalse();
            window.Close();
        }
        finally
        {
            FileIcon.Cache = previous;
        }
    });

    [Fact]
    public Task FileIcon_Thumbnail_LoadsOnlyNearViewport() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange: фотография далеко ниже видимой области прокрутки (плитка «Обзора» не виртуализуется, этап 10.2).
        var cache = Substitute.For<IIconCache>();
        var previous = FileIcon.Cache;
        FileIcon.Cache = cache;
        try
        {
            var icon = new FileIcon { Entry = Pdf with { Kind = FileKind.Image, FullPath = @"D:\Фото\море.jpg" }, PreferThumbnail = true, Width = 32, Height = 32 };
            var scroller = new ScrollViewer { Content = new StackPanel { Children = { new Border { Height = 3000 }, icon } } };
            var window = new Window { Width = 200, Height = 200, Content = scroller };
            int ThumbnailRequests() => cache.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == nameof(IIconCache.GetAsync) && (bool)call.GetArguments()[2]!);

            // Act
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            var before = ThumbnailRequests();
            scroller.Offset = new Avalonia.Vector(0, 3000);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();

            // Assert
            before.ShouldBe(0, "миниатюра не запрашивается, пока иконка далеко за видимой областью");
            ThumbnailRequests().ShouldBe(1);
            window.Close();
        }
        finally
        {
            FileIcon.Cache = previous;
        }
    });

    [Fact]
    public Task FileIcon_WithoutCache_ShowsFallback() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange
        var previous = FileIcon.Cache;
        FileIcon.Cache = null;
        try
        {
            var icon = new FileIcon { Symbol = Symbol.DocumentPdf, Entry = Pdf };
            var window = new Window { Width = 100, Height = 100, Content = icon };

            // Act
            window.Show();

            // Assert
            var symbol = icon.GetVisualDescendants().OfType<SymbolIcon>().Single();
            symbol.IsVisible.ShouldBeTrue();
            symbol.Symbol.ShouldBe(Symbol.DocumentPdf);
            window.Close();
        }
        finally
        {
            FileIcon.Cache = previous;
        }
    });
}
