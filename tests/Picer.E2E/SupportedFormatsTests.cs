namespace Picer.E2E;

public sealed class SupportedFormatsTests
{
    [Theory]
    [InlineData(".jpg", true)]
    [InlineData(".jpeg", true)]
    [InlineData(".png", true)]
    [InlineData(".bmp", true)]
    [InlineData(".gif", true)]
    [InlineData(".webp", true)]
    [InlineData(".tif", true)]
    [InlineData(".tiff", true)]
    [InlineData(".txt", false)]
    [InlineData(".pdf", false)]
    [InlineData(".mp4", false)]
    [InlineData(".doc", false)]
    [InlineData(".xml", false)]
    public void IsSupported_ReturnsCorrectly(string ext, bool expected)
    {
        Assert.Equal(expected, Picer.Core.Indexing.SupportedFormats.IsSupported($"test{ext}"));
    }

    [Fact]
    public void OpenFileDialogFilter_ContainsAllFormats()
    {
        var filter = Picer.Core.Indexing.SupportedFormats.OpenFileDialogFilter;
        Assert.Contains(".jpg", filter);
        Assert.Contains(".png", filter);
        Assert.Contains(".webp", filter);
    }
}
