using Picer.Core.Hashing;

namespace Picer.Core.FileOperations;

public interface IRecycleBinGateway
{
    void MoveToRecycleBin(string path);
}

/// <summary>Validates an immutable user-approved snapshot immediately before crossing the recycle-bin boundary.</summary>
public sealed class AuthorizedFileOperationExecutor(IRecycleBinGateway gateway) : IUserAuthorizedFileOperation
{
    public Task<FileOperationResult> MoveToRecycleBinAsync(DeletionPlan plan, CancellationToken cancellationToken = default) =>
        Task.Run(() => Execute(plan, cancellationToken), cancellationToken);

    private FileOperationResult Execute(DeletionPlan plan, CancellationToken cancellationToken)
    {
        var results = new List<FileOperationItemResult>(plan.Items.Count);
        foreach (var selection in plan.Items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new(selection, false, FileOperationFailure.Cancelled, "操作已取消"));
                continue;
            }
            results.Add(ExecuteOne(selection));
        }
        return new FileOperationResult(plan.PlanId, results);
    }

    private FileOperationItemResult ExecuteOne(AuthorizedFileSelection selection)
    {
        try
        {
            var file = new FileInfo(selection.Path);
            if (!file.Exists) return new(selection, false, FileOperationFailure.FileNotFound, "文件已不存在");
            if (file.Length != selection.ExpectedSize || file.LastWriteTimeUtc != selection.ExpectedModifiedUtc)
                return new(selection, false, FileOperationFailure.FileChanged, "确认后文件已发生变化");
            var hash = FileHasher.ComputeSha256(selection.Path);
            if (!string.Equals(hash, selection.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                return new(selection, false, FileOperationFailure.HashMismatch, "文件内容与确认时不一致");
            gateway.MoveToRecycleBin(selection.Path);
            return File.Exists(selection.Path)
                ? new(selection, false, FileOperationFailure.Unknown, "系统未确认文件已移入回收站")
                : new(selection, true, FileOperationFailure.None);
        }
        catch (UnauthorizedAccessException ex) { return new(selection, false, FileOperationFailure.AccessDenied, ex.Message); }
        catch (OperationCanceledException ex) { return new(selection, false, FileOperationFailure.Cancelled, ex.Message); }
        catch (Exception ex) { return new(selection, false, FileOperationFailure.Unknown, ex.Message); }
    }
}
