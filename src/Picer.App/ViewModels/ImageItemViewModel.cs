using System.IO;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.App.ViewModels;

public partial class ImageItemViewModel : ObservableObject
{
    private static readonly SemaphoreSlim ThumbnailLoadGate = new(8, 8);
    private readonly ImageRecord _record;
    private string? _previewPath;
    private readonly Func<VideoRecord, CancellationToken, Task<string?>>? _videoPreviewLoader;
    private BitmapImage? _thumbnail;
    private bool _isLoadingThumbnail;
    private CancellationTokenSource? _thumbnailCts;
    private readonly CancellationToken _pageCancellation;

    public ImageItemViewModel(ImageRecord record, CancellationToken pageCancellation = default)
    {
        _record = record;
        _pageCancellation = pageCancellation;
    }

    public ImageItemViewModel(VideoRecord video, string? previewPath,
        Func<VideoRecord, CancellationToken, Task<string?>>? videoPreviewLoader = null,
        CancellationToken pageCancellation = default)
    {
        VideoRecord = video;
        _previewPath = previewPath;
        _videoPreviewLoader = videoPreviewLoader;
        _pageCancellation = pageCancellation;
        _record = new ImageRecord
        {
            Id = -video.Id, SourceId = video.SourceId, Path = video.Path, FileSize = video.FileSize,
            ModifiedUtc = video.ModifiedUtc, TakenUtc = video.TakenUtc, Width = video.Width, Height = video.Height,
            Gps = video.Gps, Status = video.Status == "active" ? ImageStatus.Active : ImageStatus.Missing
        };
    }

