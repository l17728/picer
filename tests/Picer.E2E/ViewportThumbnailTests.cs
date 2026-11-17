using System.IO;
using System.Windows.Threading;
using Picer.App;
using Picer.App.ViewModels;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.E2E;

public sealed class ViewportThumbnailTests
{
    [Fact]
    public void SharedPresenterReusesDirectoryCacheAcrossDifferentReferences() => Run(async root =>
    {
        var calls = 0;
        using var gateway = new ServiceBrowseWindowTests.StubGateway
        {
            ThumbnailSource = (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult<Stream?>(Image()); }
        };
        var services = new ClientServices { ServiceMode = true, Gateway = gateway, RemoteThumbnails = new(root) };
        var hash = new string('a', 64);
        var directory = new ImageItemViewModel(new ImageRecord { Id = 1, Sha256 = hash }, services: services);
        directory.EnsureThumbnailLoad();
        await Until(() => directory.HasThumbnail);
        var classification = new ImageItemViewModel(new ImageRecord { Id = 2, Sha256 = hash }, services: services);
        var image = new System.Windows.Controls.Image();
        Picer.App.Controls.ThumbnailPresenter.SetItem(image, classification);
        var window = new System.Windows.Window { Content = image, Width = 300, Height = 300 };
        try
        {
            window.Show();
            await Until(() => image.Source is not null);
            Assert.Equal(1, calls);
            window.Hide();
            window.Show();
            await Until(() => image.Source is not null);
            Assert.Equal(1, calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SharedPresenterRecyclingCancelsObsoleteDownload() => Run(async root =>
    {
        var started = false;
        var cancelled = false;
        using var gateway = new ServiceBrowseWindowTests.StubGateway
        {
            ThumbnailSource = async (id, token) =>
            {
                if (id == 1)
                {
                    started = true;
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { cancelled = true; }
                }
                return Image();
            }
        };
        var services = new ClientServices { ServiceMode = true, Gateway = gateway, RemoteThumbnails = new(root) };
        var image = new System.Windows.Controls.Image();
        Picer.App.Controls.ThumbnailPresenter.SetItem(image, new(new ImageRecord { Id = 1 }, services: services));
        var window = new System.Windows.Window { Content = image, Width = 300, Height = 300 };
        try
        {
            window.Show();
            await Until(() => started);
            var next = new ImageItemViewModel(new ImageRecord { Id = 2 }, services: services);
            Picer.App.Controls.ThumbnailPresenter.SetItem(image, next);
            await Until(() => cancelled && image.Source is not null);
            Assert.Same(next.Thumbnail, image.Source);
        }
        finally { window.Close(); }
    });

    private static Stream Image() => new MemoryStream(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII="));

    [Fact]
    public void RealPhotoGridScrollReleasesPreviousViewport() => Run(async root =>
    {
        var mode = typeof(AppServices).GetProperty(nameof(AppServices.ServiceMode))!;
        var previousMode = AppServices.ServiceMode;
        var started = 0;
        var cancelled = 0;
        using var gateway = new ServiceBrowseWindowTests.StubGateway
        {
            ThumbnailSource = async (id, token) =>
            {
                if (id < 20)
                {
                    Interlocked.Increment(ref started);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { Interlocked.Increment(ref cancelled); }
                }
                return Image();
            }
        };
        using var stop = new CancellationTokenSource();
        var services = new ClientServices { ServiceMode = true, Gateway = gateway, RemoteThumbnails = new(root) };
        var items = Enumerable.Range(1, 100).Select(id => new ImageItemViewModel(new ImageRecord { Id = id }, stop.Token, services)).ToArray();
        var grid = new Picer.App.Controls.PhotoGrid { ItemsSource = items };
        var window = new System.Windows.Window { Content = grid, Width = 700, Height = 500 };
        try
        {
            mode.SetValue(null, true);
            window.Show();
            await Until(() => started > 0);
            var panel = FindPanel(grid)!;
            Assert.NotNull(panel);
            panel.SetVerticalOffset(panel.ExtentHeight - panel.ViewportHeight);
            window.UpdateLayout();
            await Until(() => cancelled > 0 && items[^1].HasThumbnail);
            Assert.NotNull(services.RemoteThumbnails.TryGet(items[^1].Record));
        }
        finally { stop.Cancel(); window.Close(); mode.SetValue(null, previousMode); }
    });

    private static Picer.App.Controls.VirtualizingWrapPanel? FindPanel(System.Windows.DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Picer.App.Controls.VirtualizingWrapPanel panel) return panel;
            if (FindPanel(child) is { } nested) return nested;
        }
        return null;
    }

    [Fact]
    public void NewViewportReleasesSlowDownloadsAndReusesPersistentCache() => Run(async root =>
    {
        var started = 0;
        var cancelled = 0;
        var calls = 0;
        var attempts = new int[4];
        using var gateway = new ServiceBrowseWindowTests.StubGateway
        {
            ThumbnailSource = async (id, token) =>
            {
                Interlocked.Increment(ref calls);
                if (id <= 2 && Interlocked.Increment(ref attempts[id]) == 1)
                {
                    Interlocked.Increment(ref started);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { Interlocked.Increment(ref cancelled); }
                }
                return Image();
            }
        };
        var services = new ClientServices { ServiceMode = true, Gateway = gateway, RemoteThumbnails = new(root) };
        var vm = new ServiceBrowseViewModel(gateway, services);
        try
        {
            for (var id = 1; id <= 3; id++) vm.Items.Add(new() { Id = id, MediaId = id });
            vm.ScheduleThumbnailPrefetch([0, 1]);
            await Until(() => started == 2);
            vm.ScheduleThumbnailPrefetch([2]);
            await Until(() => vm.Items[2].HasThumbnail && cancelled == 2);
            Assert.NotNull(services.RemoteThumbnails.TryGet(new() { Id = 3 }));
            await Until(() => vm.Items.All(item => item.HasThumbnail));
            var before = calls;
            services.RemoteThumbnails = new(root);
            var reopened = new ImageItemViewModel(new ImageRecord { Id = 3 }, services: services);
            reopened.EnsureThumbnailLoad();
            await Until(() => reopened.HasThumbnail);
            Assert.Equal(before, calls);
        }
        finally { vm.CancelThumbnailWork(); }
    });

    [Fact]
    public void DiskHitsBypassBusyNetworkAndCorruptCacheRecoversWithoutScrolling() => Run(async root =>
    {
        var started = 0;
        using var gateway = new ServiceBrowseWindowTests.StubGateway
        {
            ThumbnailSource = async (id, token) =>
            {
                if (id <= 2) { Interlocked.Increment(ref started); await Task.Delay(Timeout.Infinite, token); }
                return Image();
            }
        };
        var cache = new RemoteThumbnailCache(root);
        await cache.GetOrDownloadAsync(new() { Id = 3 }, () => Task.FromResult<Stream?>(Image()));
        await cache.GetOrDownloadAsync(new() { Id = 4 }, () => Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));
        var vm = new ServiceBrowseViewModel(gateway, new() { ServiceMode = true, Gateway = gateway, RemoteThumbnails = cache });
        try
        {
            for (var id = 1; id <= 4; id++) vm.Items.Add(new() { Id = id, MediaId = id });
            vm.ScheduleThumbnailPrefetch([0, 1]);
            await Until(() => started == 2);
            vm.ScheduleThumbnailPrefetch([0, 1, 2]);
            await Until(() => vm.Items[2].HasThumbnail);
            vm.ScheduleThumbnailPrefetch([3]);
            await Until(() => vm.Items[3].HasThumbnail, 10);
            Assert.NotNull(cache.TryGet(new() { Id = 4 }));
        }
        finally { vm.CancelThumbnailWork(); }
    });

    [Fact]
    public void PausedPhotoDoesNotRestartFromBindingAndVideoPersistsAcrossViewModels() => Run(async root =>
    {
        var started = 0;
        using var gateway = new VideoGateway
        {
            ThumbnailSource = async (_, token) =>
            { Interlocked.Increment(ref started); await Task.Delay(Timeout.Infinite, token); return null; }
        };
        var services = new ClientServices { ServiceMode = true, Gateway = gateway, RemoteThumbnails = new(root) };
        var photo = new ImageItemViewModel(new ImageRecord { Id = 1 }, services: services);
        photo.EnsureThumbnailLoad();
        await Until(() => started == 1);
        photo.PauseThumbnailLoad();
        await Until(() => !photo.IsThumbnailLoading);
        Assert.Null(photo.Thumbnail);
        Assert.Equal(1, started);
        var video = new VideoRecord(1, 1, "missing.mp4", 1, DateTime.UtcNow, null, 0, 0, null, null, "active");
        var first = new ImageItemViewModel(video, null, services: services);
        first.EnsureThumbnailLoad();
        await Until(() => first.HasThumbnail);
        services.RemoteThumbnails = new(root);
        var second = new ImageItemViewModel(video, null, services: services);
        second.EnsureThumbnailLoad();
        await Until(() => second.HasThumbnail);
        Assert.Equal(1, gateway.VideoCalls);
        var vm = new ServiceBrowseViewModel(gateway, services);
        try
        {
            vm.Items.Add(new() { Id = -1, MediaId = 1, IsVideo = true });
            vm.ScheduleThumbnailPrefetch([0]);
            await Until(() => vm.Items[0].HasThumbnail);
            Assert.Equal(1, gateway.VideoCalls);
        }
        finally { vm.CancelThumbnailWork(); }
    });

    private sealed class VideoGateway : ServiceBrowseWindowTests.StubGateway, IMediaAssetGateway
    {
        public int VideoCalls;
        public Task<Stream?> OpenVideoThumbnailAsync(long videoId, string? agentId = null, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref VideoCalls); return Task.FromResult<Stream?>(Image()); }
        public Task<Stream?> OpenFaceThumbnailAsync(long faceId, string? agentId = null, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
        public Task<Stream?> OpenOriginalImageAsync(long imageId, string? agentId = null, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
        public Task<Stream?> OpenOriginalVideoAsync(long videoId, string? agentId = null, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
    }

    private static async Task Until(Func<bool> condition, int seconds = 3)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(condition(), "缩略图未在期限内完成");
    }

    private static void Run(Func<string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-viewport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(root); }
                catch (Exception exception) { failure = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "视口缩略图测试超时");
        try { if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
