using Picer.Core.Classification;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    public ClassificationTransferReceipt ImportClassification(ClassificationTransfer transfer)
    {
        if (transfer.Reference.Kind != "image" || !transfer.Reference.Scope.People && transfer.Input.People.Count > 0)
            throw new UnauthorizedAccessException();
        var reference = ResolvePublishedMediaCore(transfer.Reference, "index.write");
        return new ClassificationRepository(database).ImportResult(reference.Id, transfer);
    }

    public ClassificationTransferReceipt ReadClassificationReceipt(ClassificationTransferRead request)
    {
        var reference = ResolvePublishedMediaCore(request.Reference, "index.write");
        var receipt = new ClassificationRepository(database).TransferReceipt(request.OperationId);
        if (receipt is null || receipt.ImageId != reference.Id) throw new InvalidOperationException("classification_receipt_missing");
        return receipt;
    }
}
