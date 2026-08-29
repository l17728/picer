using System.IO;
using Picer.Core.Hashing;

namespace Picer.Tests;

public sealed class HashTests
{
    [Fact]
    public void ComputeSha256_HandlesEmptyStream_ReturnsExpectedHash()
    {
        var result = FileHasher.ComputeSha256(Stream.Null);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", result);
    }

    [Fact]
    public void ComputeSha256_HandlesKnownContent_ReturnsCorrectHash()
    {
        using var ms = new MemoryStream();
        var sw = new StreamWriter(ms);
        sw.Write("Hello, World!");
        sw.Flush();
        ms.Position = 0;
        var result = FileHasher.ComputeSha256(ms);
        Assert.Equal("dffd6021bb2bd5b0af676290809ec3a53191dd81c7f70a4b28688a362182986f", result);
    }
}
