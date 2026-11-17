using System.Net.Http;

namespace Picer.Core.Logging;

internal static class AnalysisFailureDiagnostics
{
    public static void Record(string phase, long mediaId, Exception exception, bool callerCancelled)
    {
        try
        {
            var chain = new List<string>();
            for (Exception? current = exception; current is not null && chain.Count < 5; current = current.InnerException)
            {
                var type = current.GetType().Name;
                if (!type.All(char.IsAsciiLetterOrDigit)) type = "Exception";
                var status = current is HttpRequestException { StatusCode: { } code } ? ((int)code).ToString() : "none";
                var socket = current is System.Net.Sockets.SocketException error ? ((int)error.SocketErrorCode).ToString() : "none";
                chain.Add($"{type}:hresult=0x{current.HResult:X8}:http={status}:socket={socket}");
            }
            AnalysisDiagnosticLog.Warn($"Analysis original failure; attemptId={AnalysisTrace.Current ?? "none"}; phase={phase}; mediaId={mediaId}; callerCancelled={callerCancelled}; chain={string.Join('|', chain)}");
        }
        catch { /* Never replace the original failure with a diagnostic failure. */ }
    }
}
