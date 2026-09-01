using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core.Video;
using Picer.App.Infrastructure;

namespace Picer.App.ViewModels;

public partial class VideoViewModel(VideoRepository repository, VideoIndexService index, FfmpegKeyframeService? keyframes,
    WhisperTranscriptionService? transcription = null, VideoFaceTimelineService? faceTimeline = null,
    VideoSummaryService? summaryService = null) : ObservableObject
{
    private CancellationTokenSource? _loadCancellation;
    public ObservableCollection<VideoItemViewModel> Videos { get; } = new();
    public ObservableCollection<VideoKeyframeItemViewModel> Frames { get; } = new();
    [ObservableProperty] private VideoItemViewModel? _selectedVideo;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = keyframes is null ? "FFmpeg尚未就绪" : "就绪";
    [ObservableProperty] private string _transcriptText = "尚未转写";
    [ObservableProperty] private string _peopleTimelineText = "尚未分析视频人物";

    [ObservableProperty] private string _summaryText = "尚未生成融合摘要";

    public async Task LoadAsync(string? query = null)
    {
        _loadCancellation?.Cancel();
        var cancellation = _loadCancellation = new CancellationTokenSource();
        var rows = await UiWork.RunDatabaseAsync(() =>
            (string.IsNullOrWhiteSpace(query) ? repository.GetAll() : repository.Search(query))
            .Select(video => new VideoItemViewModel(video, repository.GetKeyframes(video.Id).Count)).ToArray(), cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        Videos.Clear();
        await UiWork.AddInBatchesAsync(rows, Videos.Add, cancellation.Token);
        StatusText = string.IsNullOrWhiteSpace(query) ? $"共 {Videos.Count} 个视频" : $"视频搜索命中 {Videos.Count} 个";
    }

    public Task ApplySearchAsync(string? query) => LoadAsync(query);

    partial void OnSelectedVideoChanged(VideoItemViewModel? value)
    {
        Frames.Clear();
        if (value is null)
        {
            TranscriptText = "尚未转写"; PeopleTimelineText = "尚未分析视频人物"; SummaryText = "尚未生成融合摘要";
            return;
        }
        _ = LoadSelectedVideoAsync(value);
    }

    private async Task LoadSelectedVideoAsync(VideoItemViewModel selected)
    {
        var snapshot = await UiWork.RunDatabaseAsync(() => (
            Transcript: repository.GetTranscript(selected.Record.Id),
            Timeline: faceTimeline?.GetTimeline(selected.Record.Id),
            Summary: repository.GetSummary(selected.Record.Id),
            Frames: repository.GetKeyframes(selected.Record.Id).ToArray()));
        if (SelectedVideo?.Record.Id != selected.Record.Id) return;
        TranscriptText = ExtractTranscriptText(snapshot.Transcript);
        PeopleTimelineText = FormatTimeline(snapshot.Timeline);
        SummaryText = snapshot.Summary ?? "尚未生成融合摘要";
        await UiWork.AddInBatchesAsync(snapshot.Frames, frame => Frames.Add(new VideoKeyframeItemViewModel(frame)), batchSize: 30);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        try { var count = await index.ScanLocalSourcesAsync(); await LoadAsync(); StatusText = $"扫描完成，共发现 {count} 个视频"; }
        catch (Exception ex) { StatusText = $"扫描失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task GenerateKeyframesAsync()
    {
        if (SelectedVideo is null) { StatusText = "请先选择视频"; return; }
        if (keyframes is null) { StatusText = "FFmpeg尚未安装或路径无效"; return; }
        var cached = await UiWork.RunDatabaseAsync(() => repository.GetKeyframes(SelectedVideo.Record.Id));
        if (cached.Count > 0) { await LoadSelectedVideoAsync(SelectedVideo); StatusText = $"已复用 {cached.Count} 张缓存关键帧"; return; }
        IsBusy = true;
        try
        {
            StatusText = "正在读取视频信息…";
            var probe = await keyframes.ProbeAsync(SelectedVideo.Record.Path);
            await UiWork.RunDatabaseAsync(() => repository.SaveProbe(SelectedVideo.Record.Id, probe));
            StatusText = "正在识别镜头并生成关键帧…";
            var frames = await keyframes.ExtractAsync(SelectedVideo.Record);
            await UiWork.RunDatabaseAsync(() => repository.SaveKeyframes(SelectedVideo.Record.Id, frames));
            var selectedId = SelectedVideo.Record.Id;
            await LoadAsync(); SelectedVideo = Videos.FirstOrDefault(item => item.Record.Id == selectedId);
            StatusText = $"关键帧完成：{frames.Count} 张";
        }
        catch (Exception ex) { StatusText = $"关键帧失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedVideo is null) { StatusText = "请先选择视频"; return; }
        if (!File.Exists(SelectedVideo.Record.Path)) { StatusText = "原视频当前不可用，请重新扫描来源"; return; }
        try { Process.Start(new ProcessStartInfo(SelectedVideo.Record.Path) { UseShellExecute = true }); }
        catch (Exception exception) { StatusText = $"无法打开视频：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task TranscribeAsync()
    {
        if (SelectedVideo is null) { StatusText="请先选择视频"; return; }
        if (transcription is null) { StatusText="本地转写组件尚未就绪"; return; }
        var cached=await UiWork.RunDatabaseAsync(()=>repository.GetTranscript(SelectedVideo.Record.Id));
        if(cached is not null){ TranscriptText=ExtractTranscriptText(cached); StatusText="已复用本地转写缓存"; return; }
        IsBusy=true;
        try { StatusText="正在本地提取音频并转写…"; var json=await transcription.TranscribeAsync(SelectedVideo.Record);
            await UiWork.RunDatabaseAsync(()=>repository.SaveTranscript(SelectedVideo.Record.Id,transcription.ModelVersion,json)); TranscriptText=ExtractTranscriptText(json); StatusText="本地转写完成"; }
        catch(Exception ex){StatusText=$"转写失败：{ex.Message}";} finally{IsBusy=false;}
    }

    [RelayCommand]
    private async Task AnalyzePeopleAsync()
    {
        if(SelectedVideo is null){StatusText="请先选择视频";return;} if(faceTimeline is null){StatusText="人脸模型尚未就绪";return;}
        if(await UiWork.RunDatabaseAsync(()=>repository.GetKeyframes(SelectedVideo.Record.Id).Count)==0){StatusText="请先生成关键帧";return;} IsBusy=true;
        try{StatusText="正在建立视频人物时间轴…";var count=await faceTimeline.AnalyzeAsync(SelectedVideo.Record);PeopleTimelineText=FormatTimeline(await UiWork.RunDatabaseAsync(()=>faceTimeline.GetTimeline(SelectedVideo.Record.Id)));StatusText=$"人物时间轴完成：{count} 个人脸片段";}
        catch(Exception ex){StatusText=$"人物时间轴失败：{ex.Message}";}finally{IsBusy=false;}
    }

    private static string FormatTimeline(IReadOnlyList<VideoFaceTimelineRecord>? faces)=>faces is null||faces.Count==0?"尚未识别到人物":string.Join(Environment.NewLine,faces.Select(face=>$"{TimeSpan.FromSeconds(face.TimestampSeconds):hh\\:mm\\:ss}  {face.SuggestedPersonName??"未知人物"}  {(face.MatchConfidence is null?"":$"匹配 {face.MatchConfidence:P0}")}"));

    [RelayCommand]
    private async Task GenerateSummaryAsync()
    {
        if (SelectedVideo is null) { StatusText = "请先选择视频"; return; }
        if (summaryService is null) { StatusText = "视频摘要组件尚未就绪"; return; }
        var configured = AppServices.CreateCloudVisionService(useComplexModel: true);
        if (configured is null) { StatusText = "请先在云端分析设置中启用并配置国内视觉 API"; return; }
        if (await UiWork.RunDatabaseAsync(() => repository.GetKeyframes(SelectedVideo.Record.Id).Count) == 0) { StatusText = "请先生成关键帧"; return; }
        var transcriptNotice = await UiWork.RunDatabaseAsync(() => repository.GetTranscript(SelectedVideo.Record.Id)) is null ? "不含语音转写" : "包含本地语音转写文本";
        if (MessageBox.Show($"将上传去除元数据的关键帧拼图（最多12帧），{transcriptNotice}，用于生成视频融合摘要。原视频不会上传。是否继续？",
                "确认云端视频分析", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            StatusText = "正在生成并分析关键帧拼图…";
            using var analyzer = configured.Value.Analyzer;
            var result = await summaryService.AnalyzeAsync(SelectedVideo.Record, analyzer);
            SummaryText = result.Summary;
            StatusText = $"视频融合摘要完成；本次估算费用 ¥{result.Usage?.EstimatedCostYuan ?? 0:0.0000}";
        }
        catch (Exception ex) { StatusText = $"视频融合摘要失败：{ex.Message}"; }
        finally { IsBusy = false; }
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
}

public sealed class VideoItemViewModel
{
    public VideoItemViewModel(VideoRecord record, int keyframeCount) { Record = record; KeyframeCount = keyframeCount; }
    public VideoRecord Record { get; }
    public string FileName => System.IO.Path.GetFileName(Record.Path);
    public string Path => Record.Path;
    public string Duration => Record.DurationSeconds is null ? "未探测" : TimeSpan.FromSeconds(Record.DurationSeconds.Value).ToString(@"hh\:mm\:ss");
    public string Resolution => Record.Width > 0 ? $"{Record.Width}×{Record.Height}" : "未知";
    public int KeyframeCount { get; }
}

public sealed class VideoKeyframeItemViewModel(VideoKeyframe frame)
{
    public string Path { get; } = frame.Path;
    public string Timestamp => TimeSpan.FromSeconds(frame.TimestampSeconds).ToString(@"hh\:mm\:ss");
}
