using Microsoft.VisualBasic.FileIO;
using Picer.Core.FileOperations;

namespace Picer.App.FileOperations;

public sealed class WindowsRecycleBinFileOperation : IUserAuthorizedFileOperation
{
    private readonly AuthorizedFileOperationExecutor _executor = new(new WindowsRecycleBinGateway());

    public Task<FileOperationResult> MoveToRecycleBinAsync(
        DeletionPlan plan,
        CancellationToken cancellationToken = default) =>
        _executor.MoveToRecycleBinAsync(plan, cancellationToken);

    private sealed class WindowsRecycleBinGateway : IRecycleBinGateway
    {
        public void MoveToRecycleBin(string path) =>
            FileSystem.DeleteFile(
                path,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
    }
}
