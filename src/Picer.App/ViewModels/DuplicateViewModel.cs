using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core.Data;
using Picer.Core.FileOperations;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.App.ViewModels;

public enum DuplicateReviewDecision { Undecided, Keep, Delete, Defer }

public partial class DuplicateViewModel : ObservableObject
{
    private readonly PicerDatabase _db;
    private readonly IndexEngine _engine;
    private readonly IUserAuthorizedFileOperation _fileOperation;
    public ObservableCollection<ImageGroupItemViewModel> Groups { get; } = new();

    [ObservableProperty] private ImageGroupItemViewModel? _selectedGroup;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindDuplicatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewDeletionPlanCommand))]
    private bool _isBusy;
    [ObservableProperty] private string _statusText = "就绪。识别和建议不会修改任何文件。";
    [ObservableProperty] private int _similarityThreshold = 85;

    public DuplicateViewModel(PicerDatabase db, IndexEngine engine, IUserAuthorizedFileOperation fileOperation)
    {
        _db = db;
        _engine = engine;
        _fileOperation = fileOperation;
    }

    [RelayCommand(CanExecute = nameof(CanFindDuplicates))]
    private async Task FindDuplicatesAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = "正在检测完全重复与视觉相似图片...";
            var ah = Math.Max(1, (int)((100 - SimilarityThreshold) * 64.0 / 100));
            var dh = Math.Max(1, (int)((100 - SimilarityThreshold) * 56.0 / 100));
            var groups = await Task.Run(() => DuplicateDetector.FindReviewGroups(_db, aHashThreshold: ah, dHashThreshold: dh));
            Groups.Clear();
            foreach (var group in groups) Groups.Add(new(group, OnDecisionChanged));
            SelectedGroup = Groups.FirstOrDefault();
            var exact = Groups.Count(group => group.Group.Kind == DuplicateGroupKind.ExactDuplicate);
            StatusText = $"检测到 {exact} 组完全重复、{Groups.Count - exact} 组视觉相似图片。当前未更改任何文件。";
        }
        finally { IsBusy = false; }
    }
    private bool CanFindDuplicates() => !IsBusy;

    [RelayCommand] private void RecommendLargest() => Recommend(items => items.MaxBy(x => x.FileSize)!, "最大文件");
    [RelayCommand] private void RecommendNewest() => Recommend(items => items.MaxBy(x => x.TakenUtc ?? x.ModifiedUtc)!, "最新拍摄");
    [RelayCommand] private void RecommendOldest() => Recommend(items => items.MinBy(x => x.TakenUtc ?? x.ModifiedUtc)!, "最早拍摄");
    [RelayCommand] private void RecommendHighestResolution() => Recommend(items => items.MaxBy(x => (long)x.Width * x.Height)!, "最高分辨率");

    private void Recommend(Func<IReadOnlyCollection<DuplicateCandidateItemViewModel>, DuplicateCandidateItemViewModel> choose, string reason)
    {
        foreach (var group in Groups)
        {
            var selected = choose(group.Images);
            foreach (var item in group.Images) item.SetRecommendation(item == selected, reason);
        }
        StatusText = $"已生成“{reason}”保留建议。建议不会改变用户决定或修改文件。";
    }

    [RelayCommand(CanExecute = nameof(CanReviewDeletionPlan))]
    private async Task ReviewDeletionPlanAsync()
    {
        var error = ValidateDecisions();
        if (error is not null)
        {
            MessageBox.Show(error, "无法生成删除方案", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pending = Groups.SelectMany(g => g.Images).Where(x => x.Decision == DuplicateReviewDecision.Delete).ToList();
        if (pending.Any(x => string.IsNullOrWhiteSpace(x.Record.Sha256)))
        {
            MessageBox.Show("部分文件缺少内容哈希。请先重建索引，当前未执行任何文件操作。", "无法安全删除",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var plan = new DeletionPlan(Guid.NewGuid(), DateTime.UtcNow, pending.Select(x => new AuthorizedFileSelection(
            x.Id, x.Path, x.FileSize, x.ModifiedUtc, x.Record.Sha256!)).ToArray());
        var paths = string.Join(Environment.NewLine, plan.Items.Take(8).Select(x => x.Path));
        var more = plan.FileCount > 8 ? $"{Environment.NewLine}……另有 {plan.FileCount - 8} 个文件" : "";
        var prompt = $"将把 {plan.FileCount} 个文件移到 Windows 回收站。{Environment.NewLine}" +
                     $"预计容量：{FormatSize(plan.TotalBytes)}{Environment.NewLine}{Environment.NewLine}{paths}{more}";
        if (MessageBox.Show(prompt, "确认删除方案", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            StatusText = "已取消删除方案，未更改任何文件。";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _fileOperation.MoveToRecycleBinAsync(plan);
            foreach (var item in result.Items.Where(x => x.Success))
            {
                await _engine.RemoveIndexRecordAsync(item.Selection.ImageId);
                RemoveCandidate(item.Selection.ImageId);
            }
            StatusText = result.FailedCount == 0
                ? $"已将 {result.SucceededCount} 个文件移到回收站，实际处理 {FormatSize(result.SucceededBytes)}。"
                : $"完成：成功 {result.SucceededCount}，失败 {result.FailedCount}；失败项仍保留在索引中。";
            if (result.FailedCount > 0)
            {
                var details = string.Join(Environment.NewLine, result.Items.Where(x => !x.Success).Take(5)
                    .Select(x => $"{x.Selection.Path}：{x.ErrorMessage ?? x.Failure.ToString()}"));
                MessageBox.Show(details, "部分文件未处理", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { IsBusy = false; }
    }

    private bool CanReviewDeletionPlan() => !IsBusy && Groups.SelectMany(g => g.Images)
        .Any(x => x.Decision == DuplicateReviewDecision.Delete);

    private string? ValidateDecisions()
    {
        foreach (var group in Groups.Where(g => g.Images.Any(x => x.Decision == DuplicateReviewDecision.Delete)))
            if (!group.Images.Any(x => x.Decision == DuplicateReviewDecision.Keep))
                return $"组“{Path.GetFileName(group.RepresentativePath)}”有待删除项，但尚未明确选择至少一个保留项。";
        return null;
    }

    private void RemoveCandidate(long id)
    {
        foreach (var group in Groups.ToList())
        {
            var item = group.Images.FirstOrDefault(x => x.Id == id);
            if (item is not null) group.Images.Remove(item);
            if (group.Images.Count < 2) Groups.Remove(group);
        }
    }

    private void OnDecisionChanged()
    {
        ReviewDeletionPlanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DecisionSummary));
    }

    public string DecisionSummary
    {
        get
        {
            var all = Groups.SelectMany(g => g.Images).ToList();
            return $"保留 {all.Count(x => x.Decision == DuplicateReviewDecision.Keep)} · " +
                   $"删除 {all.Count(x => x.Decision == DuplicateReviewDecision.Delete)} · " +
                   $"未处理 {all.Count(x => x.Decision is DuplicateReviewDecision.Defer or DuplicateReviewDecision.Undecided)}";
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }
}

public sealed class ImageGroupItemViewModel
{
    public ImageGroup Group { get; }
    public ObservableCollection<DuplicateCandidateItemViewModel> Images { get; }
    public string RepresentativePath => Group.Representative.Path;
    public string InfoText => $"{Images.Count} 张{(Group.Kind == DuplicateGroupKind.ExactDuplicate ? "完全重复" : "视觉相似")}图片";
    public string AlgorithmText => Group.Algorithm;
    public ImageGroupItemViewModel(ImageGroup group, Action changed)
    {
        Group = group;
        Images = new(group.Images.Select(x => new DuplicateCandidateItemViewModel(x, group.Algorithm, group.SimilarityPercent(x), changed)));
    }
}

public partial class DuplicateCandidateItemViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly ImageItemViewModel _image;
    public ImageRecord Record { get; }
    public long Id => Record.Id;
    public string Path => Record.Path;
    public string FileName => _image.FileName;
    public string InfoText => _image.InfoText;
    public long FileSize => Record.FileSize;
    public int Width => Record.Width;
    public int Height => Record.Height;
    public DateTime ModifiedUtc => Record.ModifiedUtc;
    public DateTime? TakenUtc => Record.TakenUtc;
    public object? Thumbnail => _image.Thumbnail;
    public string MatchText { get; }
    [ObservableProperty] private DuplicateReviewDecision _decision;
    [ObservableProperty] private bool _isRecommendedKeep;
    [ObservableProperty] private string? _recommendationReason;
    public string DecisionText => Decision switch
    {
        DuplicateReviewDecision.Keep => "保留",
        DuplicateReviewDecision.Delete => "移到回收站",
        DuplicateReviewDecision.Defer => "暂不处理",
        _ => "未决定"
    };
    public string RecommendationText => IsRecommendedKeep ? $"建议保留：{RecommendationReason}" : "";
    public DuplicateCandidateItemViewModel(ImageRecord record, string algorithm, int similarityPercent, Action changed)
    {
        Record = record;
        _image = new(record);
        _changed = changed;
        MatchText = $"{algorithm} · {similarityPercent}%";
    }
    [RelayCommand] private void MarkKeep() => Decision = DuplicateReviewDecision.Keep;
    [RelayCommand] private void MarkDelete() => Decision = DuplicateReviewDecision.Delete;
    [RelayCommand] private void MarkDefer() => Decision = DuplicateReviewDecision.Defer;
    public void SetRecommendation(bool value, string reason)
    {
        IsRecommendedKeep = value;
        RecommendationReason = value ? reason : null;
        OnPropertyChanged(nameof(RecommendationText));
    }
    partial void OnDecisionChanged(DuplicateReviewDecision value)
    {
        OnPropertyChanged(nameof(DecisionText));
        _changed();
    }
}
