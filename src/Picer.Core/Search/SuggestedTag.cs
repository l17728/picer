namespace Picer.Core.Search;

/// <summary>
/// Represents a suggested tag with confidence level for AI-assisted classification.
/// </summary>
public sealed class SuggestedTag
{
    public string TagName { get; }
    public Confidence Confidence { get; }

    public SuggestedTag(string tagName, Confidence confidence)
    {
        TagName = tagName;
        Confidence = confidence;
    }
}

public enum Confidence
{
    Low,
    Medium,
    High
}