    public long Id => _record.Id;
    public ImageRecord Record => _record;
    public VideoRecord? VideoRecord { get; }
    public bool IsVideo => VideoRecord is not null;
    public string MediaBadge => IsVideo ? "▶ 视频" : "图片";
    public string Path => _record.Path;
    public string FileName => System.IO.Path.GetFileName(_record.Path);
    public bool IsMissing => _record.Status == ImageStatus.Missing;
    public bool IsTimelineAnchor => false;
    public ObservableCollection<ImageItemViewModel> BurstMembers { get; } = [];
    public bool IsBurstStack => BurstMembers.Count > 1;
    public int BurstCount => Math.Max(1, BurstMembers.Count);
    public string BurstBadge => IsBurstStack ? $"连拍 · {BurstCount} 张" : "";
    public string BurstToggleText => IsBurstExpanded ? "−" : "+";
    public string BurstTimeRange { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BurstToggleText))]
    private bool _isBurstExpanded;

    public void ConfigureBurst(IReadOnlyList<ImageRecord> records, DateTime startedUtc, DateTime endedUtc)
    {
        BurstMembers.Clear();
        foreach (var record in records) BurstMembers.Add(new ImageItemViewModel(record, _pageCancellation));
        BurstTimeRange = $"{startedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} – {endedUtc.ToLocalTime():HH:mm:ss}";
        OnPropertyChanged(nameof(IsBurstStack));
        OnPropertyChanged(nameof(BurstCount));
        OnPropertyChanged(nameof(BurstBadge));
        OnPropertyChanged(nameof(BurstTimeRange));
    }

    [RelayCommand]
    private void ToggleBurst() => IsBurstExpanded = !IsBurstExpanded;
    public DateTime EffectiveTime => (_record.TakenUtc ?? _record.CreatedUtc ?? _record.ModifiedUtc).ToLocalTime();

    [ObservableProperty]
    private string _timelineAnchor = "";

    public void SetTimelineGranularity(int granularity)
    {
        var time = EffectiveTime;
        TimelineAnchor = granularity switch
        {
            1 => $"{ISOWeek.GetYear(time)} 年 第 {ISOWeek.GetWeekOfYear(time):00} 周",
            2 => time.ToString("yyyy 年 MM 月"),
            3 => time.ToString("yyyy 年"),
            _ => time.ToString("yyyy 年 MM 月 dd 日")
        };
    }

    public string InfoText
    {
        get
        {
            var parts = new List<string>();
            if (_record.Width > 0 && _record.Height > 0)
                parts.Add($"{_record.Width}x{_record.Height}");
            if (_record.FileSize > 0)
                parts.Add(FormatSize(_record.FileSize));
            if (_record.TakenUtc is not null)
                parts.Add(_record.TakenUtc.Value.ToLocalTime().ToString("yyyy-MM-dd"));
            if (VideoRecord?.DurationSeconds is double duration)
                parts.Add(TimeSpan.FromSeconds(duration).ToString(duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss"));
            return string.Join("  ", parts);
        }
    }

    public BitmapImage? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_isLoadingThumbnail) LoadThumbnailAsync();
            return _thumbnail;
        }
    }

    public void EnsureThumbnailLoad()
    {
        if (_thumbnail is null && !_isLoadingThumbnail) LoadThumbnailAsync();
    }

    public void SetVideoPreviewPath(string path)
    {
        if (!IsVideo || string.IsNullOrWhiteSpace(path)) return;
        _previewPath = path;
        _thumbnail = null;
        _isLoadingThumbnail = false;
        OnPropertyChanged(nameof(Thumbnail));
    }

    private async void LoadThumbnailAsync()
    {
        if (_isLoadingThumbnail) return;
        _isLoadingThumbnail = true;
        _thumbnailCts?.Dispose();
        var cts = _thumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCancellation);
        var acquired = false;

        try
        {
            await ThumbnailLoadGate.WaitAsync(cts.Token);
            acquired = true;
            var imagePaths = await Task.Run(GetImagePaths, cts.Token);
            if (imagePaths.Count == 0 && VideoRecord is { } video && _videoPreviewLoader is not null)
            {
                _previewPath = await _videoPreviewLoader(video, cts.Token);
                imagePaths = await Task.Run(GetImagePaths, cts.Token);
            }
            if (imagePaths.Count == 0 && !IsVideo && AppServices.ServiceMode)
            {
                await using var remote = await AppServices.Gateway.OpenThumbnailAsync(_record.Id, cts.Token);
                if (remote is not null)
                {
                    using var buffer = new MemoryStream();
                    await remote.CopyToAsync(buffer, cts.Token);
                    buffer.Position = 0;
                    var remoteImage = new BitmapImage();
                    remoteImage.BeginInit();
                    remoteImage.CacheOption = BitmapCacheOption.OnLoad;
                    remoteImage.StreamSource = buffer;
                    remoteImage.EndInit();
                    remoteImage.Freeze();
                    if (!cts.IsCancellationRequested)
                    {
                        _thumbnail = remoteImage;
                        OnPropertyChanged(nameof(Thumbnail));
                    }
                }
                return;
            }
            if (imagePaths.Count == 0)
                return;
            var bi = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                foreach (var imagePath in imagePaths)
                {
                    try
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.DecodePixelWidth = 256;
                        image.UriSource = new Uri(imagePath, UriKind.Absolute);
                        image.EndInit();
                        image.Freeze();
                        return image;
                    }
                    catch { }
                }
                return null;
            }, cts.Token);

            if (bi is not null && !cts.IsCancellationRequested)
            {
                _thumbnail = bi;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            if (acquired) ThumbnailLoadGate.Release();
            if (ReferenceEquals(_thumbnailCts, cts))
            {
                _isLoadingThumbnail = false;
                if (_thumbnail is null && cts.IsCancellationRequested && !_pageCancellation.IsCancellationRequested)
                    OnPropertyChanged(nameof(Thumbnail));
            }
        }
    }

    public void CancelThumbnailLoad() => _thumbnailCts?.Cancel();

    private IReadOnlyList<string> GetImagePaths()
    {
        var paths = new List<string>(2);
        if (IsVideo && !string.IsNullOrWhiteSpace(_previewPath) && File.Exists(_previewPath)) paths.Add(_previewPath);
        if (!string.IsNullOrWhiteSpace(_record.ThumbPath) && File.Exists(_record.ThumbPath))
            paths.Add(_record.ThumbPath);
        if (!IsVideo && File.Exists(_record.Path) && !paths.Contains(_record.Path, StringComparer.OrdinalIgnoreCase))
            paths.Add(_record.Path);
        return paths;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }
}
