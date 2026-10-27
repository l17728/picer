using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;

namespace Picer.Core.Identity;

public static class TrainingSyncBaseline
{
    public static string Artifact(GatewayFaceTrainingArtifact artifact) => Artifact(artifact.ModelVersion,
        artifact.ArtifactJson, artifact.State, artifact.AppliedUtc);
    public static string Artifact(string model, string json, string state, string? appliedUtc) =>
        DeviceAuthorization.Digest(JsonSerializer.Serialize(new { model, json, state, appliedUtc }, DeviceAuthorization.Json));
    public static string Row<T>(T value) => DeviceAuthorization.Digest(JsonSerializer.Serialize(value, DeviceAuthorization.Json));
    public static string PrototypeKey(long person, string model) => person.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + model;
    public static string PersonKey(long person) => person.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string? Read(PicerDatabase database, string publisher, string kind, string key)
    {
        using var query = database.Command("SELECT fingerprint FROM sync_training_baselines WHERE publisher_id=$publisher AND kind=$kind AND row_key=$key");
        query.Parameters.AddWithValue("$publisher", publisher); query.Parameters.AddWithValue("$kind", kind); query.Parameters.AddWithValue("$key", key);
        return query.ExecuteScalar() as string;
    }

    public static void Save(PicerDatabase database, string publisher, string kind, string key, string model, string fingerprint)
    {
        using var save = database.Command("""
            INSERT INTO sync_training_baselines(publisher_id,kind,row_key,model_version,fingerprint)
            VALUES($publisher,$kind,$key,$model,$fingerprint)
            ON CONFLICT(publisher_id,kind,row_key) DO UPDATE SET model_version=excluded.model_version,fingerprint=excluded.fingerprint
            """);
        save.Parameters.AddWithValue("$publisher", publisher); save.Parameters.AddWithValue("$kind", kind);
        save.Parameters.AddWithValue("$key", key); save.Parameters.AddWithValue("$model", model); save.Parameters.AddWithValue("$fingerprint", fingerprint);
        save.ExecuteNonQuery();
    }

    public static void Acknowledge(PicerDatabase database, string publisher, GatewayMetadata data)
    {
        foreach (var artifact in data.FaceTrainingArtifactsOrEmpty)
            Save(database, publisher, "artifact", artifact.ArtifactId, artifact.ModelVersion, Artifact(artifact));
        foreach (var prototype in data.FacePersonPrototypesOrEmpty)
            Save(database, publisher, "prototype", PrototypeKey(prototype.PersonId, prototype.ModelVersion), prototype.ModelVersion, Row(prototype));
        foreach (var policy in data.FacePersonPoliciesOrEmpty)
            Save(database, publisher, "policy", PersonKey(policy.PersonId), policy.ModelVersion, Row(policy));
    }
}
