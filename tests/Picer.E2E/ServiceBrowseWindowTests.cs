using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Picer.App.Views;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.E2E;

public sealed class ServiceBrowseWindowTests
{
    private sealed class StubGateway : IIndexGateway
    {
        public List<ImageRecord> Records { get; } = Enumerable.Range(0, 5).Select(i => new ImageRecord
        {
            Id = i + 1,
            SourceId = 1,
            Path = $"C:\\svc\\img{i}.png",
            FileSize = 100 + i,
            ModifiedUtc = DateTime.UtcNow,
            IndexedUtc = DateTime.UtcNow,
            Status = ImageStatus.Active
        }).ToList();

        public Task<GatewayHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayHealth("ok", 30, "1.0", "stub"));

        public Task<GatewayPage> ListImagesAsync(long? sourceId = null, bool includeMissing = false, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default) =>
            SearchAsync(new SearchFilters { SourceId = sourceId, IncludeMissing = includeMissing }, pageSize, cursor, cancellationToken);

        public Task<ImageRecord?> GetImageAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Records.FirstOrDefault(record => record.Id == id));

        public Task<Stream?> OpenThumbnailAsync(long imageId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);

        public Task<GatewayVideoPage> ListVideosAsync(long? sourceId = null, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayVideoPage(Array.Empty<VideoRecord>(), null, 0));

        public Task<VideoRecord?> GetVideoAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult<VideoRecord?>(null);

        public Task<Stream?> OpenVideoKeyframeAsync(long keyframeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);

        public async Task<GatewayPage> SearchAsync(SearchFilters filters, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default)
        {
            var items = string.IsNullOrEmpty(filters.Query)
                ? Records
                : Records.Where(record => record.Path.Contains(filters.Query, StringComparison.OrdinalIgnoreCase)).ToList();
            await Task.Yield();
            return new GatewayPage(items, null, items.Count);
        }

        public Task<IReadOnlyList<SourceFolder>> ListSourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SourceFolder>>(Array.Empty<SourceFolder>());

        public Task<IReadOnlyList<Tag>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Tag>>(Array.Empty<Tag>());

        public Task<IReadOnlyList<Album>> ListAlbumsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Album>>(Array.Empty<Album>());

        public Task<GatewayMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayMetadata(Array.Empty<GatewayAlbumImageLink>(), Array.Empty<GatewayImageTagLink>(), Array.Empty<ImageLocationHierarchyRow>()));

        public Task<IReadOnlyList<PersonSummary>> ListPeopleAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PersonSummary>>(Array.Empty<PersonSummary>());

        public Task<SourceFolder> RegisterSourceAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveSourceAsync(long sourceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<string> StartScanAsync(long sourceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ScanJobInfo?> GetScanJobAsync(string jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ScanJobInfo?>(null);

        public Task<bool> CancelScanAsync(string jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> RemoveRecycledRecordsAsync(IReadOnlyList<long> imageIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<string> BackupAsync(string destinationPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(destinationPath);

        public void Dispose() { }
    }

    [Fact]
    public void ServiceBrowseWindow_RendersAndSearchesThroughGateway()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new ServiceBrowseWindow(new StubGateway(), autoLoad: true);
                window.Show();
                WaitUntil(window, () => window.ViewModel.Items.Count == 5 && !window.ViewModel.IsBusy, TimeSpan.FromSeconds(15));
                Assert.Equal(5, window.ViewModel.Items.Count);
                Assert.Equal(5, window.ViewModel.TotalCount);

                window.Dispatcher.Invoke(() => window.ViewModel.SearchText = "img3");
                window.Dispatcher.Invoke(() => window.ViewModel.SearchCommand.Execute(null));
                WaitUntil(window, () => window.ViewModel.Items.Count == 1, TimeSpan.FromSeconds(15));
                var match = Assert.Single(window.ViewModel.Items);
                Assert.EndsWith("img3.png", match.Path);

                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "服务浏览窗口测试超时");
        if (failure is not null) throw failure;
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Accessibility_KeyboardFocusAndMinimumLayout(double scale)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            ServiceBrowseWindow? window = null;
            try
            {
                window = new ServiceBrowseWindow(new StubGateway(), autoLoad: false)
                { Width = 720 * scale, Height = 480 * scale, ShowInTaskbar = false };
                ((FrameworkElement)window.Content).LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
                window.Show();
                window.Activate();
                window.UpdateLayout();
                var search = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
                search.Text = "keyboard query";
                Assert.True(window.HandleShortcut(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.Control));
                Assert.True(search.IsKeyboardFocusWithin);
                Assert.Equal(search.Text, search.SelectedText);
                Assert.Equal("搜索图片", System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(search)!.GetName());
                Assert.NotNull(window.FindResource(SystemParameters.FocusVisualStyleKey));
                Assert.True(window.HandleShortcut(System.Windows.Input.Key.L, System.Windows.Input.ModifierKeys.Control));
                Assert.True(((System.Windows.Controls.TreeView)window.FindName("SourcesTree")).IsKeyboardFocusWithin);
                Assert.False(window.HandleShortcut(System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.None));
                Assert.False(window.HandleShortcut(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt));
                window.HandleShortcut(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.Control);
                Assert.True(window.HandleShortcut(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None));
                Assert.Empty(search.Text);
                WaitUntil(window, () => !window.ViewModel.IsBusy, TimeSpan.FromSeconds(10));
                window.UpdateLayout();
                var bounds = search.TransformToAncestor(window).TransformBounds(new Rect(search.RenderSize));
                Assert.True(bounds.Right <= window.ActualWidth, "最小窗口下搜索框必须完整可见");
                Assert.True(((System.Windows.Controls.ListBox)window.FindName("Photos")).ActualWidth >= 192);
            }
            catch (Exception exception) { failure = exception; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "无障碍焦点测试超时");
        if (failure is not null) throw failure;
    }

    [Fact]
    public void RemoteMainWindow_LocalSearchEventsDoNotDereferenceMissingViewModel()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Picer.App.MainWindow();
                ((System.Windows.Controls.TextBox)window.FindName("SearchBox")).Text = "query";
                ((System.Windows.Controls.ComboBox)window.FindName("MediaTypeBox")).SelectedIndex = 1;
                Assert.False(window.HandleLocalShortcut(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None));
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) throw failure;
    }

    private static void WaitUntil(Window window, Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(50);
        }
        if (!condition())
        {
            var statusText = ((ServiceBrowseWindow)window).ViewModel.StatusText;
            throw new TimeoutException($"条件未在时限内满足，status={statusText}");
        }
    }
}
