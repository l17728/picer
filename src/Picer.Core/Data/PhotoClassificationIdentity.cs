using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public string GetLocalPhotoContentIdentity(long imageId,SqliteTransaction? transaction=null)
    {
        using var query=Command("SELECT 'local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||version_id FROM photo_metadata_file_versions WHERE image_id=$id");
        if(transaction is not null) query.Transaction=transaction;
        query.Parameters.AddWithValue("$id",imageId);
        return query.ExecuteScalar() as string??throw new ArgumentException("photo_reference_missing");
    }

    public string GetPhotoClassificationContentIdentity(long imageId,SqliteTransaction? transaction=null)
    {
        using var query=Command("""
            SELECT CASE WHEN length(i.sha256)=64 AND lower(i.sha256) NOT GLOB '*[^0-9a-f]*'
                THEN 'sha256:'||lower(i.sha256)
                ELSE 'local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||v.version_id END
            FROM images i JOIN photo_metadata_file_versions v ON v.image_id=i.id WHERE i.id=$id
            """);
        if(transaction is not null) query.Transaction=transaction;
        query.Parameters.AddWithValue("$id",imageId);
        return query.ExecuteScalar() as string??throw new ArgumentException("photo_reference_missing");
    }

    public string BindPhotoClassificationOperation(string operationId,long imageId,SqliteTransaction transaction)
    {
        if(!Guid.TryParseExact(operationId,"N",out _)) throw new ArgumentException("invalid_photo_operation_id");
        var identity=GetPhotoClassificationContentIdentity(imageId,transaction);
        using var owner=Command("SELECT owner_key FROM sync_image_owners WHERE image_id=$id");
        owner.Transaction=transaction;owner.Parameters.AddWithValue("$id",imageId);
        var ownerKey=owner.ExecuteScalar() as string;
        if(ownerKey is null || ownerKey.StartsWith("ambiguous-source:",StringComparison.Ordinal))
            throw new InvalidOperationException("photo_owner_unresolved");
        using(var observed=Command("SELECT content_identity,owner_key,reference_id FROM photo_metadata_operation_bindings WHERE operation_id=$operation"))
        {
            observed.Transaction=transaction;observed.Parameters.AddWithValue("$operation",operationId);
            using var row=observed.ExecuteReader();
            if(row.Read())
            {
                if(row.GetString(1)!=ownerKey || row.GetInt64(2)!=imageId && row.GetString(0)!=identity)
                    throw new InvalidDataException("photo_operation_reference_changed");
                return row.GetString(0);
            }
        }
        using var insert=Command("INSERT INTO photo_metadata_operation_bindings VALUES($operation,$reference,$owner,$identity)");
        insert.Transaction=transaction;insert.Parameters.AddWithValue("$operation",operationId);
        insert.Parameters.AddWithValue("$reference",imageId);insert.Parameters.AddWithValue("$owner",ownerKey);
        insert.Parameters.AddWithValue("$identity",identity);insert.ExecuteNonQuery();return identity;
    }

    public long? ResolvePhotoClassificationOperation(string operationId,SqliteTransaction? transaction=null)
    {
        using var query=Command("""
            SELECT MIN(i.id) FROM photo_metadata_operation_bindings b
            JOIN images i JOIN sync_image_owners o ON o.image_id=i.id
            JOIN photo_metadata_file_versions v ON v.image_id=i.id
            WHERE b.operation_id=$operation AND o.owner_key=b.owner_key AND (
                (b.content_identity='sha256:'||lower(i.sha256) AND length(i.sha256)=64)
                OR (i.id=b.reference_id AND b.content_identity='local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||v.version_id))
            """);
        if(transaction is not null) query.Transaction=transaction;
        query.Parameters.AddWithValue("$operation",operationId);
        var value=query.ExecuteScalar();return value is null or DBNull?null:Convert.ToInt64(value);
    }
}
