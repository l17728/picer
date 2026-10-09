using System.Net;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class AnalysisStageExceptionTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void StoredFailurePreservesStageStatusAndRetryabilityWithoutRawRequestText(HttpStatusCode status, bool retryable)
    {
        var failure = AnalysisStageException.Wrap(AnalysisStage.Model, new HttpRequestException("sensitive-request-body", null, status));
        var restored = AnalysisStageException.FromStored(nameof(AnalysisStageException) + ": " + failure.Message);
        Assert.Equal(AnalysisStage.Model, restored.Stage); Assert.Equal("HTTP_" + (int)status, restored.ErrorCode);
        Assert.Equal(retryable, restored.Retryable); Assert.DoesNotContain("sensitive", failure.Message);
    }

    [Fact]
    public void SaveFailureKeepsItsStageWhenWrappedAtTheUiBoundary()
    {
        var failure = AnalysisStageException.Wrap(AnalysisStage.Save, new Microsoft.Data.Sqlite.SqliteException("sensitive-sql", 19));
        Assert.Same(failure, AnalysisStageException.Wrap(AnalysisStage.Model, failure));
        Assert.Equal(AnalysisStage.Save, failure.Stage); Assert.StartsWith("SQLite_19_", failure.ErrorCode);
        Assert.DoesNotContain("sensitive", failure.Message);
    }
}
