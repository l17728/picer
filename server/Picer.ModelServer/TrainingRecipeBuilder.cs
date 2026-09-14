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
            // Keep an original/derived burst in one recipe item.  Image hash is
            // preferred; GroupId prevents leakage when a burst has been resized.
            .GroupBy(s => s.Evidence?.ImageHash is { Length: > 0 } hash
                ? $"hash:{hash}"
                : s.Evidence?.GroupId is { Length: > 0 } group
                    ? $"group:{group}"
                    : $"face:{s.FaceId}", StringComparer.OrdinalIgnoreCase)
            // Prefer usable, difficult examples, then use a stable tie-breaker.
            .Select(g => g.OrderByDescending(s => s.Attributes?.IsUsefulForTraining ?? true)
                .ThenByDescending(s => s.Attributes?.FaceQuality ?? 0)
                .ThenBy(s => Stable(seed, s.FaceId)).First())
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
