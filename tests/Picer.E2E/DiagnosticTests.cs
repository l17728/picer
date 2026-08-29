using Picer.App;

namespace Picer.E2E;

public sealed class DiagnosticTests
{
    [Fact]
    public void Redaction_RemovesWindowsPathsAndSecrets()
    {
        var source = "open D:\\Photos\\Family\\secret.jpg token=ghp_example password:hello Authorization=Bearer123";
        var redacted = DiagnosticService.Redact(source);
        Assert.DoesNotContain("Family", redacted);
        Assert.DoesNotContain("ghp_example", redacted);
        Assert.DoesNotContain("hello", redacted);
        Assert.DoesNotContain("Bearer123", redacted);
        Assert.Contains("[PATH]", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }
}
