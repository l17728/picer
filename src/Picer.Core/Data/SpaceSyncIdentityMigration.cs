using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class SpaceSyncIdentityMigration
{
    public static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE library_identity(id INTEGER PRIMARY KEY CHECK(id=1),library_id TEXT NOT NULL UNIQUE);
            INSERT INTO library_identity VALUES(1,lower(hex(randomblob(16))));
            CREATE TABLE sync_publishers(
                publisher_id TEXT PRIMARY KEY,library_id TEXT NOT NULL,incarnation_id TEXT NOT NULL,
                device_grant_id TEXT NOT NULL,space_id TEXT NOT NULL,state TEXT NOT NULL,
                last_sequence INTEGER NOT NULL DEFAULT 0,epoch TEXT NOT NULL,
                uploaded_media_revision INTEGER NOT NULL DEFAULT -1,uploaded_metadata_revision INTEGER NOT NULL DEFAULT -1,
                uploaded_scope_hash TEXT NOT NULL DEFAULT '',
                UNIQUE(library_id,incarnation_id));
            CREATE TABLE sync_entity_identities(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                kind TEXT NOT NULL,local_key TEXT NOT NULL,global_id TEXT NOT NULL UNIQUE,target_id INTEGER,
                PRIMARY KEY(publisher_id,kind,local_key));
            CREATE INDEX ix_sync_entity_target ON sync_entity_identities(kind,target_id);
            CREATE TABLE sync_operation_receipts(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                sequence INTEGER NOT NULL,operation_id TEXT NOT NULL UNIQUE,payload_hash TEXT NOT NULL,
                status INTEGER NOT NULL,result TEXT NOT NULL,
                PRIMARY KEY(publisher_id,sequence));
            CREATE TABLE sync_outbox(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                operation_id TEXT NOT NULL UNIQUE,kind TEXT NOT NULL,entity_key TEXT NOT NULL,payload TEXT NOT NULL);
            CREATE INDEX ix_sync_outbox_publisher ON sync_outbox(publisher_id,sequence);
            CREATE TABLE sync_exports(
                publisher_id TEXT PRIMARY KEY REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                snapshot_name TEXT NOT NULL,next_batch INTEGER NOT NULL DEFAULT 0,
                revision_cut INTEGER NOT NULL,metadata_cut INTEGER NOT NULL,created_utc TEXT NOT NULL,scope_hash TEXT NOT NULL DEFAULT '',include_metadata INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE sync_source_bindings(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                local_source_id INTEGER NOT NULL,source_id INTEGER NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
                source_uuid TEXT NOT NULL UNIQUE,original_path TEXT NOT NULL,agent_grant_id TEXT,
                PRIMARY KEY(publisher_id,local_source_id));
            CREATE INDEX ix_sync_source_route ON sync_source_bindings(source_id,agent_grant_id);
            CREATE TABLE sync_space_state(id INTEGER PRIMARY KEY CHECK(id=1),epoch TEXT NOT NULL,
                change_sequence INTEGER NOT NULL DEFAULT 0,purge_generation INTEGER NOT NULL DEFAULT 0,
                state TEXT NOT NULL DEFAULT 'active');
            INSERT INTO sync_space_state(id,epoch) VALUES(1,lower(hex(randomblob(16))));
            CREATE TABLE sync_purge_journal(id INTEGER PRIMARY KEY CHECK(id=1),operation_id TEXT NOT NULL,
                media_kind TEXT NOT NULL,media_id INTEGER NOT NULL,generation INTEGER NOT NULL);
            PRAGMA user_version=46;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
