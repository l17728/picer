using System.Security.Cryptography;

namespace Picer.ModelServer;

public sealed record RecipeSelection(long FaceId, long PersonId, string Split, string Reason);

/// Deterministic, algorithm-agnostic first pass for constructing a training
/// recipe from GUI evidence. VL enrichment is applied by the next stage.
public sealed class TrainingRecipeBuilder
{
    public IReadOnlyList<RecipeSelection> Build(DatasetRequest dataset, int seed = 20260914)
    {
        var samples = (dataset.Samples ?? [])
            .Where(s => s.Evidence?.Source == "user-confirmed")
            .GroupBy(s => s.Evidence?.ImageHash ?? $"face:{s.FaceId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(s => Stable(seed, s.FaceId)).First())
            .ToArray();
        return samples.Select(s => new RecipeSelection(s.FaceId, s.PersonId, s.Split,
            s.Evidence?.SelectionReason ?? "user-confirmed;deduplicated")).ToArray();
    }

    private static uint Stable(int seed, long id)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{seed}:{id}"));
        return BitConverter.ToUInt32(bytes, 0);
    }
}
