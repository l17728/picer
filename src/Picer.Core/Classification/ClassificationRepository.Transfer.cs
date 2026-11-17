using System.Globalization;
using Picer.Core.Identity;

namespace Picer.Core.Classification;

public sealed partial class ClassificationRepository
{
    public static string DefinitionFingerprint(ClassificationTaxonomySnapshot snapshot) => Digest(Encode(snapshot.Labels
        .Where(l => l.Assignable && l.Active).OrderBy(l => l.Id, StringComparer.Ordinal)
        .Select(l => new { l.Id, l.Dimension, l.Definition, l.Exclusions })));
    public IReadOnlyList<ClassificationTransfer> ExportResults(string publisher, string epoch, SpaceUploadScope scope)
    {
        if (!scope.Photos) return [];
        var config = Configuration(); if (config is null) return [];
        using var read = database.BeginReadTransaction(); _transaction = read;
        try
        {
            var result = new List<ClassificationTransfer>(); var taxonomy = DefinitionFingerprint(Taxonomy());
            foreach (var context in Contexts(config).Where(c => scope.SourceIds.Contains(c.Image.SourceId)))
            {
                if (!context.Input.MediaIdentity.StartsWith("sha256:", StringComparison.Ordinal)
                    || !scope.People && context.Input.People.Count > 0) continue;
                var fingerprint = ClassificationInputFingerprint.Compute(context.Input);
                var payload = Scalar("SELECT payload FROM classification_v2_results WHERE image_id=$id AND fingerprint=$fingerprint", ("$id", context.Image.Id), ("$fingerprint", fingerprint));
                if (payload is null) continue;
                var global = Scalar("SELECT global_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='image' AND local_key=CAST($id AS TEXT)", ("$publisher", publisher), ("$id", context.Image.Id));
                if (global is null) continue;
                var proof = PeopleProof(publisher, context.Image.Id, server: false);
                if (proof.Count != context.Input.People.Count) continue;
                var operation = Digest(Encode(new { publisher, epoch, global, fingerprint, scope, taxonomy, payload }))[..32];
                result.Add(new(operation, new(publisher, epoch, scope, "image", global), config, taxonomy, context.Input,
                    Decode<ClassificationResult>(payload), proof));
            }
            return result;
        }
        finally { _transaction = null; }
    }

    public IReadOnlyList<string> PeopleProof(string publisher, long imageId, bool server)
    {
        var column = server ? "target_id" : "local_key";
        using var command = Sql($"""
            SELECT face.global_id,person.global_id,a.updated_utc FROM detected_faces f
            JOIN face_person_assignments a ON a.face_id=f.id AND a.state='confirmed'
            JOIN sync_entity_identities face ON face.kind='face' AND face.{column}=CAST(f.id AS TEXT) AND face.publisher_id=$publisher
            JOIN sync_entity_identities person ON person.kind='person' AND person.{column}=CAST(a.person_id AS TEXT) AND person.publisher_id=$publisher
            WHERE f.image_id=$image AND f.is_archived=0 ORDER BY face.global_id,person.global_id
            """, ("$publisher", publisher), ("$image", imageId));
        using var reader = command.ExecuteReader(); var proofs = new List<string>();
        while (reader.Read()) proofs.Add(Digest(Encode(new[] { reader.GetString(0), reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcTicks.ToString(CultureInfo.InvariantCulture) })));
        return proofs.Order(StringComparer.Ordinal).ToArray();
    }

    public ClassificationTransferReceipt? TransferReceipt(string operationId) =>
        Scalar("SELECT result FROM classification_v2_changes WHERE operation_id=$id", ("$id", operationId)) is { } payload
            ? Decode<ClassificationTransferReceipt>(payload) : null;

    // The authenticated receiver resolves the reference under its publisher scope before entering this transaction.
    public ClassificationTransferReceipt ImportResult(long imageId, ClassificationTransfer transfer) => Write(() =>
    {
        ValidateRequestId(transfer.OperationId);
        var hash = Digest(Encode(transfer));
        var old = Scalar("SELECT payload_hash FROM classification_v2_changes WHERE operation_id=$id", ("$id", transfer.OperationId));
        if (old is not null)
        {
            if (old != hash) throw new InvalidOperationException("classification_operation_conflict");
            return TransferReceipt(transfer.OperationId)!;
        }
        ValidateConfiguration(transfer.Configuration);
        var taxonomy = Taxonomy();
        if (DefinitionFingerprint(taxonomy) != transfer.TaxonomyFingerprint) throw new InvalidOperationException("classification_taxonomy_changed");
        var configured = Configuration() ?? throw new InvalidOperationException("classification_configuration_required");
        var current = Contexts(configured, imageId).SingleOrDefault() ?? throw new InvalidOperationException("classification_input_changed");
        if (current.Input.OwnerId != "publisher:" + transfer.Reference.PublisherId
            || !transfer.Input.MediaIdentity.StartsWith("sha256:", StringComparison.Ordinal)
            || current.Input.MediaIdentity != transfer.Input.MediaIdentity)
            throw new InvalidOperationException("classification_owner_changed");
        var proof = PeopleProof(transfer.Reference.PublisherId, imageId, server: true);
        if (proof.Count != current.Input.People.Count || proof.Count != transfer.Input.People.Count
            || !proof.SequenceEqual(transfer.PeopleProof.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("classification_people_changed");
        var rebased = transfer.Input with { SpaceEpoch = current.Input.SpaceEpoch, OwnerId = current.Input.OwnerId,
            People = current.Input.People, Definitions = current.Input.Definitions, DiscoveryRevision = current.Input.DiscoveryRevision };
        if (ClassificationInputFingerprint.Compute(rebased) != ClassificationInputFingerprint.Compute(current.Input)
            || ClassificationInputFingerprint.Compute(transfer.Input) != transfer.Result.InputFingerprint)
            throw new InvalidOperationException("classification_input_changed");
        var fingerprint = ClassificationInputFingerprint.Compute(current.Input);
        var result = transfer.Result with { InputFingerprint = fingerprint };
        ClassificationOutputValidator.Validate(result, current, taxonomy);
        if (Scalar("SELECT 1 FROM classification_v2_items WHERE image_id=$id AND state='leased'", ("$id", imageId)) is not null)
            throw new InvalidOperationException("classification_result_busy");
        var existing = Scalar("SELECT payload FROM classification_v2_results WHERE image_id=$id AND fingerprint=$fingerprint", ("$id", imageId), ("$fingerprint", fingerprint));
        if (existing is not null && existing != Encode(result)) throw new InvalidOperationException("classification_result_conflict");
        StoreResult(imageId, fingerprint, current, result);
        var receipt = new ClassificationTransferReceipt(transfer.OperationId, imageId, fingerprint);
        Execute("INSERT INTO classification_v2_changes VALUES($id,$hash,$result)", ("$id", transfer.OperationId), ("$hash", hash), ("$result", Encode(receipt)));
        return receipt;
    });
}
