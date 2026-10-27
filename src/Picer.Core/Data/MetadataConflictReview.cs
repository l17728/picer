using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed record MetadataConflictResolution(string OperationId, bool UseProposed, long ExpectedVersion, string ExpectedOperationId, string? Epoch = null);
public sealed record MetadataConflictReview(MetadataEditConflict Conflict, string Title,string? ProposedDisplay=null,string? CurrentDisplay=null);
public sealed record MetadataConflictSnapshot(string Epoch, IReadOnlyList<MetadataConflictReview> Reviews);
public sealed record MetadataConflictReceipt(string OperationId, long Version);

public sealed partial class PicerDatabase
{
    public bool ApplyMetadataConflictReceipt(MetadataConflictReceipt receipt, SqliteTransaction transaction)
    {
        if (!Guid.TryParseExact(receipt.OperationId,"N",out _) || receipt.Version <= 0)
            throw new InvalidDataException("metadata_conflict_receipt_invalid");
        MetadataEdit proposed;
        using (var query = Command("SELECT proposed_json FROM metadata_edit_conflicts WHERE operation_id=$op AND state='pending'"))
        {
            query.Transaction=transaction; query.Parameters.AddWithValue("$op",receipt.OperationId);
            if (query.ExecuteScalar() is not string json) return false;
            proposed=System.Text.Json.JsonSerializer.Deserialize<MetadataEdit>(json)!;
        }
        var current=FindMetadataEdit(NormalizeCausalVideoEdit(proposed,transaction),transaction);
        // A status reply must never acknowledge data that has not arrived, or
        // discard a newer offline edit while that edit awaits acknowledgement.
        if (current is null || current.ServerVersion < receipt.Version) return false;
        using var update=Command("UPDATE metadata_edit_conflicts SET state='resolved',resolved_version=$version WHERE operation_id=$op AND state='pending'");
        update.Transaction=transaction; update.Parameters.AddWithValue("$op",receipt.OperationId);
        update.Parameters.AddWithValue("$version",receipt.Version);
        return update.ExecuteNonQuery()==1;
    }

    public MetadataConflictSnapshot GetMetadataConflictSnapshot()
    {
        using var transaction = BeginReadTransaction();
        using var epoch = Command("SELECT epoch FROM sync_space_state WHERE id=1 AND state='active'"); epoch.Transaction = transaction;
        var value = epoch.ExecuteScalar() as string ?? throw new InvalidOperationException("space_fenced");
        return new(value, GetMetadataEditConflicts(transaction)
            .Select(conflict => new MetadataConflictReview(conflict, DescribeMetadataEdit(conflict.Proposed,transaction),
                DescribeMetadataValue(conflict.Proposed,transaction),DescribeMetadataValue(conflict.Current,transaction))).ToArray());
    }

    public string? DescribeMetadataValue(MetadataEdit edit,SqliteTransaction? transaction=null)
    {
        if(edit.Kind=="scene-description" && TryReadSceneDescriptionText(edit.Value,out var scene))return scene!.SceneName+"\n"+scene.Summary;
        if(edit.Kind=="photo-classification-confirmation") return edit.Value=="1"?"确认分类":"撤回人工确认";
        if(edit.Kind=="photo-classification-policy") return edit.Value switch
        { "accepted"=>"接受机器分类","rejected"=>"不是此类","restored"=>"恢复为待确认","retracted"=>"撤销机器归类",_=>null };
        if(edit.Kind is "face-rejection" or "video-face-rejection") return ParseFacePersonRejection(edit.Value).Rejected?"不再推荐此人物":"允许推荐此人物";
        if(edit.Kind is not ("face-state" or "video-face-state")) return null;
        var state=ParseFaceManualState(edit.Value);
        if(state.State=="ignored") return "忽略此人脸";
        if(state.State=="unassigned") return "移回待分类";
        using var person=Command("SELECT display_name FROM people WHERE id=$id");person.Transaction=transaction;
        person.Parameters.AddWithValue("$id",state.PersonId!.Value);
        return "确认人物："+(person.ExecuteScalar() as string??"已移除的人物");
    }

    public string DescribeMetadataEdit(MetadataEdit edit, SqliteTransaction? transaction = null)
    {
        if(edit.Kind=="scene-description")return "场景标题与描述";
        if (edit.Kind=="space-setting") return TryReadClusterPreference(edit.Value,out var preference) && preference!.ModelVersion is { } model
            ? "照片空间 · 动态分类设置（"+model+"）" : "照片空间 · 动态分类设置";
        string Lookup(string sql,long id,string fallback)
        {
            using var query=Command(sql); if (transaction is not null) query.Transaction=transaction;
            query.Parameters.AddWithValue("$id",id); return query.ExecuteScalar() as string ?? fallback;
        }
        string Media(bool video,long id) => Lookup(video?"SELECT path FROM videos WHERE id=$id":"SELECT path FROM images WHERE id=$id",id,"已移除的媒体").Replace('\\','/').Split('/').Last();
        if(IsPhotoClassificationEdit(edit)) return Media(false,edit.EntityId)+" · 分类："+Picer.Core.Vision.VisualConceptCatalog.DisplayName(edit.Field);
        if(edit.Kind is "video-face-state" or "video-face-rejection")
        {
            var title=Lookup("SELECT v.path FROM video_effective_faces f JOIN videos v ON v.id=f.reference_id WHERE f.id=$id ORDER BY v.id LIMIT 1",edit.EntityId,"已移除的视频")
                .Replace('\\','/').Split('/').Last();
            return edit.Kind=="video-face-state"?title+" · 视频人脸归属":title+" · 人物推荐："+
                Lookup("SELECT display_name FROM people WHERE id=$id",long.Parse(edit.Field,System.Globalization.CultureInfo.InvariantCulture),"已移除的人物");
        }
        if(edit.Kind=="face-rejection")
            return Lookup("SELECT i.path FROM detected_faces f JOIN images i ON i.id=f.image_id WHERE f.id=$id",edit.EntityId,"已移除的照片")
                .Replace('\\','/').Split('/').Last()+" · 人物推荐："+
                Lookup("SELECT display_name FROM people WHERE id=$id",long.Parse(edit.Field,System.Globalization.CultureInfo.InvariantCulture),"已移除的人物");
        if(edit.Kind=="face-state")
            return Lookup("SELECT i.path FROM detected_faces f JOIN images i ON i.id=f.image_id WHERE f.id=$id",edit.EntityId,"已移除的照片")
                .Replace('\\','/').Split('/').Last()+" · 人脸归属";
        if (edit.Kind=="person") return Lookup("SELECT display_name FROM people WHERE id=$id",edit.EntityId,"已移除的人物")+
            " · "+(edit.Field=="name"?"姓名":edit.Field=="description"?"描述":"别名");
        if (edit.Kind=="video-concept")
            return Media(true,edit.EntityId)+" · 分类 "+System.Text.Json.JsonSerializer.Deserialize<string[]>(edit.Field)![0];
        var related=long.Parse(edit.Field,System.Globalization.CultureInfo.InvariantCulture);
        return edit.Kind=="album-link"
            ? Lookup("SELECT name FROM albums WHERE id=$id",edit.EntityId,"已移除的相册")+" · "+Media(false,related)
            : Media(edit.Kind=="video-tag-link",edit.EntityId)+" · 标签 "+Lookup("SELECT name FROM tags WHERE id=$id",related,"已移除的标签");
    }
}
