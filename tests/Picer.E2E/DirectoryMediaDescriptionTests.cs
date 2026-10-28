using System.Windows;
using System.IO;
using System.Windows.Controls;
using Picer.App.Infrastructure;
using Picer.App;
using Picer.App.Views;
using Picer.Core.Models;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.E2E;

public sealed class DirectoryMediaDescriptionTests
{
    [Fact]
    public void RemoteSceneAnchorKeepsActiveImagesWithoutCheckingClientPaths()
    {
        var activeRemote = new ImageRecord { Id = 1, Path = "remote://101APPLE/photo.jpg", Status = ImageStatus.Active };
        var inactiveRemote = new ImageRecord { Id = 2, Path = "remote://101APPLE/missing.jpg", Status = ImageStatus.Missing };
        var localChecks = 0;

        var selected = SceneDescriptionImageSelector.SelectReadable(
            [activeRemote, inactiveRemote, null], serviceMode: true,
            _ => { localChecks++; return false; });

        Assert.Collection(selected, image => Assert.Same(activeRemote, image));
        Assert.Equal(0, localChecks);
    }

    [Fact]
    public void LocalSceneAnchorRequiresActiveImageFileToExist()
    {
        var available = new ImageRecord { Id = 1, Path = "available.jpg", Status = ImageStatus.Active };
        var missing = new ImageRecord { Id = 2, Path = "missing.jpg", Status = ImageStatus.Active };
        var inactive = new ImageRecord { Id = 3, Path = "inactive.jpg", Status = ImageStatus.Missing };

        var selected = SceneDescriptionImageSelector.SelectReadable(
            [available, missing, inactive], serviceMode: false,
            path => path == "available.jpg");

        Assert.Collection(selected, image => Assert.Same(available, image));
    }

