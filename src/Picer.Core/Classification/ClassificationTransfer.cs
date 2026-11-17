using Picer.Core.Identity;

namespace Picer.Core.Classification;

public sealed record ClassificationTransfer(string OperationId, SpaceMediaReferenceRequest Reference,
    ClassificationConfiguration Configuration, string TaxonomyFingerprint,
    ClassificationInput Input, ClassificationResult Result, IReadOnlyList<string> PeopleProof);
public sealed record ClassificationTransferReceipt(string OperationId, long ImageId, string Fingerprint);
public sealed record ClassificationTransferRead(string OperationId, SpaceMediaReferenceRequest Reference);
