using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed record ClusterPreference(bool Automatic,float ManualThreshold)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelVersion { get; init; }
}

public sealed partial class PicerDatabase
{
    internal static bool TryReadClusterPreference(string value,out ClusterPreference? preference)
    {
        preference=null;
        try
        {
            using var document=JsonDocument.Parse(value); var root=document.RootElement;
            if (root.ValueKind!=JsonValueKind.Object || root.EnumerateObject().Count() is not (2 or 3) ||
                !root.TryGetProperty("Automatic",out var mode) || mode.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("ManualThreshold",out var threshold) || threshold.ValueKind!=JsonValueKind.Number ||
                !threshold.TryGetSingle(out var number) || !float.IsFinite(number) || number is <0.70f or >0.95f) return false;
            string? model = null;
            if (root.EnumerateObject().Count()==3)
            {
                if (!root.TryGetProperty("ModelVersion",out var modelValue) || modelValue.ValueKind!=JsonValueKind.String) return false;
                model=modelValue.GetString();
                if (string.IsNullOrWhiteSpace(model) || model.Length>256 || model.Trim()!=model) return false;
            }
            preference=new(mode.GetBoolean(),number) { ModelVersion=model }; return true;
        }
        catch (JsonException) { return false; }
    }

    internal static string ClusterPreferenceField(string? modelVersion) => modelVersion is null ? "clustering" :
        "clustering:"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(modelVersion))).ToLowerInvariant();

    internal MetadataEdit? GetClusterPreferenceEdit(SqliteTransaction? transaction=null,string? modelVersion=null) =>
        FindMetadataEdit(new("space-setting",1,ClusterPreferenceField(modelVersion),"",DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N")),transaction);

    public void PromoteClusterPreference(string? modelVersion=null)
    {
        using var transaction=BeginTransaction();
        if (GetClusterPreferenceEdit(transaction,modelVersion) is { ServerVersion:0 } current)
            AcceptMetadataEditsInTransaction([current],transaction);
        transaction.Commit();
    }

    public bool SaveClusterPreference(bool automatic,float threshold,bool authoritative=false,long? expectedVersion=null,string? expectedOperationId=null,string? expectedEpoch=null,string? modelVersion=null)
    {
        var value=JsonSerializer.Serialize(new ClusterPreference(automatic,threshold) { ModelVersion=modelVersion });
        var field=ClusterPreferenceField(modelVersion);
        if (!TryReadClusterPreference(value,out _)) throw new ArgumentException("invalid_cluster_preference");
        using var transaction=BeginTransaction();
        var applied=true;
        if (authoritative)
        {
            if (expectedEpoch is not null)
            {
                using var epoch=Command("SELECT epoch FROM sync_space_state WHERE id=1 AND state='active'"); epoch.Transaction=transaction;
                if (epoch.ExecuteScalar() is not string currentEpoch || currentEpoch!=expectedEpoch)
                    throw new InvalidOperationException("space_epoch_reset_required");
            }
            if (expectedVersion is null or <0) throw new ArgumentException("cluster_preference_baseline_required");
            var request=new MetadataEdit("space-setting",1,field,value,DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N")) { BaseVersion=expectedVersion.Value };
            var accepted=AcceptMetadataEditsInTransaction([request],transaction).Single();
            applied=accepted.Value==value;
        }
        else
        {
            var current=GetClusterPreferenceEdit(transaction,modelVersion);
            if (expectedVersion is { } version && current is not null &&
                (Math.Max(current.ServerVersion,current.BaseVersion)!=version || current.OperationId!=expectedOperationId))
            {
                if (current.Value!=value)
                    SaveMetadataConflict(new("space-setting",1,field,value,DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N")) { BaseVersion=version },current,transaction);
                transaction.Commit(); return current.Value==value;
            }
            RecordMetadataEdit("space-setting",1,field,value,transaction);
            ApplyMetadataEdit(GetClusterPreferenceEdit(transaction,modelVersion)!,transaction);
        }
        transaction.Commit(); return applied;
    }
}
