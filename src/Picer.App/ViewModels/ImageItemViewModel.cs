using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Picer.Core.Models;

namespace Picer.App.ViewModels;

public partial class ImageItemViewModel : ObservableObject
{
    private static readonly SemaphoreSlim ThumbnailLoadGate = new(8, 8);
    private readonly ImageRecord _record;
    private BitmapImage? _thumbnail;
    private bool _isLoadingThumbnail;
    private CancellationTokenSource? _thumbnailCts;

    public ImageItemViewModel(ImageRecord record)
    {
        _record = record;
    }

    public long Id => _record.Id;
    public ImageRecord Record => _record;
    public string Path => _record.Path;
    public string FileName => System.IO.Path.GetFileName(_record.Path);
    public bool IsMissing => _record.Status == ImageStatus.Missing;
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

    private async void LoadThumbnailAsync()
    {
        if (_isLoadingThumbnail) return;
        _isLoadingThumbnail = true;
        _thumbnailCts?.Dispose();
        var cts = _thumbnailCts = new CancellationTokenSource();
        var acquired = false;

        try
        {
            var thumbPath = GetImagePath();
            if (string.IsNullOrWhiteSpace(thumbPath))
                return;
            await ThumbnailLoadGate.WaitAsync(cts.Token);
            acquired = true;
            var bi = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 256;
                    image.UriSource = new Uri(thumbPath, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                catch
                {
                    return null;
                }
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
            _isLoadingThumbnail = false;
        }
    }

    public void CancelThumbnailLoad() => _thumbnailCts?.Cancel();

    private string? GetImagePath() =>
        !string.IsNullOrWhiteSpace(_record.ThumbPath) && File.Exists(_record.ThumbPath)
            ? _record.ThumbPath
            : File.Exists(_record.Path) ? _record.Path : null;

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
