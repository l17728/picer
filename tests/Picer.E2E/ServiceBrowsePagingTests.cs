using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Threading;
using Picer.App;
using Picer.App.ViewModels;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.E2E;

public sealed class ServiceBrowsePagingTests
{
    [Fact]
    public void PhotosAppearBeforeSlowVideosAndDirectoryTreeAndBothCursorsContinue() => RunSta(async () =>
    {
        using var handler = new PagingHandler { HoldVideos = true, HoldDirectories = true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") };
        using var gateway = new HttpIndexGateway(client);
        var root = Path.Combine(Path.GetTempPath(), "picer-browse-" + Guid.NewGuid().ToString("N"));
        var model = new ServiceBrowseViewModel(gateway, new ClientServices { RemoteThumbnails = new RemoteThumbnailCache(root) }) { MediaFilterIndex = 2 };
        try
        {
            var loading = model.SearchAsync();
            await Until(() => model.Items.Count == 2);
            Assert.All(model.Items, item => Assert.False(item.IsVideo));
            Assert.False(loading.IsCompleted);
            Assert.Empty(model.DirectoryTree);
            handler.VideoRelease.TrySetResult();
            await loading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, model.Items.Count);
            Assert.False(handler.DirectoryRelease.Task.IsCompleted);
            await model.LoadMoreCommand.ExecuteAsync(null);
            Assert.Equal(6, model.Items.Count);
            Assert.Equal(6, model.Items.Select(item => item.Id).Distinct().Count());
            Assert.Equal(3, model.Items.Count(item => item.IsVideo));
            Assert.Equal(6, model.TotalCount);
        }
        finally
        {
            model.CancelThumbnailWork(); handler.VideoRelease.TrySetResult(); handler.DirectoryRelease.TrySetResult();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public void ReplacedSearchCannotAppendLateResults() => RunSta(async () =>
    {
        using var handler = new PagingHandler { HoldOldSearch = true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") };
        using var gateway = new HttpIndexGateway(client);
        var model = new ServiceBrowseViewModel(gateway) { MediaFilterIndex = 0, SearchText = "old" };
        try
        {
            var old = model.SearchAsync(); await handler.OldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.SearchText = "new"; await model.SearchAsync();
            Assert.Equal(new long[] { 101, 102 }, model.Items.Select(item => item.Id));
            handler.OldRelease.TrySetResult(); await old;
            Assert.Equal(new long[] { 101, 102 }, model.Items.Select(item => item.Id));
        }
        finally { model.CancelThumbnailWork(); handler.OldRelease.TrySetResult(); }
    });

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void UnsupportedVideoSearchFallsBackToPagedVideoList(HttpStatusCode legacyStatus) => RunSta(async () =>
    {
        using var handler = new PagingHandler { LegacyVideos = true, LegacyStatus = legacyStatus };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") };
        using var gateway = new HttpIndexGateway(client);
        var model = new ServiceBrowseViewModel(gateway) { MediaFilterIndex = 1 };
        try
        {
            await model.SearchAsync(); Assert.Equal(2, model.Items.Count); Assert.Equal(-1, model.TotalCount);
            await model.LoadMoreCommand.ExecuteAsync(null);
            Assert.Equal(3, model.Items.Count); Assert.Equal(3, model.TotalCount);
            Assert.Equal(1, handler.UnsupportedCalls);
        }
        finally { model.CancelThumbnailWork(); }
    });

    private sealed class PagingHandler : HttpMessageHandler
    {
        public bool HoldVideos, HoldDirectories, HoldOldSearch, LegacyVideos;
        public HttpStatusCode LegacyStatus = HttpStatusCode.NotFound;
        public int UnsupportedCalls;
        public TaskCompletionSource VideoRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DirectoryRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OldRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/sources")) return Json(new[] { new SourceFolder { Id = 1, Path = "/photos" } });
            if (route.EndsWith("/directories"))
            {
                if (HoldDirectories) await DirectoryRelease.Task.WaitAsync(token);
                return Json(new[] { new GatewayDirectorySummary(1, "/photos/trip", 3, 3) });
            }
            if (route.EndsWith("/search"))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var query = body.RootElement.GetProperty("filters").GetProperty("query").GetString();
                if (query == "old" && HoldOldSearch) { OldStarted.TrySetResult(); await OldRelease.Task; }
                var last = body.RootElement.GetProperty("cursor").ValueKind != JsonValueKind.Null;
                var ids = last ? new[] { 3 } : new[] { 1, 2 };
                return Json(new GatewayPage(ids.Select(id => new ImageRecord { Id = id + (query == "new" ? 100 : 0),
                    SourceId = 1, Path = $"/photos/trip/{id}.jpg" }).ToArray(), last ? null : "2", 3));
            }
            if (route.EndsWith("/video-search") || route.EndsWith("/videos"))
            {
                if (LegacyVideos && route.EndsWith("/video-search")) { UnsupportedCalls++; return new(LegacyStatus); }
                if (HoldVideos) await VideoRelease.Task.WaitAsync(token);
                var last = request.RequestUri.Query.Contains("cursor=2");
                if (request.Content is not null)
                {
                    using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
                    last = body.RootElement.GetProperty("cursor").ValueKind != JsonValueKind.Null;
                }
                var ids = last ? new[] { 3 } : new[] { 1, 2 };
                return Json(new GatewayVideoPage(ids.Select(id => new VideoRecord(id, 1, $"/photos/trip/{id}.mp4", 1,
                    DateTime.UtcNow, 1, 1, 1, null, null, "active")).ToArray(), last ? null : "2", 3));
            }
            return new(HttpStatusCode.NotFound);
        }
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private static void RunSta(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); } catch (Exception exception) { failure = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "浏览分页用例超时");
        Assert.Null(failure);
    }
}
