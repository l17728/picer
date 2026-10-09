using System.Text.Encodings.Web;
using System.Text.Json;
using Picer.Core.People;
using Picer.Core.Vision;
using Picer.ModelServer;

namespace Picer.Tests;

public sealed class ArtifactWireIntegrityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClientHashMatchesHttpJsonWithChineseDiagnosticsAndSmallFloats(bool equivalentNumberSpelling)
    {
        using var signer = new ArtifactSigner();
        var artifact = signer.Sign(new TrainingArtifact("wire", "prototype-calibration-gpu", "insightface-arcface",
            DateTime.UtcNow, [new(1, [1.25e-5f, -0.03810234f], .73182718, 40)],
            new(70, 2, .922112121, .91077777, .0023151515, .0221212, 0, 0),
            Diagnostics: new("calibrate-threshold", .93, .8, .01, .07, .91, "阈值可达 90%，优先校准阈值")));
        var wire = JsonSerializer.Serialize(artifact, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (equivalentNumberSpelling) wire = wire.Replace("1.25E-05", "0.0000125");
        Assert.Equal(artifact.Sha256, FaceTrainingService.ComputeUnsignedArtifactHash(wire));
        var clientRecord = JsonSerializer.Deserialize<TrainingArtifactData>(wire, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(artifact.Sha256, FaceTrainingService.ComputeUnsignedArtifactHash(JsonSerializer.Serialize(clientRecord,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        var extra = System.Text.Json.Nodes.JsonNode.Parse(wire)!;
        extra["unsignedExtra"] = "must not escape verification";
        Assert.Throws<JsonException>(() => FaceTrainingService.ComputeUnsignedArtifactHash(extra.ToJsonString()));
        var changed = System.Text.Json.Nodes.JsonNode.Parse(wire)!;
        changed["prototypes"]![0]!["centroid"]![0] = .5f;
        Assert.NotEqual(artifact.Sha256, FaceTrainingService.ComputeUnsignedArtifactHash(changed.ToJsonString()));
    }
}
