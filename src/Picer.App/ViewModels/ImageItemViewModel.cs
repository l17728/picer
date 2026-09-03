using System.IO;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core.Models;

namespace Picer.App.ViewModels;

public partial class ImageItemViewModel : ObservableObject
{
    private static readonly SemaphoreSlim ThumbnailLoadGate = new(8, 8);
    private readonly ImageRecord _record;
    private BitmapImage? _thumbnail;
    private bool _isLoadingThumbnail;
    private CancellationTokenSource? _thumbnailCts;
    private readonly CancellationToken _pageCancellation;

    public ImageItemViewModel(ImageRecord record, CancellationToken pageCancellation = default)
    {
        _record = record;
        _pageCancellation = pageCancellation;
    }

    public long Id => _record.Id;
    public ImageRecord Record => _record;
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
            return string.Join("  ", parts);
        }
    }

    public BitmapImage? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_isLoadingThumbnail && File.Exists(GetImagePath()))
            {
                LoadThumbnailAsync();
            }
            return _thumbnail;
        }
    }

    public void EnsureThumbnailLoad()
    {
        if (_thumbnail is null && !_isLoadingThumbnail) LoadThumbnailAsync();
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
            var imagePaths = GetImagePaths();
            if (imagePaths.Count == 0)
                return;
            await ThumbnailLoadGate.WaitAsync(cts.Token);
            acquired = true;
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

    private string? GetImagePath() => GetImagePaths().FirstOrDefault();

    private IReadOnlyList<string> GetImagePaths()
    {
        var paths = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(_record.ThumbPath) && File.Exists(_record.ThumbPath))
            paths.Add(_record.ThumbPath);
        if (File.Exists(_record.Path) && !paths.Contains(_record.Path, StringComparer.OrdinalIgnoreCase))
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
