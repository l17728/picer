using System.IO;
using System.Windows.Threading;
using Picer.App.ViewModels;
using Picer.Core.Data;
using Picer.Core.People;
using Picer.Core.Video;
using Xunit.Abstractions;

namespace Picer.E2E;

public sealed class VideoDetailsRegressionTests(ITestOutputHelper output)
{
    private void PrepareDatabase(string path)
    {
        // Schema setup is not part of the UI response deadline. Keep the existing
        // STA-thread deadlines so a stalled dispatcher still fails promptly.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using (var database = new PicerDatabase(path)) { }
        output.WriteLine($"数据库准备耗时：{timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ClearVideoAssignmentReturnsTheSelectedFaceToUnassignedAndQueuesSync()
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-video-unassign-"+Guid.NewGuid().ToString("N"));
        PrepareDatabase(Path.Combine(root, "picer.db"));
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async ()=>
            {
                try
                {
                    using var database=new PicerDatabase(Path.Combine(root,"picer.db"));
                    var source=database.AddSource(root);var repository=new VideoRepository(database);
                    repository.UpsertMirrored(new(1,source.Id,Path.Combine(root,"video.mp4"),10,DateTime.UnixEpoch,10,100,100,null,null,"active"),source.Id);
                    database.UpsertMirroredPerson(new(1,"视频命令测试人物",0,0));
                    var now=DateTime.UtcNow.ToString("O");
                    database.UpsertMirroredVideoMetadata(new Picer.Core.Gateway.GatewayMetadata([],[],[]) with
                    {
                        VideoKeyframes=[new(10,1,0,"",0)],
                        VideoFaces=[new(11,1,10,0,0,0,0,10,10,0.9,null,null,null,1,"confirmed",now,now) { ModelVersion="test-model" }]
                    });
                    var timeline=new VideoFaceTimelineService(database,new ExistingVideoFacesAnalyzer(),new Picer.Core.Vision.VisionAnalysisRepository(database));
                    var queued=0;
                    var services=new Picer.App.ClientServices { Db=database,Videos=repository,PublishChanges=_=>queued++ };
                    var model=new VideoViewModel(repository,new VideoIndexService(database,repository),null,
                        faceTimeline:timeline,peopleService:new PeopleService(database),services:services);
                    await model.LoadAsync();model.SelectedVideo=Assert.Single(model.Videos);
                    await WaitUntilAsync(()=>model.VideoFaces.Count==1);
                    model.SelectedVideoFace=Assert.Single(model.VideoFaces);
                    await model.ClearVideoFaceAssignmentCommand.ExecuteAsync(null);
                    Assert.Null(Assert.Single(database.GetAllMirroredVideoFaces()).AssignedPersonId);
                    Assert.Equal("unassigned",PicerDatabase.ParseFaceManualState(database.GetMetadataEdits().Single(edit=>edit.Kind=="video-face-state").Value).State);
                    Assert.Contains("移回待分类",model.StatusText);Assert.True(queued>0);
                }
                catch(Exception exception) { failure=exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)),"视频人物撤回命令测试超时");
        try { Assert.Null(failure); } finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }

    private sealed class ExistingVideoFacesAnalyzer : Picer.Core.Vision.IImageFaceAnalyzer
    {
        public string AnalyzerName=>"test";
        public string ModelVersion=>"test-model";
        public Task<Picer.Core.Vision.FaceAnalysisResult> AnalyzeAsync(Picer.Core.Models.ImageRecord image,CancellationToken cancellationToken)
            =>throw new InvalidOperationException("This test must not start model analysis.");
    }

