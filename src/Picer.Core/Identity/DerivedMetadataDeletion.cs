using System.Text.Json;

namespace Picer.Core.Identity;

public sealed record DerivedMetadataDeletion(string OperationId,string Table,string MediaGlobalId,JsonElement Before,string MediaKind="image",string? EntityGlobalId=null,string? RelatedGlobalId=null);
public sealed record DerivedMetadataDeletionBatch(IReadOnlyList<DerivedMetadataDeletion> Items,SpaceUploadScope Scope);

/// <summary>Fixed wire allowlist; never accept SQL identifiers supplied by a client.</summary>
public static class DerivedMetadataDeletionRules
{
    public static string? EntityKind(string table)=>table switch {"detected_faces" or "face_candidate_members"=>"face","video_keyframes"=>"keyframe","video_detected_faces"=>"video-face",_=>null};
    public static (string[] Keys,string[] Columns)? Get(string table) => table switch
    {
        "image_classification_assertions" => (new[]{"image_id","concept_id","source_kind","source_version"},
            new[]{"image_id","concept_id","source_kind","source_version","assertion_state","review_state","evidence_fingerprint","updated_utc","reviewed_utc"}),
        "semantic_content_cluster_members" => (new[]{"image_id","cluster_key"},new[]{"image_id","cluster_key","similarity"}),
        "cloud_vision_analyses" => (new[]{"image_id","provider"},new[]{"image_id","provider","model_version","prompt_version","source_file_size","source_modified_utc","status","summary","result_json","updated_utc"}),
        "image_locations" => (new[]{"image_id"},new[]{"image_id","country","province","city","county","district","town","village"}),
        "detected_faces" => (new[]{"id"},new[]{"image_id","model_version","ordinal","box_x","box_y","box_width","box_height","confidence","created_utc"}),
        "face_candidate_members" => (new[]{"cluster_id","face_id"},new[]{"similarity"}),
        "video_summaries" => (new[]{"video_id"},new[]{"video_id","provider","model_version","prompt_version","source_file_size","source_modified_utc","summary","result_json","created_utc"}),
        "video_transcripts" => (new[]{"video_id"},new[]{"video_id","model_version","transcript_json","created_utc"}),
        "video_keyframes" => (new[]{"id"},new[]{"video_id","timestamp_seconds","dhash"}),
        "video_detected_faces" => (new[]{"id"},new[]{"video_id","timestamp_seconds","model_version","ordinal","box_x","box_y","box_width","box_height","confidence","created_utc"}),
        _ => null
    };
}
