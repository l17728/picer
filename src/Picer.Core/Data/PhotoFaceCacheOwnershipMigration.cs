using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace Picer.Core.Data;

/// <summary>Geometry may be shared by content; human identity decisions may only be restored inside their owner.</summary>
internal static class PhotoFaceCacheOwnershipMigration
{
    private static readonly string[] Tables=["face_person_assignments","face_person_rejections","face_review_states","face_corrections"];
    private const string Owner="(SELECT owner_key FROM sync_image_owners WHERE image_id=i.id)";
    private const string NewOwner="(SELECT owner_key FROM sync_image_owners WHERE image_id=new.id)";

    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        var triggers=new List<(string Name,string Sql)>();
        using(var read=connection.CreateCommand())
        {
            read.Transaction=transaction;
            read.CommandText="SELECT name,sql FROM sqlite_master WHERE type='trigger' AND (name LIKE 'content_capture_%' OR name LIKE 'content_remove_%' OR name IN ('content_restore_image','content_restore_image_update'))";
            using var rows=read.ExecuteReader();while(rows.Read()) triggers.Add((rows.GetString(0),rows.GetString(1)));
        }
        foreach(var (name,original) in triggers)
        {
            var sql=original;
            foreach(var table in Tables)
            {
                if(name=="content_capture_"+table+"_insert" || name=="content_capture_"+table+"_update" || name=="content_remove_"+table)
                {
                    if(sql.Contains("sync_image_owners",StringComparison.Ordinal)) break;
                    var extra=table=="face_person_rejections" ? ","+(name.StartsWith("content_remove_",StringComparison.Ordinal)?"old":"r")+".person_id" : "";
                    var key="json_array(f.model_version,f.ordinal"+extra+")";
                    sql=sql.Replace(key,"json_array(f.model_version,f.ordinal"+extra+","+Owner+")",StringComparison.Ordinal);
                }
                else if(name is "content_restore_image" or "content_restore_image_update")
                {
                    var predicate="d.kind='"+table+"'";
                    var owned=predicate+" AND json_extract(d.item_key,'$[#-1]')="+NewOwner+" AND "+NewOwner+" NOT LIKE 'ambiguous-source:%'";
                    if(!sql.Contains(owned,StringComparison.Ordinal)) sql=sql.Replace(predicate,owned,StringComparison.Ordinal);
                }
            }
            if(sql==original) continue;
            // Trigger names come from the fixed comparisons above, never user input.
            Execute("DROP TRIGGER \""+name+"\";"+sql);
        }

        // Rebuild scoped cache entries from the current, attributable reference rows.
        // Older unscoped cache entries are retained as evidence but never auto-restored.
        foreach(var table in Tables)
        {
            string sql;
            using(var read=connection.CreateCommand())
            {
                read.Transaction=transaction;read.CommandText="SELECT sql FROM sqlite_master WHERE type='trigger' AND name=$name";
                read.Parameters.AddWithValue("$name","content_capture_"+table+"_insert");
                sql=read.ExecuteScalar() as string??"";
            }
            if(sql.Length==0) continue;
            var start=sql.IndexOf(" BEGIN ",StringComparison.Ordinal);
            if(start<0 || !sql.Contains("sync_image_owners",StringComparison.Ordinal))
                throw new InvalidDataException("photo_face_cache_trigger_shape_invalid");
            var body=sql[(start+7)..];
            body=Regex.Replace(body,@" AND r\.\w+=new\.\w+(?: AND r\.\w+=new\.\w+)*","",RegexOptions.CultureInvariant);
            body=Regex.Replace(body,@";\s*END\s*;?$",";",RegexOptions.CultureInvariant);
            Execute(body);
        }
        Execute("""
            DROP TRIGGER IF EXISTS content_owned_face_cache_purge;
            PRAGMA user_version=73;
            """);
        transaction.Commit();

        void Execute(string sql)
        {
            using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;command.ExecuteNonQuery();
        }
    }
}