    [Fact]
    public void PartialVideoPurgeRefreshesCatalogAndRetryOnlyProcessesRemainingSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-video-purge-retry-" + Guid.NewGuid().ToString("N"));
        PrepareDatabase(Path.Combine(root, "index.db"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    using var db = new PicerDatabase(Path.Combine(root, "index.db"));
                    var source = db.AddSource(root); var repository = new VideoRepository(db);
                    foreach (var name in new[] { "a.mp4", "b.mp4" })
                    { var path = Path.Combine(root, name); File.WriteAllBytes(path, [1]); repository.Upsert(source.Id, path); }
                    var calls = new List<long>(); var failOnce = true;
                    var services = new Picer.App.ClientServices
                    {
                        Db = db, Videos = repository, ConfirmVideoPurge = _ => true,
                        PurgeVideo = (video, _) =>
                        {
                            calls.Add(video.Id);
                            if (calls.Count == 2 && failOnce) { failOnce = false; throw new IOException("fixture_retry"); }
                            using var independent = db.OpenIndependentConnection();
                            new VideoRecordPurger(independent).Purge(video.Id); return Task.CompletedTask;
                        }
                    };
                    var model = new VideoViewModel(repository, new VideoIndexService(db, repository), null, services: services);
                    await model.LoadAsync(); model.UpdateSelection(model.Videos.ToArray());
                    await model.PurgeIndexCommand.ExecuteAsync(null);
                    var remaining = Assert.Single(model.Videos).Record.Id;
                    Assert.Equal(remaining, model.SelectedVideo?.Record.Id); Assert.Contains("已清除 1 项", model.StatusText);
                    await model.PurgeIndexCommand.ExecuteAsync(null);
                    Assert.Empty(model.Videos); Assert.Equal(3, calls.Count); Assert.Equal(remaining, calls[2]); Assert.NotEqual(calls[0], calls[2]);
                    Assert.Equal(2, Directory.GetFiles(root, "*.mp4").Length);
                }
                catch (Exception exception) { failure = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "视频批量清除重试测试超时");
        try { Assert.Null(failure); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void PurgeCommandRequiresConfirmationRemovesIndexAndKeepsOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-video-purge-ui-" + Guid.NewGuid().ToString("N"));
        PrepareDatabase(Path.Combine(root, "index.db"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    using var db = new PicerDatabase(Path.Combine(root, "index.db"));
                    var path = Path.Combine(root, "clip.mp4"); File.WriteAllBytes(path, [1, 2, 3]);
                    var repository = new VideoRepository(db); repository.Upsert(db.AddSource(root).Id, path);
                    var confirmed = false; var calls = 0;
                    var services = new Picer.App.ClientServices
                    {
                        Db = db, Videos = repository,
                        ConfirmVideoPurge = count => { Assert.Equal(1, count); return confirmed; },
                        PurgeVideo = (video, _) =>
                        {
                            calls++;
                            using var independent = db.OpenIndependentConnection();
                            new VideoRecordPurger(independent).Purge(video.Id); return Task.CompletedTask;
                        }
                    };
                    var model = new VideoViewModel(repository, new VideoIndexService(db, repository), null, services: services);
                    await model.LoadAsync(); model.SelectedVideo = Assert.Single(model.Videos);
                    await model.PurgeIndexCommand.ExecuteAsync(null);
                    Assert.Equal(0, calls); Assert.Single(repository.GetAll());
                    model.IsBusy = true; Assert.False(model.PurgeIndexCommand.CanExecute(null)); model.IsBusy = false;
                    confirmed = true;
                    await model.PurgeIndexCommand.ExecuteAsync(null);
                    Assert.Equal(1, calls); Assert.Empty(repository.GetAll()); Assert.Empty(model.Videos);
                    Assert.Contains("已彻底清除 1 个视频索引", model.StatusText);
                    Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
                }
                catch (Exception exception) { failure = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "视频清除命令测试超时");
        try { Assert.Null(failure); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData(0, 0, null, false, false, "")]
    [InlineData(2, 0, null, false, false, "帧")]
    [InlineData(0, 0, null, true, false, "对话")]
    [InlineData(1, 2, "completed", true, true, "帧|人物|对话|摘要")]
    public void AnalysisStatusText_IncludesOnlyAvailableFrameAndAnalysisStates(
        int frameCount, int faceCount, string? faceAnalysisStatus, bool hasTranscript, bool hasSummary, string expected)
    {
        var record = new VideoRecord(1, 1, "video.mp4", 0, DateTime.UnixEpoch, null, 0, 0, null, null, "active");
        var summary = new VideoFaceReviewSummary(faceCount, 0, faceCount, 0, faceAnalysisStatus, hasTranscript, hasSummary);
        var item = new VideoItemViewModel(record, frameCount, summary);

        Assert.Equal(expected, item.AnalysisStatusText);
    }

    [Fact]
    public void EmptyTimelineText_DistinguishesCompletedWithoutFacesFromMissingModelRows()
    {
        var record = new VideoRecord(1, 1, "video.mp4", 0, DateTime.UnixEpoch, null, 0, 0, null, null, "active");
        var noFaces = new VideoItemViewModel(record, 1,
            new VideoFaceReviewSummary(0, 0, 0, 0, "completed"));
        var modelMismatch = new VideoItemViewModel(record, 1,
            new VideoFaceReviewSummary(4, 4, 0, 0, "completed"));

        Assert.Equal("已分析·未发现人脸", noFaces.EmptyTimelineText("insightface-arcface"));
        Assert.Contains("4 条人脸记录", modelMismatch.EmptyTimelineText("insightface-arcface"));
        Assert.Contains("insightface-arcface", modelMismatch.EmptyTimelineText("insightface-arcface"));
    }

    [Fact]
    public void SlowPreviousVideo_CannotOverwriteNewSelection_AndTextDoesNotWaitForFrames()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-video-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PrepareDatabase(Path.Combine(root, "index.db"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    using var database = new PicerDatabase(Path.Combine(root, "index.db"));
                    var source = database.AddSource(root);
                    var repository = new VideoRepository(database);
                    var videos = new List<VideoRecord>();
                    for (var i = 0; i < 2; i++)
                    {
                        var path = Path.Combine(root, $"video-{i}.mp4");
                        File.WriteAllBytes(path, [(byte)i]);
                        var video = repository.Upsert(source.Id, path);
                        repository.SaveKeyframes(video.Id, [new VideoKeyframe(i + 1, Path.Combine(root, $"frame-{i}.jpg"))]);
                        repository.SaveTranscript(video.Id, "test", $"{{\"text\":\"transcript-{i}\"}}");
                        videos.Add(video);
                    }
                    var firstDownload = new TaskCompletionSource<IReadOnlyList<VideoKeyframe>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var viewModel = new VideoViewModel(repository, new VideoIndexService(database, repository), null,
                        remoteMode: true, remoteFrameLoader: (frames, id, _) =>
                            id == videos[0].Id ? firstDownload.Task : Task.FromResult(frames));
                    await viewModel.LoadAsync();
                    viewModel.SelectedVideo = viewModel.Videos.Single(video => video.Record.Id == videos[0].Id);
                    await WaitUntilAsync(() => viewModel.TranscriptText == "transcript-0");
                    Assert.Empty(viewModel.Frames); // metadata does not wait for the slow download
                    viewModel.SelectedVideo = viewModel.Videos.Single(video => video.Record.Id == videos[1].Id);
                    await WaitUntilAsync(() => viewModel.Frames.Count == 1);
                    // Simulate a transport which ignores cancellation and finishes late.
                    firstDownload.SetResult(repository.GetKeyframes(videos[0].Id, existingOnly: false));
                    await Task.Delay(100);
                    Assert.Equal(videos[1].Id, viewModel.SelectedVideo?.Record.Id);
                    Assert.Equal("transcript-1", viewModel.TranscriptText);
                    Assert.EndsWith("frame-1.jpg", Assert.Single(viewModel.Frames).Path, StringComparison.Ordinal);
                }
                catch (Exception exception) { failure = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "快速切换视频用例超时");
        try { Assert.Null(failure); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptVideoFaceThumbnail_CompletesWithoutUnobservedFailure(bool corrupt)
    {
        var path = Path.Combine(Path.GetTempPath(), "picer-invalid-frame-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            if (corrupt) await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var face = new VideoFaceItemViewModel(new VideoFaceTimelineRecord(1, path,
                new Picer.Core.Vision.NormalizedFaceBox(0, 0, 1, 1), 1, 123, null, null, null, null, null, null));
            await face.LoadThumbnailAsync();
            Assert.False(face.HasThumbnail);
            Assert.False(face.IsThumbnailLoading);
            Assert.True(face.ThumbnailLoadAttempted);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SelectedVideoDetails_AreRestoredAfterCatalogRefresh(bool remote, bool downloadFails)
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-video-details-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PrepareDatabase(Path.Combine(root, "index.db"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var database = new PicerDatabase(Path.Combine(root, "index.db"));
                var source = database.AddSource(root);
                var videoPath = Path.Combine(root, "family.mp4");
                var framePath = Path.Combine(root, "family-frame.jpg");
                File.WriteAllBytes(videoPath, [1, 2, 3]);
                File.WriteAllBytes(framePath, [4, 5, 6]);
                var repository = new VideoRepository(database);
                var video = repository.Upsert(source.Id, videoPath);
                repository.SaveKeyframes(video.Id, [new VideoKeyframe(1.5, framePath)]);
                repository.SaveTranscript(video.Id, "test-asr", "{\"text\":\"family conversation\"}");
                using (var summary = database.Command("""
                           INSERT INTO video_summaries(video_id,provider,model_version,prompt_version,source_file_size,
                               source_modified_utc,summary,result_json,created_utc)
                           VALUES($video,'test','test','test',0,'','family summary','{}',$now)
                           """))
                {
                    summary.Parameters.AddWithValue("$video", repository.GetContentId(video.Id));
                    summary.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                    summary.ExecuteNonQuery();
                }

                var viewModel = new VideoViewModel(repository, new VideoIndexService(database, repository), null,
                    peopleService: new PeopleService(database), remoteMode: remote,
                    remoteFrameLoader: async (frames, _, token) =>
                    {
                        await Task.Delay(100, token);
                        if (downloadFails) throw new IOException("simulated relay unavailable");
                        return frames;
                    });
                var dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await viewModel.LoadAsync();
                        viewModel.SelectedVideo = Assert.Single(viewModel.Videos);
                        await WaitUntilAsync(() => downloadFails ? viewModel.StatusText.Contains("关键帧加载失败", StringComparison.Ordinal) : viewModel.Frames.Count == 1);

                        await viewModel.LoadAsync();
                        Assert.Equal(video.Id, viewModel.SelectedVideo?.Record.Id);
                        await WaitUntilAsync(() => downloadFails ? viewModel.StatusText.Contains("关键帧加载失败", StringComparison.Ordinal) : viewModel.Frames.Count == 1);
                        Assert.Contains("family conversation", viewModel.TranscriptText, StringComparison.Ordinal);
                        Assert.Equal("family summary", viewModel.SummaryText);
                        if (downloadFails) Assert.Empty(viewModel.Frames);
                        else Assert.Equal(framePath, Assert.Single(viewModel.Frames).Path);
                    }
                    catch (Exception exception) { failure = exception; }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                }));
                Dispatcher.Run();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "视频详情刷新回归用例超时");
        try { Assert.Null(failure); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException("视频详情未能在时限内恢复关键帧、转写和摘要。");
            await Task.Delay(10);
        }
    }
}
