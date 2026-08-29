namespace Picer.Core.FileOperations;

public sealed record AuthorizedFileSelection(
    long ImageId,
    string Path,
    long ExpectedSize,
    DateTime ExpectedModifiedUtc,
    string ExpectedSha256);

public sealed class DeletionPlan
{
    public Guid PlanId { get; }
    public DateTime CreatedUtc { get; }
    public IReadOnlyList<AuthorizedFileSelection> Items { get; }
    public int FileCount => Items.Count;
    public long TotalBytes => Items.Sum(item => item.ExpectedSize);

    public DeletionPlan(Guid planId, DateTime createdUtc, IEnumerable<AuthorizedFileSelection> items)
    {
        PlanId = planId;
        CreatedUtc = createdUtc;
        Items = Array.AsReadOnly(items.ToArray());
    }
}

public enum FileOperationFailure
{
    None,
    FileNotFound,
    FileChanged,
    HashMismatch,
    AccessDenied,
    Cancelled,
    Unknown
}

public sealed record FileOperationItemResult(
    AuthorizedFileSelection Selection,
    bool Success,
    FileOperationFailure Failure,
    string? ErrorMessage = null);

public sealed record FileOperationResult(
    Guid PlanId,
    IReadOnlyList<FileOperationItemResult> Items)
{
    public int SucceededCount => Items.Count(item => item.Success);
    public int FailedCount => Items.Count - SucceededCount;
    public long SucceededBytes => Items.Where(item => item.Success).Sum(item => item.Selection.ExpectedSize);
}

public interface IUserAuthorizedFileOperation
{
    Task<FileOperationResult> MoveToRecycleBinAsync(
        DeletionPlan plan,
        CancellationToken cancellationToken = default);
}
