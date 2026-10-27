using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed record CatalogRecreationContext(string PublisherId,IReadOnlyDictionary<string,string> GlobalByOperation);

public sealed partial class PicerDatabase
{
    private bool FollowsConfirmedCatalogRetirement(MetadataEdit edit,MetadataEdit? current,
        CatalogRecreationContext? context,SqliteTransaction transaction)
    {
        if(context is null || edit.ServerVersion!=0
            || !context.GlobalByOperation.TryGetValue(edit.OperationId,out var global)) return false;
        string kind;long catalog;
        if(edit.Kind is "tag-link" or "video-tag-link")
        {
            if(edit.Value!="1" || current is not null && current.Value!="0") return false;
            kind="tag";catalog=long.Parse(edit.Field,CultureInfo.InvariantCulture);
        }
        else if(edit.Kind=="album-link")
        {
            if(edit.Value!="1" || current is not null && current.Value!="0") return false;
            kind="album";catalog=edit.EntityId;
        }
        else if(edit.Kind is "face-state" or "video-face-state")
        {
            var proposed=ParseFaceManualState(edit.Value);
            if(proposed.PersonId is not { } person) return false;
            if(current is not null)
            {
                var predecessor=ParseFaceManualState(current.Value);
                if(predecessor.PersonId is not null || predecessor.State!="unassigned" || proposed.ModelVersion!=predecessor.ModelVersion) return false;
            }
            kind="person";catalog=person;
        }
        else return false;
        if(current is null)
        {
            if(edit.BaseVersion==0) return false; // Ordinary creation already has the correct baseline.
            using var generation=Command("""
                SELECT 1 FROM sync_active_entity_identities active
                JOIN sync_publishers p ON p.publisher_id=active.publisher_id AND p.state='receiving'
                JOIN sync_catalog_generations g ON g.publisher_id=active.publisher_id AND g.kind=active.kind AND g.global_id=active.global_id
                JOIN sync_retired_catalog_identities retired ON retired.publisher_id=g.publisher_id AND retired.kind=g.kind AND retired.global_id=g.previous_global_id
                WHERE active.publisher_id=$publisher AND active.kind=$kind AND active.global_id=$global AND active.target_id=$catalog
                """);
            generation.Transaction=transaction;generation.Parameters.AddWithValue("$publisher",context.PublisherId);
            generation.Parameters.AddWithValue("$kind",kind);generation.Parameters.AddWithValue("$global",global);
            generation.Parameters.AddWithValue("$catalog",catalog);return generation.ExecuteScalar() is not null;
        }
        if(current.ServerVersion<=0) return false;
        using var proof=Command("""
            SELECT 1 FROM sync_active_entity_identities active
            JOIN sync_publishers p ON p.publisher_id=active.publisher_id AND p.state='receiving'
            WHERE active.publisher_id=$publisher AND active.kind=$kind AND active.global_id=$global AND active.target_id=$catalog
                AND (EXISTS(SELECT 1 FROM sync_catalog_retirement_receipts receipt
                    JOIN sync_retired_catalog_identities retired ON retired.publisher_id=receipt.publisher_id
                        AND retired.kind=receipt.kind AND retired.global_id=receipt.global_id AND retired.operation_id=receipt.operation_id
                    WHERE receipt.publisher_id=$publisher AND receipt.kind=$kind AND receipt.global_id<>$global
                        AND receipt.state IN ('applied','delivered')
                        AND EXISTS(SELECT 1 FROM json_each(receipt.applied_operations) WHERE value=$predecessor))
                    OR EXISTS(SELECT 1 FROM sync_catalog_retirement_predecessors compacted
                        WHERE compacted.publisher_id=$publisher AND compacted.kind=$kind AND compacted.global_id<>$global
                            AND compacted.field_operation_id=$predecessor))
            LIMIT 1
            """);
        proof.Transaction=transaction;proof.Parameters.AddWithValue("$publisher",context.PublisherId);
        proof.Parameters.AddWithValue("$kind",kind);proof.Parameters.AddWithValue("$global",global);
        proof.Parameters.AddWithValue("$catalog",catalog);proof.Parameters.AddWithValue("$predecessor",current.OperationId);
        return proof.ExecuteScalar() is not null;
    }
}
