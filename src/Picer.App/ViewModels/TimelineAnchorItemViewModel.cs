namespace Picer.App.ViewModels;

public sealed class TimelineAnchorItemViewModel(string label)
{
    public string Label { get; } = label;
    public bool IsTimelineAnchor => true;
}
