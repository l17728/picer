namespace Picer.Core.Classification;

/// <summary>Injected by the authenticated host; actor data is never accepted from a public request body.</summary>
public sealed record ClassificationAuthorization(Func<string?, bool> IsCurrent, Action<string?, Action> Commit,
    Action<string?, string, Action>? CommitScoped = null);