    [Fact]
    public async Task RemoteAnalyzerReusesOnlyPrivateAnalysisInputs()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "picer-analyzer-input-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(dataRoot, "description-input");
        Directory.CreateDirectory(cache);
        var local = Path.Combine(cache, "prepared.jpg");
        var outside = Path.Combine(dataRoot, "original.jpg");
        try
        {
            await File.WriteAllBytesAsync(local, [1, 2, 3]);
            await File.WriteAllBytesAsync(outside, [4, 5, 6]);
            await using var accepted = AppServices.OpenPrivateAnalysisInput(dataRoot, local);
            Assert.NotNull(accepted);
            using var output = new MemoryStream();
            await accepted!.CopyToAsync(output);
            Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
            await accepted.DisposeAsync();
            Assert.Null(AppServices.OpenPrivateAnalysisInput(dataRoot, outside));
            Assert.Null(AppServices.OpenPrivateAnalysisInput(dataRoot, Path.Combine(cache, "missing.jpg")));

            var montageDirectory = Path.Combine(dataRoot, "spaces", "account", "remote-video-summary-cache");
            Directory.CreateDirectory(montageDirectory);
            var montage = Path.Combine(montageDirectory, "video-1.jpg");
            await File.WriteAllBytesAsync(montage, [7, 8, 9]);
            await using var acceptedMontage = AppServices.OpenPrivateAnalysisInput(
                Path.Combine(dataRoot, "spaces", "account"), montage);
            Assert.NotNull(acceptedMontage);
        }
        finally { Directory.Delete(dataRoot, true); }
    }

    [Fact]
    public async Task FirstDescriptionStartsBeforeDiscoveryFinishes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async IAsyncEnumerable<DirectoryDescriptionItem> Discover()
        {
            yield return new(new ImageRecord { Id = 1, Path = "one.jpg" }, null, false);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            yield return new(new ImageRecord { Id = 2, Path = "already.jpg" }, null, true);
            yield return new(null, new VideoRecord(3, 1, "video.mp4", 1, DateTime.UtcNow, 1, 2, 2, null, null, "active"), false);
        }
        var runner = new MediaDescriptionBatch((_, _) => { Interlocked.Increment(ref calls); started.TrySetResult(); return Task.FromResult(true); },
            (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(true); });
        var messages = new List<string>();
        await runner.RunStreamingAsync(Discover(), new Capture(messages), CancellationToken.None, 2, 2);
        Assert.Equal(2, calls);
        Assert.Contains("原有描述跳过 1", messages[^1]);
        Assert.Contains("2/2", messages[^1]);
    }

    [Fact]
    public async Task DiscoveryRunsAheadOfSlowModelWorkers()
    {
        const int itemCount = 200;
        var discoveryCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseModels = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modelCalls = 0;
        async IAsyncEnumerable<DirectoryDescriptionItem> Discover()
        {
            for (var id = 1; id <= itemCount; id++)
                yield return new(new ImageRecord { Id = id, Path = $"{id}.jpg" }, null, false);
            discoveryCompleted.TrySetResult();
            await Task.CompletedTask;
        }
        var runner = new MediaDescriptionBatch(async (_, token) =>
        {
            Interlocked.Increment(ref modelCalls);
            await releaseModels.Task.WaitAsync(token);
            return true;
        }, (_, _) => Task.FromResult(true));
        var run = runner.RunStreamingAsync(Discover(), new Capture([]), CancellationToken.None, photoConcurrency: 2);
        await discoveryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(modelCalls, 0, 2);
        releaseModels.TrySetResult();
        await run;
        Assert.Equal(itemCount, modelCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemotePhotoUsesPreparedInputOrVerifiedLegacyOriginal(bool supported)
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-prepared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] original = [1, 2, 3, 4]; byte[] prepared = [8, 9];
            var record = new ImageRecord { Id = 1, Path = "remote/photo.jpg", FileSize = original.Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)) };
            var assets = new PreparedAssets(supported, original, prepared);
            var inner = new SnapshotAnalyzer(record.Path, supported ? prepared : original);
            await new MediaDescriptionBatch.PhotoInputAnalyzer(inner, root, assets).AnalyzeAsync(new(record, null, []), default);
            Assert.Equal(1, inner.Calls);
            Assert.Equal(supported ? 0 : 1, assets.OriginalReads);
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RemotePhotoFallsBackToVerifiedOriginalWhenPreparedInputIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-prepared-rejected-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] original = [1, 2, 3, 4];
            var record = new ImageRecord { Id = 1, Path = "remote/photo.jpg", FileSize = original.Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)) };
            var assets = new PreparedAssets(false, original, [], rejectPrepared: true);
            var inner = new SnapshotAnalyzer(record.Path, original);
            await new MediaDescriptionBatch.PhotoInputAnalyzer(inner, root, assets).AnalyzeAsync(new(record, null, []), default);
            Assert.Equal(1, inner.Calls);
            Assert.Equal(1, assets.OriginalReads);
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class PreparedAssets(bool supported, byte[] original, byte[] prepared, bool rejectPrepared = false) : Picer.Core.Gateway.IMediaAssetGateway
    {
        public int OriginalReads;
        public Task<Stream?> OpenPhotoAnalysisInputAsync(ImageRecord image, CancellationToken cancellationToken = default)
            => rejectPrepared ? throw new InvalidDataException("photo_analysis_source_conflict")
                : Task.FromResult<Stream?>(supported ? new MemoryStream(prepared) : null);
        public Task<Stream?> OpenOriginalImageAsync(long id, string? agentId = null, CancellationToken cancellationToken = default)
        { OriginalReads++; return Task.FromResult<Stream?>(new MemoryStream(original)); }
        public Task<Stream?> OpenOriginalVideoAsync(long id, string? agentId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream?> OpenVideoThumbnailAsync(long id, string? agentId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream?> OpenFaceThumbnailAsync(long id, string? agentId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalPhotoInputUsesVerifiedSnapshotAndRejectsChangedContent(bool changed)
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-description-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.jpg");
            byte[] original = [1, 2, 3, 4];
            await File.WriteAllBytesAsync(source, original);
            var modified = File.GetLastWriteTimeUtc(source);
            var image = new ImageRecord { Id = 1, Path = source, FileSize = original.Length, ModifiedUtc = modified,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)) };
            if (changed)
            {
                await File.WriteAllBytesAsync(source, new byte[] { 4, 3, 2, 1 });
                File.SetLastWriteTimeUtc(source, modified);
            }
            var inner = new SnapshotAnalyzer(source, original);
            var cache = Path.Combine(root, "input");
            var analyzer = new MediaDescriptionBatch.PhotoInputAnalyzer(inner, cache);
            if (changed)
            {
                var failure = await Assert.ThrowsAsync<AnalysisStageException>(() => analyzer.AnalyzeAsync(new(image, null, []), CancellationToken.None));
                Assert.Equal(AnalysisStage.Preprocess, failure.Stage);
                Assert.Equal(0, inner.Calls);
            }
            else
            {
                await analyzer.AnalyzeAsync(new(image, null, []), CancellationToken.None);
                Assert.Equal(1, inner.Calls);
            }
            Assert.Empty(Directory.EnumerateFiles(cache));
            Assert.Equal(changed ? new byte[] { 4, 3, 2, 1 } : original, await File.ReadAllBytesAsync(source));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class SnapshotAnalyzer(string source, byte[] original) : ICloudVisionAnalyzer
    {
        public int Calls { get; private set; }
        public string Provider => "test";
        public string ModelVersion => "test";
        public string PromptVersion => "test";
        public async Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.NotEqual(source, request.Image.Path);
            Assert.Equal(original, await File.ReadAllBytesAsync(request.Image.Path, cancellationToken));
            return new("test", [], [], [], []);
        }
    }

    [Fact]
    public async Task FailureListIncludesBothMediaKindsButExcludesUserCancellation()
    {
        var failures = new List<MediaDescriptionFailure>();
        var plan = new DirectoryMediaDescriptionPlan("D:/media", 1, 1,
            [new ImageRecord { Id = 1, Path = "D:/media/photo.jpg" }],
            [new VideoRecord(2, 1, "D:/media/video.mp4", 1, DateTime.UtcNow, 1, 1, 1, null, null, "active")]);
        var runner = new MediaDescriptionBatch((_, _) => Task.FromResult(false), (_, _) => throw new AnalysisStageException(AnalysisStage.Preprocess, "HTTP_403", false));
        await Assert.ThrowsAsync<DescriptionServiceUnavailableException>(() => runner.RunAsync(plan, new Capture([]), CancellationToken.None, failures: new FailureCapture(failures)));
        Assert.Equal(2, failures.Count); Assert.False(failures[0].IsVideo); Assert.True(failures[1].IsVideo);
        Assert.Equal(AnalysisStage.Preprocess, failures[1].Stage); Assert.False(failures[1].Retryable); Assert.Equal("HTTP_403", failures[1].Error);
        failures.Clear(); using var cancelled = new CancellationTokenSource();
        runner = new MediaDescriptionBatch((_, _) => { cancelled.Cancel(); return Task.FromCanceled<bool>(cancelled.Token); }, (_, _) => Task.FromResult(true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(plan, new Capture([]), cancelled.Token, failures: new FailureCapture(failures)));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task UnavailableServiceStopsBatchWithoutFailingEveryRemainingPhoto()
    {
        var attempted = 0;
        var plan = new DirectoryMediaDescriptionPlan("D:/media", 100, 0,
            Enumerable.Range(1, 100).Select(id => new ImageRecord { Id = id, Path = $"D:/media/{id}.jpg" }).ToArray(), []);
        var runner = new MediaDescriptionBatch((_, _) =>
        {
            Interlocked.Increment(ref attempted);
            throw new AnalysisStageException(AnalysisStage.Preprocess, "HTTP_503", true);
        }, (_, _) => Task.FromResult(true));
        await Assert.ThrowsAsync<DescriptionServiceUnavailableException>(() => runner.RunAsync(plan, new Capture([]), CancellationToken.None, photoConcurrency: 2));
        Assert.InRange(attempted, 6, 7);
    }

    [Fact]
    public async Task VideoConcurrencyIsBoundedAndCancellationDrainsWorkers()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var calls = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var coordinator = new MediaDescriptionBatch((_, _) => throw new InvalidOperationException("No photos expected"), async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            if (Interlocked.Increment(ref active) == 2) started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return true; }
            finally { Interlocked.Decrement(ref active); }
        });
        var plan = new DirectoryMediaDescriptionPlan("G:/videos", 0, 8, [],
            Enumerable.Range(1, 8).Select(id => new VideoRecord(id, 1, $"G:/videos/{id}.mp4", 1,
                DateTime.UtcNow, 1, 1, 1, null, null, "active")).ToArray());
        var run = coordinator.RunAsync(plan, new Capture([]), cancellation.Token, videoConcurrency: 2);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, active);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PhotoConcurrencyIsBoundedAndCancellationDrainsWorkers()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var calls = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var coordinator = new MediaDescriptionBatch(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            if (Interlocked.Increment(ref active) == 2) started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return true; }
            finally { Interlocked.Decrement(ref active); }
        }, (_, _) => throw new InvalidOperationException("Video must not start"));
        var plan = new DirectoryMediaDescriptionPlan("G:/photos", 8, 0,
            Enumerable.Range(1, 8).Select(id => new ImageRecord { Id = id }).ToArray(), []);
        var run = coordinator.RunAsync(plan, new Capture([]), cancellation.Token, 2);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, active);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task BatchProcessesPhotosAndVideosAndContinuesPastOneFailure()
    {
        var photos = 0; var videos = 0;
        var coordinator = new MediaDescriptionBatch((_, _) => { photos++; return Task.FromResult(false); },
            (_, _) => { videos++; return Task.FromResult(true); });
        var plan = new DirectoryMediaDescriptionPlan("D:/media", 1, 1, [new ImageRecord()],
            [new VideoRecord(1, 1, "D:/media/sub/video.mp4", 1, DateTime.UtcNow, 1, 1, 1, null, null, "active")]);
        var messages = new List<string>();
        await coordinator.RunAsync(plan, new Capture(messages), CancellationToken.None);
        Assert.Equal(1, photos); Assert.Equal(1, videos);
        Assert.Contains("2/2", messages[^1]); Assert.Contains("失败 1", messages[^1]);
    }

    [Fact]
    public async Task CancellationStopsBeforeNextVideo()
    {
        using var cancellation = new CancellationTokenSource();
        var videoCalls = 0;
        var coordinator = new MediaDescriptionBatch((_, _) => { cancellation.Cancel(); return Task.FromResult(true); },
            (_, _) => { videoCalls++; return Task.FromResult(true); });
        var plan = new DirectoryMediaDescriptionPlan("D:/media", 1, 1, [new ImageRecord()],
            [new VideoRecord(1, 1, "D:/media/video.mp4", 1, DateTime.UtcNow, 1, 1, 1, null, null, "active")]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(plan, new Capture([]), cancellation.Token));
        Assert.Equal(0, videoCalls);
    }

    [Fact]
    public void DirectoryTreeIncludesVideoOnlySubdirectories()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new DirectoryMediaDescriptionWindow(loadOnShow: false);
                window.PopulateDirectories(["D:/media/photo.jpg", "D:/media/videos/only.mp4", "D:/other/file.jpg",
                    "D:/opt/picer-index/D:/media/.directory-node", "/opt/picer-index/D:/media/.directory-node"]);
                var drive = Assert.IsType<TreeViewItem>(Assert.Single(window.DirectoryTree.Items.Cast<object>()));
                drive.RaiseEvent(new RoutedEventArgs(TreeViewItem.ExpandedEvent, drive));
                var media = drive.Items.Cast<TreeViewItem>().Single(item => (string)item.Tag == "D:/media");
                media.RaiseEvent(new RoutedEventArgs(TreeViewItem.ExpandedEvent, media));
                Assert.Contains(media.Items.Cast<TreeViewItem>(), item => (string)item.Tag == "D:/media/videos");
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) throw failure;
    }

    [Fact]
    public void StartButtonExplainsDirectorySelectionAndEnablesAfterSelection()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new DirectoryMediaDescriptionWindow(loadOnShow: false);
                window.PopulateDirectories(["D:/media/photos/one.jpg"]);
                Assert.True(window.StartButton.IsEnabled);
                Assert.Contains("单击目录树中的目录名称", window.StatusMessage);
                var root = Assert.IsType<TreeViewItem>(Assert.Single(window.DirectoryTree.Items.Cast<object>()));
                root.IsSelected = true;
                Assert.Contains("已选择目录", window.StatusMessage);
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) throw failure;
    }

    [Fact]
    public void EmptyDirectoryTreeExplainsWhyDescriptionCannotStart()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new DirectoryMediaDescriptionWindow(loadOnShow: false);
                window.PopulateDirectories([]);
                Assert.False(window.StartButton.IsEnabled);
                Assert.Contains("当前索引没有可选择", window.StatusMessage);
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) throw failure;
    }

    private sealed class Capture(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
    private sealed class FailureCapture(List<MediaDescriptionFailure> failures) : IProgress<MediaDescriptionFailure>
    {
        public void Report(MediaDescriptionFailure value) => failures.Add(value);
    }
}
