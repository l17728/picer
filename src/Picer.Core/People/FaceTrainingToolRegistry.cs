namespace Picer.Core.People;

/// <summary>Registered training-agent tools. The registry is deliberately declarative so
/// policy, authorization and audit layers can inspect a call before it has side effects.</summary>
public sealed record FaceTrainingToolDefinition(
    string Name, string Description, bool ReadOnly, IReadOnlySet<string> RequiredScopes,
    string IdempotencyMode, string AuditEventType);

public static class FaceTrainingToolRegistry
{
    public static IReadOnlyList<FaceTrainingToolDefinition> Definitions { get; } =
    [
        new("inspect_data_quality", "检查泄漏、缺失向量、时间覆盖和连拍组", true, new HashSet<string>{ "dataset:read" }, "none", "TrainingDataInspected"),
        new("build_recipe", "根据硬规则和历史证据生成训练配方", true, new HashSet<string>{ "dataset:read" }, "recipe", "TrainingRecipeBuilt"),
        new("diagnose_threshold_or_model", "判断阈值可达性或基模能力", true, new HashSet<string>{ "dataset:read" }, "none", "TrainingDiagnosisCreated"),
        new("submit_training_job", "提交异步训练任务", false, new HashSet<string>{ "job:create" }, "required", "TrainingJobSubmitted"),
        new("verify_artifact", "验证制品签名、哈希和质量门禁", true, new HashSet<string>{ "artifact:read" }, "none", "TrainingArtifactVerified"),
        new("apply_artifact", "在用户授权后应用制品", false, new HashSet<string>{ "artifact:apply" }, "required", "TrainingArtifactApplied")
    ];

    public static bool TryGet(string name, out FaceTrainingToolDefinition definition)
    {
        definition = Definitions.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal))!;
        return definition is not null;
    }
}
