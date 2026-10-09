using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core.Video;
using Picer.Core.Gateway;
using Picer.Core.Logging;
using Picer.Core.People;
using Picer.App.Infrastructure;

namespace Picer.App.ViewModels;

public partial class VideoViewModel(VideoRepository repository, VideoIndexService index, IVideoMediaProcessor? keyframes,
    IVideoTranscriptionService? transcription = null, VideoFaceTimelineService? faceTimeline = null,
    VideoSummaryService? summaryService = null, PeopleService? peopleService = null, bool remoteMode = false,
    Func<IReadOnlyList<VideoKeyframe>, long, CancellationToken, Task<IReadOnlyList<VideoKeyframe>>>? remoteFrameLoader = null, ClientServices? services = null) : ObservableObject
{
    private readonly ClientServices _services = services ?? new();
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _selectedVideoLoadCancellation;
    public ObservableCollection<VideoItemViewModel> Videos { get; } = new();
    public ObservableCollection<VideoKeyframeItemViewModel> Frames { get; } = new();
    public ObservableCollection<VideoFaceItemViewModel> VideoFaces { get; } = new();
    public ObservableCollection<VideoFaceItemViewModel> FilteredVideoFaces { get; } = new();
    public ObservableCollection<VideoFaceGroupViewModel> VideoFaceGroups { get; } = new();
    public ObservableCollection<PersonSummary> VideoPeople { get; } = new();
    private Func<Task>? _identityChanged;
    private Task? _videoFaceThumbnailWorker;
    private long _videoFaceThumbnailRequestTicks;
    private VideoItemViewModel[] _selectedVideos = [];
    private VideoFaceItemViewModel[] _selectedVideoFaces = [];
    [ObservableProperty] private VideoItemViewModel? _selectedVideo;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateKeyframesCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzePeopleCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateSummaryCommand))]
    [NotifyCanExecuteChangedFor(nameof(PurgeIndexCommand))]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value) =>
        AppLogger.Info($"视频页 IsBusy 变化；value={value}; selectedVideo={SelectedVideo?.Record.Id.ToString() ?? "none"}");
    [ObservableProperty] private string _statusText = keyframes is not null
        ? "就绪"
        : remoteMode ? "远程关键帧由索引服务提供" : "FFmpeg尚未就绪";
    [ObservableProperty] private string _transcriptText = "尚未转写";
    [ObservableProperty] private string _peopleTimelineText = "尚未分析视频人物";
    [ObservableProperty] private VideoFaceItemViewModel? _selectedVideoFace;
    [ObservableProperty] private VideoFaceGroupViewModel? _selectedVideoFaceGroup;
    [ObservableProperty] private PersonSummary? _selectedVideoPerson;

    [ObservableProperty] private string _summaryText = "尚未生成融合摘要";

    public void SetIdentityChangedCallback(Func<Task> callback) => _identityChanged = callback;

    public async Task LoadAsync(string? query = null)
    {
        _loadCancellation?.Cancel();
        var cancellation = _loadCancellation = new CancellationTokenSource();
        var started = Stopwatch.StartNew();
        try
        {
        var rows = await UiWork.RunDatabaseAsync("Videos.Catalog.Read", () =>
        {
            var faceReviews = repository.GetFaceReviewSummaries();
            var counts = remoteMode ? repository.GetKeyframeCounts() : null;
            return (string.IsNullOrWhiteSpace(query) ? repository.GetAll() : repository.Search(query))
                .Select(video => new VideoItemViewModel(video, counts?.GetValueOrDefault(video.Id) ?? repository.GetKeyframes(video.Id, existingOnly: !remoteMode).Count,
                    faceReviews.GetValueOrDefault(video.Id) ?? new VideoFaceReviewSummary(0, 0, 0, 0, null))).ToArray();
        }, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        var selectedVideoId = SelectedVideo?.Record.Id;
        Videos.Clear();
        await UiWork.AddInBatchesAsync(rows, Videos.Add, cancellation.Token);
        if (selectedVideoId is { } currentId && (SelectedVideo is null || !Videos.Contains(SelectedVideo)))
            SelectedVideo = Videos.FirstOrDefault(video => video.Record.Id == currentId);
        StatusText = string.IsNullOrWhiteSpace(query) ? $"共 {Videos.Count} 个视频" : $"视频搜索命中 {Videos.Count} 个";
        AppLogger.Info($"Video catalog loaded: count={Videos.Count}; remote={remoteMode}; elapsedMs={started.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested) StatusText = "视频列表加载失败，请重试；已有内容已保留";
            AppLogger.Error($"Video catalog load failed: remote={remoteMode}; elapsedMs={started.ElapsedMilliseconds}", exception);
        }
    }

    public Task ApplySearchAsync(string? query) => LoadAsync(query);

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task PurgeIndexAsync()
    {
        var selected = _selectedVideos.Length > 0 ? _selectedVideos : SelectedVideo is null ? [] : new[] { SelectedVideo };
        if (selected.Length == 0) { StatusText = "请先选择视频"; return; }
        var confirmed = _services.ConfirmVideoPurge?.Invoke(selected.Length) ?? (MessageBox.Show(
            $"彻底清除所选 {selected.Length} 个视频的索引、分析记录及应用缓存？原视频文件保留。同一内容仍有其他引用时，共享分析随其他引用保留。",
            "彻底清除视频索引", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        if (!confirmed) return;
        IsBusy = true;
        var completed = 0;
        try
        {
            _selectedVideoLoadCancellation?.Cancel();
            if (_videoFaceThumbnailWorker is not null)
                try { await _videoFaceThumbnailWorker; } catch (OperationCanceledException) { }
            foreach (var video in selected)
            {
                if (_services.PurgeVideo is not null) await _services.PurgeVideo(video.Record, CancellationToken.None);
                else throw new InvalidOperationException("视频清除服务尚未就绪");
                completed++;
                Videos.Remove(video);
            }
            _selectedVideos = [];
            SelectedVideo = null;
            await LoadAsync();
            StatusText = $"已彻底清除 {completed} 个视频索引，原文件已保留";
        }
        catch (Exception exception)
        {
            var remaining = selected.Skip(completed).Select(video => video.Record.Id).ToHashSet();
            await LoadAsync();
            _selectedVideos = Videos.Where(video => remaining.Contains(video.Record.Id)).ToArray();
            SelectedVideo = _selectedVideos.FirstOrDefault();
            StatusText = $"已清除 {completed} 项；剩余项未完成，请检查源主机连接后重试（{exception.GetType().Name}）";
        }
        finally { IsBusy = false; }
    }

    public async Task FocusVideoAsync(long videoId)
    {
        await LoadAsync();
        SelectedVideo = Videos.FirstOrDefault(value => value.Record.Id == videoId);
        if (SelectedVideo is not null) StatusText = $"已定位视频：{SelectedVideo.FileName}";
    }

    public void UpdateSelection(IEnumerable<VideoItemViewModel> videos) => _selectedVideos = videos.Distinct().ToArray();
    public void UpdateVideoFaceSelection(IEnumerable<VideoFaceItemViewModel> faces) => _selectedVideoFaces = faces.Distinct().ToArray();

    partial void OnSelectedVideoChanged(VideoItemViewModel? value)
    {
        _selectedVideoLoadCancellation?.Cancel();
        _selectedVideoLoadCancellation?.Dispose();
        _selectedVideoLoadCancellation = null;
        Frames.Clear();
        VideoFaces.Clear();
        FilteredVideoFaces.Clear();
        VideoFaceGroups.Clear();
        if (value is null)
        {
            TranscriptText = "尚未转写"; PeopleTimelineText = "尚未分析视频人物"; SummaryText = "尚未生成融合摘要";
            VideoFaces.Clear(); FilteredVideoFaces.Clear(); VideoFaceGroups.Clear();
            return;
        }
        TranscriptText = "正在加载转写…";
        PeopleTimelineText = "正在加载人物标注…";
        SummaryText = "正在加载摘要…";
        var cancellation = _selectedVideoLoadCancellation = new CancellationTokenSource();
        _ = LoadSelectedVideoAsync(value, cancellation.Token);
    }

    private async Task LoadSelectedVideoAsync(VideoItemViewModel selected, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var metadataLoaded = false;
        try
        {
        var snapshot = await UiWork.RunDatabaseAsync(() => (
            Transcript: repository.GetTranscript(selected.Record.Id),
            Timeline: faceTimeline?.GetTimeline(selected.Record.Id),
            Summary: repository.GetSummary(selected.Record.Id),
            Frames: repository.GetKeyframes(selected.Record.Id, existingOnly: !remoteMode).ToArray(),
            People: peopleService?.GetAll().ToArray() ?? []), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedVideo?.Record.Id != selected.Record.Id) return;
        AppLogger.Info($"视频详情加载：video={selected.Record.Id}; frames={snapshot.Frames.Length}; videoFaces={snapshot.Timeline?.Count ?? 0}; remote={remoteMode}");
        TranscriptText = ExtractTranscriptText(snapshot.Transcript);
        SummaryText = snapshot.Summary ?? "尚未生成融合摘要";
        PeopleTimelineText = FormatTimeline(snapshot.Timeline);
        PopulateVideoFaces(snapshot.Timeline ?? [], snapshot.People);
        metadataLoaded = true;
        AppLogger.Info($"Video people annotation projection: video={selected.Record.Id}; preferredModel={faceTimeline?.PreferredModelVersion ?? "none"}; resolvedModels={string.Join(',', snapshot.Timeline?.Select(face => face.ModelVersion).Distinct(StringComparer.OrdinalIgnoreCase) ?? [])}; indexedFaces={selected.FaceReview.Total}; displayedFaces={snapshot.Timeline?.Count ?? 0}; analysisStatus={selected.FaceReview.AnalysisStatus ?? "none"}");
        var frames = remoteMode ? await (remoteFrameLoader ?? ((frames, id, ct) => MaterializeRemoteFramesAsync(_services, frames, id, ct)))(snapshot.Frames, selected.Record.Id, cancellationToken) : snapshot.Frames;
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedVideo?.Record.Id != selected.Record.Id) return;
        AppLogger.Info($"Video keyframes materialized: video={selected.Record.Id}; requested={snapshot.Frames.Length}; available={frames.Count}; elapsedMs={started.ElapsedMilliseconds}");
        if (frames.Count < snapshot.Frames.Length)
            StatusText = $"关键帧已加载 {frames.Count}/{snapshot.Frames.Length}，部分暂不可用，请重试";
        // Service mode cannot open the source-machine path in the mirrored
        // timeline. Rebind video-face cards to the downloaded keyframe cache.
        var timeline = snapshot.Timeline;
        if (remoteMode && timeline is not null)
        {
            var cachedByTimestamp = frames.GroupBy(frame => frame.TimestampSeconds)
                .ToDictionary(group => group.Key, group => group.First().Path);
            timeline = timeline.Select(face => cachedByTimestamp.TryGetValue(face.TimestampSeconds, out var path)
                ? face with { KeyframePath = path }
                : face).ToArray();
        }
        PeopleTimelineText = FormatTimeline(timeline);
        PopulateVideoFaces(timeline ?? [], snapshot.People);
        AppLogger.Info($"视频人物标注已绑定：video={selected.Record.Id}; displayedFaces={timeline?.Count ?? 0}; cachedFrames={frames.Count}");
        await UiWork.AddInBatchesAsync(frames, frame => Frames.Add(new VideoKeyframeItemViewModel(frame)), cancellationToken, batchSize: 30);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested || SelectedVideo?.Record.Id != selected.Record.Id) return;
            if (!metadataLoaded)
            {
                TranscriptText = "转写信息加载失败，请重试";
                PeopleTimelineText = "人物标注加载失败，请重试";
                SummaryText = "摘要加载失败，请重试";
            }
            StatusText = metadataLoaded ? "关键帧加载失败，已保留转写、摘要和人物标注，请重试" : "视频详情加载失败，请重试";
            AppLogger.Error($"Video details load failed: video={selected.Record.Id}; remote={remoteMode}; elapsedMs={started.ElapsedMilliseconds}", exception);
        }
    }

    partial void OnSelectedVideoFaceGroupChanged(VideoFaceGroupViewModel? value) => ApplyVideoFaceFilter();

    private void PopulateVideoFaces(IReadOnlyList<VideoFaceTimelineRecord> faces, IReadOnlyList<PersonSummary>? people = null)
    {
        VideoFaces.Clear();
        foreach (var face in faces)
        {
            var item = new VideoFaceItemViewModel(face);
            VideoFaces.Add(item);
        }
        ScheduleVideoFaceThumbnails();
        VideoPeople.Clear();
        foreach (var person in people ?? []) VideoPeople.Add(person);
        var selectedKey = SelectedVideoFaceGroup?.Key ?? "all";
        VideoFaceGroups.Clear();
        VideoFaceGroups.Add(new("all", $"全部人脸（{faces.Count}）"));
        VideoFaceGroups.Add(new("pending", $"待确认（{faces.Count(face => face.AssignmentState is null)}）"));
        foreach (var group in faces.Where(face => face.AssignmentState == "confirmed" && face.AssignedPersonId is not null)
                     .GroupBy(face => new { Id = face.AssignedPersonId!.Value, Name = face.AssignedPersonName }).OrderBy(group => group.Key.Name))
            VideoFaceGroups.Add(new($"person:{group.Key.Id}", $"{group.Key.Name}（{group.Count()}）", group.Key.Id));
        VideoFaceGroups.Add(new("ignored", $"不分类（{faces.Count(face => face.AssignmentState == "ignored")}）"));
        SelectedVideoFaceGroup = VideoFaceGroups.FirstOrDefault(group => group.Key == selectedKey) ?? VideoFaceGroups[0];
        ApplyVideoFaceFilter();
    }

    private void ApplyVideoFaceFilter()
    {
        var group = SelectedVideoFaceGroup;
        var values = group?.Key switch
        {
            "pending" => VideoFaces.Where(face => face.Record.AssignmentState is null),
            "ignored" => VideoFaces.Where(face => face.Record.AssignmentState == "ignored"),
            _ when group?.PersonId is long personId => VideoFaces.Where(face => face.Record.AssignedPersonId == personId),
            _ => VideoFaces
        };
        FilteredVideoFaces.Clear();
        foreach (var face in values) FilteredVideoFaces.Add(face);
    }

    private void ScheduleVideoFaceThumbnails()
    {
        Volatile.Write(ref _videoFaceThumbnailRequestTicks, DateTime.UtcNow.Ticks);
        if (_videoFaceThumbnailWorker is null || _videoFaceThumbnailWorker.IsCompleted)
            _videoFaceThumbnailWorker = LoadVideoFaceThumbnailsWhenIdleAsync();
    }

    private async Task LoadVideoFaceThumbnailsWhenIdleAsync()
    {
        await Task.Delay(500);
        while (true)
        {
            var remaining = Volatile.Read(ref _videoFaceThumbnailRequestTicks) + TimeSpan.TicksPerMillisecond * 500 - DateTime.UtcNow.Ticks;
            if (remaining > 0) { await Task.Delay(TimeSpan.FromTicks(remaining)); continue; }
            var pending = VideoFaces.Where(face => !face.HasThumbnail && !face.IsThumbnailLoading && !face.ThumbnailLoadAttempted).Take(8).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending.Select(face => face.LoadThumbnailAsync()));
        }
    }

    [RelayCommand]
    private async Task ConfirmVideoFaceAsync()
    {
        var faces = GetSelectedVideoFaces();
        if (faces.Length == 0 || faceTimeline is null) { StatusText = "请先选择视频人脸"; return; }
        var confirmable = faces.Where(face => face.Record.SuggestedPersonId is not null).ToArray();
        if (confirmable.Length == 0) { StatusText = "所选人脸没有推荐人物，请先选择正确人物"; return; }
        await SaveVideoFaceIdentitiesAsync(() =>
        {
            foreach (var face in confirmable)
                faceTimeline.Confirm(face.Record.FaceId, face.Record.SuggestedPersonId!.Value);
        }, $"已确认 {confirmable.Length} 张视频人脸");
    }

    [RelayCommand]
    private async Task AssignVideoFaceAsync()
    {
        var faces = GetSelectedVideoFaces();
        if (faces.Length == 0 || SelectedVideoPerson is null || faceTimeline is null) { StatusText = "请选择视频人脸和正确人物"; return; }
        var person = SelectedVideoPerson;
        await SaveVideoFaceIdentitiesAsync(() =>
        {
            foreach (var face in faces)
            {
                if (face.Record.SuggestedPersonId is long suggested && suggested != person.Id)
                    faceTimeline.RejectSuggestion(face.Record.FaceId, suggested);
                faceTimeline.Confirm(face.Record.FaceId, person.Id);
            }
        }, $"已将 {faces.Length} 张视频人脸归入“{person.DisplayName}”");
    }

    [RelayCommand]
    private async Task RejectVideoFaceSuggestionAsync()
    {
        var faces = GetSelectedVideoFaces().Where(face => face.Record.SuggestedPersonId is not null).ToArray();
        if (faces.Length == 0 || faceTimeline is null) { StatusText = "所选人脸没有推荐人物"; return; }
        await SaveVideoFaceIdentitiesAsync(() =>
        {
            foreach (var face in faces) faceTimeline.RejectSuggestion(face.Record.FaceId, face.Record.SuggestedPersonId!.Value);
        }, $"已否定 {faces.Length} 张人脸的推荐，请选择正确人物");
    }

    [RelayCommand]
    private async Task IgnoreVideoFaceAsync()
    {
        var faces = GetSelectedVideoFaces();
        if (faces.Length == 0 || faceTimeline is null) { StatusText = "请先选择视频人脸"; return; }
        await SaveVideoFaceIdentitiesAsync(() =>
        {
            foreach (var face in faces) faceTimeline.MarkIgnored(face.Record.FaceId);
        }, $"已将 {faces.Length} 张视频人脸标记为不分类");
    }

    private VideoFaceItemViewModel[] GetSelectedVideoFaces() =>
        _selectedVideoFaces.Length > 0 ? _selectedVideoFaces : SelectedVideoFace is null ? [] : [SelectedVideoFace];

    private async Task SaveVideoFaceIdentitiesAsync(Action save, string message)
    {
        var selectedFaceId = SelectedVideoFace?.Record.FaceId;
        var selectedVideoId = SelectedVideo?.Record.Id;
        await UiWork.RunDatabaseAsync(save);
        _services.QueueImmediateRemoteSync("视频人物标注写入");
        if (_identityChanged is not null) await _identityChanged();
        _selectedVideoFaces = [];
        await LoadAsync();
        SelectedVideo = selectedVideoId is null ? null : Videos.FirstOrDefault(video => video.Record.Id == selectedVideoId);
        SelectedVideoFace = FilteredVideoFaces.FirstOrDefault(face => face.Record.FaceId == selectedFaceId);
        StatusText = message;
    }

    public async Task AssignVideoFaceToPersonAsync(VideoFaceItemViewModel face, PersonSummary person)
    {
        SelectedVideoFace = face;
        SelectedVideoPerson = person;
        await AssignVideoFaceAsync();
    }

    public async Task AssignSelectedVideoFacesToPersonAsync(PersonSummary person)
    {
        SelectedVideoPerson = person;
        await AssignVideoFaceAsync();
    }

    public async Task CreatePersonAndAssignVideoFaceAsync(VideoFaceItemViewModel face, string name)
    {
        if (peopleService is null || faceTimeline is null) return;
        try
        {
            var person = await UiWork.RunDatabaseAsync(() => peopleService.Create(name));
            SelectedVideoFace = face;
            await SaveVideoFaceIdentitiesAsync(() => faceTimeline.Confirm(face.Record.FaceId, person.Id), $"已创建并归入“{person.DisplayName}”");
        }
        catch (Exception exception)
        {
            StatusText = $"创建人物失败：{exception.Message}";
        }
    }


    public async Task CreatePersonAndAssignSelectedVideoFacesAsync(string name)
    {
        var faces = GetSelectedVideoFaces();
        if (faces.Length == 0 || peopleService is null || faceTimeline is null) { StatusText = "请先选择视频人脸"; return; }
        try
        {
            var person = await UiWork.RunDatabaseAsync(() => peopleService.Create(name));
            await SaveVideoFaceIdentitiesAsync(() =>
            {
                foreach (var face in faces) faceTimeline.Confirm(face.Record.FaceId, person.Id);
            }, $"已创建“{person.DisplayName}”并归入 {faces.Length} 张视频人脸");
        }
        catch (Exception exception)
        {
            StatusText = $"创建人物失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        var started = Stopwatch.StartNew();
        AppLogger.Info($"视频扫描命令进入；remote={remoteMode}; isBusy={IsBusy}; videoCount={Videos.Count}");
        if (IsBusy)
        {
            StatusText = "当前已有视频任务正在执行，扫描请求已排队提示；请等待当前任务完成";
            AppLogger.Warn("视频扫描未启动：VideoViewModel.IsBusy=true");
            return;
        }
        IsBusy = true;
        StatusText = remoteMode ? "正在从 IndexService 刷新远程视频索引…" : "正在异步扫描本地视频文件…";
        try
        {
            if (remoteMode)
            {
                AppLogger.Info("视频扫描进入远程镜像刷新；sourceCount=" + _services.Db.GetSources().Count);
                using var refreshTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await _services.SyncRemoteVideosAsync(refreshTimeout.Token);
                }
                catch (OperationCanceledException) when (refreshTimeout.IsCancellationRequested)
                {
                    AppLogger.Warn("视频扫描远程镜像刷新超时；保留已有本地视频镜像，不继续阻塞界面");
                    StatusText = "远程视频索引刷新超时，已保留已有结果；请检查 IndexService 后重试";
                    await LoadAsync();
                    return;
                }
                await LoadAsync();
                StatusText = $"远程视频索引刷新完成：当前 {Videos.Count} 个视频";
                AppLogger.Info($"视频扫描远程镜像刷新完成；videoCount={Videos.Count}; elapsedMs={started.ElapsedMilliseconds}");
                return;
            }
            AppLogger.Info("视频扫描进入本地异步枚举；sourceCount=" + _services.Db.GetSources().Count);
            var count = await index.ScanLocalSourcesAsync();
            await LoadAsync();
            AppLogger.Info($"视频扫描本地完成；found={count}; videoCount={Videos.Count}; elapsedMs={started.ElapsedMilliseconds}");
            StatusText = $"扫描完成，共发现 {count} 个视频";
        }
        catch (Exception ex) { StatusText = $"扫描失败：{ex.Message}"; }
        finally { IsBusy = false; AppLogger.Info($"视频扫描命令退出；isBusy={IsBusy}; elapsedMs={started.ElapsedMilliseconds}"); }
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task GenerateKeyframesAsync()
    {
        var targets = (_selectedVideos.Length > 0 ? _selectedVideos : SelectedVideo is null ? [] : [SelectedVideo]).ToArray();
        if (targets.Length == 0) { StatusText = "请先选择视频"; return; }
        IVideoMediaProcessor? mediaProcessor = keyframes;
        IDisposable? ownedProcessor = null;
        if (mediaProcessor is null && remoteMode)
        {
            var backend = AppPaths.LoadIndexBackend();
            if (backend.BaseUrl is { Length: > 0 } baseUrl &&
                Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var endpoint))
            {
                mediaProcessor = new ServiceVideoMediaProcessor(endpoint, backend.Token ?? "",
                    Path.Combine(AppPaths.DataRoot, "remote-video-keyframes"));
                ownedProcessor = (IDisposable)mediaProcessor;
                AppLogger.Info($"远程关键帧命令动态绑定 IndexService：endpoint={endpoint}");
            }
        }
        if (mediaProcessor is null)
        {
            StatusText = remoteMode ? "远程模式不能在本机生成关键帧，请在本地端生成后同步" : "FFmpeg尚未安装或路径无效";
            return;
        }
        IsBusy = true;
        var concurrency = Math.Min(3, Math.Max(2, Environment.ProcessorCount / 2));
        var generated = 0;
        var reused = 0;
        var failed = 0;
        var generatedFrames = 0;
        var completed = 0;
        var active = 0;
        var selectedId = SelectedVideo?.Record.Id;
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        IProgress<string> progress = new Progress<string>(message => StatusText = message);
        try
        {
            async Task ProcessAsync(VideoItemViewModel target)
            {
                await gate.WaitAsync();
                var video = target.Record;
                var running = Interlocked.Increment(ref active);
                progress.Report($"并行关键帧：完成 {Volatile.Read(ref completed)}/{targets.Length}，正在处理 {running} 个（最多 {concurrency} 个）");
                try
                {
                    if (video.DurationSeconds is null || video.Width <= 0 || video.Height <= 0)
                    {
                        try
                        {
                            var probe = await mediaProcessor.ProbeAsync(video);
                            await UiWork.RunDatabaseAsync(() => repository.SaveProbe(video.Id, probe, video));
                            _services.QueueImmediateRemoteSync("视频媒体信息写入");
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn($"视频元数据探测失败，将继续处理关键帧：{video.Path}；{CompactError(ex.Message)}");
                        }
                    }
                    var cached = await UiWork.RunDatabaseAsync(() => repository.GetKeyframes(video.Id, existingOnly: !remoteMode));
                    if (cached.Count > 0)
                    {
                        Interlocked.Increment(ref reused);
                        return;
                    }
                    var frames = await mediaProcessor.ExtractAsync(video);
                    if (frames.Count == 0) throw new InvalidOperationException("FFmpeg 未能从该视频提取任何画面");
                    await UiWork.RunDatabaseAsync(() => repository.SaveKeyframes(video.Id, frames, video));
                    _services.QueueImmediateRemoteSync("视频关键帧写入");
                    Interlocked.Increment(ref generated);
                    Interlocked.Add(ref generatedFrames, frames.Count);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    AppLogger.Error($"关键帧生成失败：{video.Path}", ex);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                    var done = Interlocked.Increment(ref completed);
                    progress.Report($"并行关键帧：完成 {done}/{targets.Length}；生成 {Volatile.Read(ref generated)}，复用 {Volatile.Read(ref reused)}，失败 {Volatile.Read(ref failed)}");
                    gate.Release();
                }
            }

            StatusText = $"正在启动并行关键帧任务（最多同时 {concurrency} 个）…";
            await Task.WhenAll(targets.Select(ProcessAsync));
            await LoadAsync();
            SelectedVideo = selectedId is null ? null : Videos.FirstOrDefault(item => item.Record.Id == selectedId);
            StatusText = $"并行关键帧完成：选择 {targets.Length} 个，生成 {generated} 个（{generatedFrames} 帧），复用 {reused} 个，失败 {failed} 个；并发数 {concurrency}";
        }
        finally
        {
            ownedProcessor?.Dispose();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenSelected()
    {
        if (SelectedVideo is null) { StatusText = "请先选择视频"; return; }
        if (remoteMode)
        {
            if (_services.Gateway is not IMediaAssetGateway gateway)
            {
                StatusText = "IndexService 原图代理不可用，无法打开远程视频";
                AppLogger.Warn($"Remote video original unavailable: gateway does not support media assets; video={SelectedVideo.Record.Id}");
                return;
            }
            IsBusy = true;
            try
            {
                var directory = Path.Combine(AppPaths.DataRoot, "remote-assets", "videos");
                Directory.CreateDirectory(directory);
                var extension = Path.GetExtension(SelectedVideo.Record.Path);
                if (string.IsNullOrWhiteSpace(extension) || extension.Length > 12 || extension.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '.'))
                    extension = ".mp4";
                var video = SelectedVideo.Record;
                var target = Path.Combine(directory, $"video-{video.Id}-{video.FileSize}-{video.ModifiedUtc.Ticks}{extension}");
                var operationId = Guid.NewGuid().ToString("N");
                if (!File.Exists(target))
                {
                    StatusText = $"正在通过原图代理下载视频：{SelectedVideo.FileName}";
                    AppLogger.Info($"Remote original video request started; operation={operationId}; video={video.Id}; cacheHit=false");
                    await using var stream = await gateway.OpenOriginalVideoAsync(video.Id);
                    if (stream is null) throw new IOException("原图代理未返回视频");
                    var temporary = target + $".{operationId}.partial";
                    try
                    {
                        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
                            await stream.CopyToAsync(output);
                        if (video.FileSize > 0 && new FileInfo(temporary).Length != video.FileSize)
                            throw new InvalidDataException("代理返回的视频长度与索引记录不一致");
                        File.Move(temporary, target, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                else AppLogger.Info($"Remote original video cache hit; operation={operationId}; video={video.Id}");
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                StatusText = $"已通过 IndexService 原图代理打开远程视频（本地缓存可复用）：{SelectedVideo.FileName}";
                AppLogger.Info($"Remote original video opened; operation={operationId}; video={video.Id}");
            }
            catch (Exception exception)
            {
                StatusText = $"远程视频打开失败：{CompactError(exception.Message)}";
                AppLogger.Error($"Remote original video open failed; video={SelectedVideo.Record.Id}", exception);
            }
            finally { IsBusy = false; }
            return;
        }
        if (!File.Exists(SelectedVideo.Record.Path)) { StatusText = "原视频当前不可用，请重新扫描来源"; return; }
        try { Process.Start(new ProcessStartInfo(SelectedVideo.Record.Path) { UseShellExecute = true }); }
        catch (Exception exception) { StatusText = $"无法打开视频：{exception.Message}"; }
    }

    [RelayCommand]
    private void RevealSelected()
    {
        if (SelectedVideo is null) { StatusText = "请先选择视频"; return; }
        if (remoteMode)
        {
            StatusText = "远程模式不能定位源目录；原始文件由源机器代理提供";
            AppLogger.Info($"远程模式已阻止资源管理器定位视频源文件：video={SelectedVideo.Record.Id}");
            return;
        }
        var path = SelectedVideo.Record.Path;
        if (!File.Exists(path)) { StatusText = "原视频已移动或删除，请重新扫描或重新定位"; return; }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            StatusText = $"已在资源管理器中定位 {Path.GetFileName(path)}";
        }
        catch (Exception exception) { StatusText = $"无法在资源管理器中定位视频：{exception.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task TranscribeAsync()
    {
        if (SelectedVideo is null) { StatusText="请先选择视频"; return; }
        var video = SelectedVideo.Record;
        var activeTranscription = _services.CreateVideoTranscriptionService() ?? transcription;
        if (activeTranscription is null) { StatusText="请先在 AI 模型设置中为视频语音转文字选择可用的本地语音模型"; return; }
        var cachedRecord=await UiWork.RunDatabaseAsync(()=>repository.GetTranscriptWithModel(video.Id));
        var cached=cachedRecord?.Json;
        if(cachedRecord is not null && string.Equals(cachedRecord.Value.ModelVersion, activeTranscription.ModelVersion, StringComparison.OrdinalIgnoreCase) && IsUsableTranscript(cached)){ TranscriptText=ExtractTranscriptText(cached); StatusText=$"已复用 {activeTranscription.ModelVersion} 转写缓存"; if (!ReferenceEquals(activeTranscription, transcription) && activeTranscription is IDisposable cachedDisposable) cachedDisposable.Dispose(); return; }
        IsBusy=true;
        try { StatusText=cached is null?$"正在使用 {activeTranscription.ModelVersion} 提取音频并转写…":"旧转写质量异常，正在重新识别…"; var json=await activeTranscription.TranscribeAsync(video);
            if (!IsUsableTranscript(json))
            {
                TranscriptText = "模型未能可靠识别出语音；未保存本次结果。音频可能以音乐、噪声或过短语音为主。";
                StatusText = "本地转写完成，但结果为空或存在明显重复幻觉，已拒绝缓存";
                return;
            }
            await UiWork.RunDatabaseAsync(()=>repository.SaveTranscript(video.Id,activeTranscription.ModelVersion,json,video));
            _services.QueueImmediateRemoteSync("视频转写写入");
            TranscriptText=ExtractTranscriptText(json); StatusText=$"本地转写完成（{activeTranscription.ModelVersion}）"; }
        catch(Exception ex){AppLogger.Error($"本地音频转写失败：{SelectedVideo?.Record.Path}",ex);StatusText=$"转写失败：{CompactError(ex.Message)}";} finally{if (!ReferenceEquals(activeTranscription, transcription) && activeTranscription is IDisposable disposable) disposable.Dispose();IsBusy=false;}
    }

    [RelayCommand]
    private async Task AnalyzePeopleAsync()
    {
        var selected = SelectedVideo;
        if(selected is null){StatusText="请先选择视频";AppLogger.Warn("视频人物分析未启动：没有选择视频");return;}
        if (remoteMode)
        {
            IsBusy = true;
            try
            {
                var backend = AppPaths.LoadIndexBackend();
                if (string.IsNullOrWhiteSpace(backend.BaseUrl)) throw new InvalidOperationException("IndexService 端点未配置");

                StatusText = $"正在通过 Service 分析视频人物：{selected.FileName}…";
                if (_services.Gateway is not HttpIndexGateway gateway)
                    throw new InvalidOperationException("IndexService 网关尚未连接");
                await gateway.AnalyzeVideoPeopleAsync(selected.Record.Id);
                StatusText = $"视频人物分析已完成：{selected.FileName}；正在同步人物标注…";
                try
                {
                    await _services.SyncRemoteMirrorAsync(requireMetadataSync: true);
                }
                catch (OperationCanceledException syncException)
                {
                    AppLogger.Warn($"视频人物分析完成但远程镜像同步延迟；video={selected.Record.Id}: {syncException.Message}");
                    throw new InvalidOperationException("本地索引同步已取消；Service 分析已经完成，请稍后重试同步，不要重新分析。");
                }
                catch (Exception)
                {
                    throw new InvalidOperationException("Service 已完成分析，但本地索引同步失败，请重试同步，不要重复分析。");
                }
                _selectedVideoLoadCancellation?.Cancel();
                _selectedVideoLoadCancellation?.Dispose();
                _selectedVideoLoadCancellation = new CancellationTokenSource();
                await LoadSelectedVideoAsync(selected, _selectedVideoLoadCancellation.Token);

                StatusText = $"视频人物分析任务已完成：{selected.FileName}";
                AppLogger.Info($"远程视频人物分析完成；video={selected.Record.Id}");
            }
            catch (Exception ex) { StatusText = $"视频人物分析失败：{CompactError(ex.Message)}"; AppLogger.Error($"远程视频人物分析失败；video={selected.Record.Id}", ex); }
            finally { IsBusy = false; }
            return;
        }
        if(faceTimeline is null){StatusText="人脸模型尚未就绪";AppLogger.Warn($"视频人物分析未启动：人脸模型不可用；video={selected.Record.Id}");return;}
        if(IsBusy){StatusText=$"已有视频任务正在执行，请完成后再分析 {selected.FileName}";AppLogger.Info($"视频人物分析未启动：已有任务占用；video={selected.Record.Id}");return;}
        StatusText=$"已收到人物分析请求：{selected.FileName}；正在检查关键帧…";
        AppLogger.Info($"视频人物分析请求：video={selected.Record.Id}; path={selected.Record.Path}");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        var keyframeCount=await UiWork.RunDatabaseAsync("VideoPeople.CheckKeyframes",()=>repository.GetKeyframes(selected.Record.Id, existingOnly: !remoteMode).Count);
        if(keyframeCount==0){StatusText=$"无法分析 {selected.FileName}：尚未生成关键帧";AppLogger.Warn($"视频人物分析未启动：无关键帧；video={selected.Record.Id}");return;}
        IsBusy=true;
        try
        {
            StatusText=$"正在准备人物分析：{selected.FileName}，共 {keyframeCount} 张关键帧…";
            AppLogger.Info($"视频人物分析开始：video={selected.Record.Id}; keyframes={keyframeCount}");
            var progress = new Progress<(int Completed, int Total)>(value =>
                StatusText = $"正在分析 {selected.FileName}：关键帧 {value.Completed}/{value.Total}");
            var count=await faceTimeline.AnalyzeAsync(selected.Record, progress: progress);
            await UiWork.RunDatabaseAsync("VideoPeople.SaveCompleted",()=>repository.SavePersonAnalysisStatus(selected.Record.Id,"completed",selected.Record));
            _services.QueueImmediateRemoteSync("视频人物分析状态写入");
            var view=await UiWork.RunDatabaseAsync("VideoPeople.LoadResult",()=>(Timeline:faceTimeline.GetTimeline(selected.Record.Id),People:peopleService?.GetAll().ToArray()??[]));
            if(SelectedVideo?.Record.Id==selected.Record.Id){PeopleTimelineText=FormatTimeline(view.Timeline);PopulateVideoFaces(view.Timeline,view.People);}
            await LoadAsync();
            SelectedVideo=Videos.FirstOrDefault(video=>video.Record.Id==selected.Record.Id);
            StatusText=count==0?$"人物分析完成：{selected.FileName} 未检测到人脸":$"人物分析完成：{selected.FileName}，检测到 {count} 个人脸片段";
            AppLogger.Info($"视频人物分析完成：video={selected.Record.Id}; faces={count}");
        }
        catch(Exception ex){AppLogger.Error($"视频人物分析失败：{selected.Record.Path}", ex);await UiWork.RunDatabaseAsync("VideoPeople.SaveFailed",()=>repository.SavePersonAnalysisStatus(selected.Record.Id,"failed",selected.Record));_services.QueueImmediateRemoteSync("视频人物分析失败状态写入");StatusText=$"人物分析失败：{selected.FileName}；{CompactError(ex.Message)}；详细信息已写入日志";}finally{IsBusy=false;}
    }

    private string FormatTimeline(IReadOnlyList<VideoFaceTimelineRecord>? faces)
    {
        if (faces is null || faces.Count == 0)
            return SelectedVideo?.EmptyTimelineText(faceTimeline?.PreferredModelVersion) ?? "请选择视频查看人物标注";

        var timeline = string.Join(Environment.NewLine, faces.Select(face =>
            $"{TimeSpan.FromSeconds(face.TimestampSeconds):hh\\:mm\\:ss}  {face.EffectivePersonName ?? "未知人物"}  {(face.MatchConfidence is null ? "" : $"匹配 {face.MatchConfidence:P0}")}"));
        var usedModels = faces.Select(face => face.ModelVersion).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (usedModels.Length == 1 && !string.Equals(usedModels[0], faceTimeline?.PreferredModelVersion, StringComparison.OrdinalIgnoreCase))
            return $"备用模型空间：{usedModels[0]}{Environment.NewLine}{timeline}";
        return timeline;
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task GenerateSummaryAsync()
    {
        if (SelectedVideo is null) { StatusText = "请先选择视频"; return; }
        if (summaryService is null) { StatusText = "视频摘要组件尚未就绪"; return; }
        var selected = SelectedVideo.Record;
        var configured = _services.CreateCloudVisionService(AiFeatureIds.VideoSummary);
        if (configured is null) { StatusText = "请先在 AI 模型设置中为视频内容总结选择本地 VL 或云端多模态模型"; return; }
        using var analyzer = configured.Value.Analyzer;
        if (await UiWork.RunDatabaseAsync(() => repository.GetKeyframes(selected.Id, existingOnly: !remoteMode).Count) == 0) { StatusText = "请先生成关键帧"; return; }
        var transcriptNotice = await UiWork.RunDatabaseAsync(() => repository.GetTranscript(selected.Id)) is null ? "不含语音转写" : "包含本地语音转写文本";
        var locationNotice = configured.Value.Policy.IncludePreciseGps ? "允许提供该视频的精确位置" : "不提供精确位置";
        var confirmation = configured.Value.IsLocal
            ? $"将把关键帧拼图（最多12帧）经已配置索引服务转发至所选本地模型，{transcriptNotice}，{locationNotice}。是否继续？"
            : $"将上传去除元数据的关键帧拼图（最多12帧），{transcriptNotice}，{locationNotice}，用于生成视频融合摘要。原视频不会上传。是否继续？";
        if (MessageBox.Show(confirmation, configured.Value.IsLocal ? "确认本地视频分析" : "确认云端视频分析",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            StatusText = "正在生成并分析关键帧拼图…";
            var summaryFrames = await UiWork.RunDatabaseAsync(() => repository.GetKeyframes(selected.Id, existingOnly: !remoteMode));
            if (remoteMode) summaryFrames = await MaterializeRemoteFramesAsync(_services, summaryFrames, selected.Id);
            var result = await summaryService.AnalyzeAsync(selected, analyzer, summaryFrames, CancellationToken.None, configured.Value.Policy);
            if (SelectedVideo?.Record.Id == selected.Id && SelectedVideo.Record.Sha256 == selected.Sha256)
                SummaryText = result.Summary;
            if (remoteMode) await _services.SyncLocalChangesToRemoteAsync();
            StatusText = $"视频融合摘要完成；本次估算费用 ¥{result.Usage?.EstimatedCostYuan ?? 0:0.0000}";
        }
        catch (Exception ex)
        {
            AppLogger.Error($"视频融合摘要失败；video={selected.Id}; remote={remoteMode}; endpoint=IndexService", ex);
            StatusText = $"视频融合摘要失败：{ex.Message}；详细信息已写入日志";
        }
        finally { IsBusy = false; }
    }

    internal static async Task<IReadOnlyList<VideoKeyframe>> MaterializeRemoteFramesAsync(
        ClientServices _services,
        IReadOnlyList<VideoKeyframe> frames, long videoId, CancellationToken cancellationToken = default)
    {
        if (_services.Gateway is not Picer.Core.Gateway.HttpIndexGateway gateway) return frames;
        var video = await UiWork.RunDatabaseAsync(() => _services.Videos.GetAll().FirstOrDefault(item => item.Id == videoId), cancellationToken)
            ?? throw new InvalidOperationException("video_reference_missing");
        using var operation = VideoOperationLifetime.Begin(video.Path, cancellationToken);
        cancellationToken = operation.Token;
        var directory = Path.Combine(AppPaths.DataRoot, "remote-video-keyframes", videoId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        using var concurrency = new SemaphoreSlim(4, 4);
        var downloads = frames.Select(async (frame, ordinal) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // v2 files have passed image decoding before publication. The id
            // and ordinal also disambiguate rounded/equal frame timestamps.
            var target = Path.Combine(directory, $"v2-{frame.Id}-{ordinal}-{frame.DifferenceHash}.jpg");
            if (File.Exists(target) && new FileInfo(target).Length > 0)
                return (ordinal, Frame: (VideoKeyframe?)(frame with { Path = target }));

            await concurrency.WaitAsync(cancellationToken);
            try
            {
                if (File.Exists(target) && new FileInfo(target).Length > 0)
                    return (ordinal, Frame: (VideoKeyframe?)(frame with { Path = target }));
                try
                {
                    await using var stream = (frame.Id > 0 ? await gateway.OpenVideoKeyframeAsync(frame.Id, cancellationToken) : null)
                        ?? await gateway.OpenVideoKeyframeByOrdinalAsync(videoId, ordinal, cancellationToken);
                    if (stream is null)
                    {
                        AppLogger.Warn($"Remote keyframe binary unavailable: video={videoId}; frame={frame.Id}; ordinal={ordinal}; timestamp={frame.TimestampSeconds:0.###}");
                        return (ordinal, Frame: (VideoKeyframe?)null);
                    }
                    await VideoKeyframeCache.WriteAsync(stream, target, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or
                    UnauthorizedAccessException or InvalidDataException)
                {
                    AppLogger.Warn($"Remote keyframe download failed: video={videoId}; frame={frame.Id}; ordinal={ordinal}; errorType={exception.GetType().Name}");
                    return (ordinal, Frame: (VideoKeyframe?)null);
                }
                return (ordinal, Frame: (VideoKeyframe?)(frame with { Path = target }));
            }
            finally
            {
                concurrency.Release();
            }
        }).ToArray();
        var result = await Task.WhenAll(downloads);
        return result.Where(item => item.Frame is not null).OrderBy(item => item.ordinal)
            .Select(item => item.Frame!).ToArray();
    }

    private static string ExtractTranscriptText(string? json)
    {
        if(string.IsNullOrWhiteSpace(json)) return "尚未转写";
        try { using var document=JsonDocument.Parse(json); var texts=new List<string>(); Collect(document.RootElement,texts); return texts.Count==0?"转写完成，但没有识别到语音":string.Join(" ",texts); }
        catch(JsonException){return json;}
        static void Collect(JsonElement element,List<string> texts){
            if(element.ValueKind==JsonValueKind.Object){foreach(var property in element.EnumerateObject()){if(property.NameEquals("text")&&property.Value.ValueKind==JsonValueKind.String){var value=property.Value.GetString();if(!string.IsNullOrWhiteSpace(value))texts.Add(value.Trim());}else Collect(property.Value,texts);}}
            else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())Collect(child,texts);
        }
    }

    private static bool IsUsableTranscript(string? json)
    {
        var text = ExtractTranscriptText(json);
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("尚未", StringComparison.Ordinal) ||
            text.StartsWith("转写完成，但没有", StringComparison.Ordinal)) return false;
        var significant = text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)).ToArray();
        if (significant.Length < 2) return false;
        var dominant = significant.GroupBy(character => character).Max(group => group.Count());
        return significant.Length < 12 || dominant < significant.Length * 0.65;
    }

    private static string CompactError(string message)
    {
        var firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? "未知错误";
        return firstLine.Length <= 180 ? firstLine : firstLine[..180] + "…";
    }

    private bool CanStartOperation() => !IsBusy;
}

public sealed class VideoItemViewModel
{
    public VideoItemViewModel(VideoRecord record, int keyframeCount, VideoFaceReviewSummary? faceReview = null)
    { Record = record; KeyframeCount = keyframeCount; FaceReview = faceReview ?? new(0, 0, 0, 0, null); }
    public VideoRecord Record { get; }
    public string FileName => System.IO.Path.GetFileName(Record.Path);
    public string Path => Record.Path;
    public string Duration => Record.DurationSeconds is null ? "未探测" : TimeSpan.FromSeconds(Record.DurationSeconds.Value).ToString(@"hh\:mm\:ss");
    public string Resolution => Record.Width > 0 ? $"{Record.Width}×{Record.Height}" : "未知";
    public int KeyframeCount { get; }
    public VideoFaceReviewSummary FaceReview { get; }
    public bool HasPendingFaces => FaceReview.Pending > 0;
    public bool HasFailedFaceAnalysis => FaceReview.AnalysisStatus == "failed";
    public bool HasCompletedWithoutFaces => FaceReview.AnalysisStatus == "completed" && FaceReview.Total == 0;
    public string EmptyTimelineText(string? preferredModel) => FaceReview.AnalysisStatus switch
    {
        "failed" => "人物分析失败，可重新分析",
        "completed" when FaceReview.Total == 0 => "已分析·未发现人脸",
        "completed" => $"索引中有 {FaceReview.Total} 条人脸记录，但默认模型空间“{preferredModel ?? "未知"}”无可显示结果；可能是关键帧关联缺失。",
        _ when FaceReview.Total > 0 => $"索引中有 {FaceReview.Total} 条人脸记录，但默认模型空间“{preferredModel ?? "未知"}”无可显示结果；可能是关键帧关联缺失。",
        _ => "尚未分析视频人物"
    };
    /// <summary>按实际已有结果组合显示分析类型；没有结果时为空。</summary>
    public string AnalysisStatusText
    {
        get
        {
            var parts = new List<string>(4);
            if (KeyframeCount > 0) parts.Add("帧");
            if (FaceReview.Total > 0 || FaceReview.AnalysisStatus == "completed") parts.Add("人物");
            if (FaceReview.HasTranscript) parts.Add("对话");
            if (FaceReview.HasSummary) parts.Add("摘要");
            return string.Join("|", parts);
        }
    }
    public string FaceReviewText => FaceReview.AnalysisStatus == "failed" ? "分析失败" : FaceReview.Total == 0
        ? FaceReview.AnalysisStatus == "completed" ? "已分析·未发现人脸" : "未分析"
        : FaceReview.Pending > 0
        ? $"待确认 {FaceReview.Pending}"
        : $"已完成 {FaceReview.Confirmed + FaceReview.Ignored}/{FaceReview.Total}";
}

public sealed class VideoKeyframeItemViewModel(VideoKeyframe frame)
{
    public string Path { get; } = frame.Path;
    public string Timestamp => TimeSpan.FromSeconds(frame.TimestampSeconds).ToString(@"hh\:mm\:ss");
}

public sealed record VideoFaceGroupViewModel(string Key, string DisplayName, long? PersonId = null);

public sealed partial class VideoFaceItemViewModel(VideoFaceTimelineRecord record) : ObservableObject
{
    public VideoFaceTimelineRecord Record { get; } = record;
    public string KeyframePath => Record.KeyframePath;
    public string Timestamp => TimeSpan.FromSeconds(Record.TimestampSeconds).ToString(@"hh\:mm\:ss");
    public string Identity => Record.AssignmentState switch
    {
        "confirmed" => $"已确认：{Record.AssignedPersonName}",
        "ignored" => "不分类",
        _ when Record.SuggestedPersonName is not null => $"推荐：{Record.SuggestedPersonName}（{Record.MatchConfidence:P0}）",
        _ => "待确认"
    };
    public string ModelVersionText => string.Equals(Record.ModelVersion, "legacy-video", StringComparison.OrdinalIgnoreCase)
        ? "模型未知（旧记录，重新分析后可确认）"
        : string.Equals(Record.ModelVersion, "insightface-arcface", StringComparison.OrdinalIgnoreCase)
            ? "识别模型：InsightFace"
            : $"识别模型：{Record.ModelVersion}";
    public bool HasSuggestion => Record.IsSuggested;
    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private bool _isThumbnailLoading;
    public bool HasThumbnail => Thumbnail is not null;
    public bool ThumbnailLoadAttempted { get; private set; }

    public async Task LoadThumbnailAsync()
    {
        if (HasThumbnail || IsThumbnailLoading) return;
        IsThumbnailLoading = true;
        ThumbnailLoadAttempted = true;
        try { Thumbnail = await Task.Run(() =>
        {
            if (!File.Exists(Record.KeyframePath)) return null;
            using var stream = File.OpenRead(Record.KeyframePath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var source = decoder.Frames[0];
            var paddingX = Record.Box.Width * .35f;
            var paddingTop = Record.Box.Height * .35f;
            var paddingBottom = Record.Box.Height * .75f;
            var x = Math.Max(0, (int)((Record.Box.X - paddingX) * source.PixelWidth));
            var y = Math.Max(0, (int)((Record.Box.Y - paddingTop) * source.PixelHeight));
            var right = Math.Min(source.PixelWidth, (int)((Record.Box.X + Record.Box.Width + paddingX) * source.PixelWidth));
            var bottom = Math.Min(source.PixelHeight, (int)((Record.Box.Y + Record.Box.Height + paddingBottom) * source.PixelHeight));
            if (right <= x || bottom <= y) return null;
            var crop = new CroppedBitmap(source, new Int32Rect(x, y, right - x, bottom - y));
            crop.Freeze();
            return (ImageSource)crop;
        }); }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException or System.Runtime.InteropServices.COMException)
        {
            AppLogger.Warn($"Video face thumbnail unavailable: face={Record.FaceId}; errorType={exception.GetType().Name}");
        }
        finally { IsThumbnailLoading = false; }
    }
}
