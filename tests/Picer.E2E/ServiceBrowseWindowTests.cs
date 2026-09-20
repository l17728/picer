using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Picer.App.Views;
using Picer.Core.Gateway;
using Picer.Core.Models;
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
                WaitUntil(window, () => window.ViewModel.Items.Count == 5, TimeSpan.FromSeconds(15));
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
