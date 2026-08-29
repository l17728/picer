using CommunityToolkit.Mvvm.ComponentModel;

namespace Picer.App.ViewModels;

public partial class SourceNodeViewModel : ObservableObject
{
    public long SourceId { get; init; }
    public bool IsAll { get; init; }

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private int _imageCount;

    public string FullPath { get; init; } = "";
}
