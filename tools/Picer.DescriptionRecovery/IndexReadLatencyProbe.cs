using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Identity;

[SupportedOSPlatform("windows")]
internal static class IndexReadLatencyProbe
{
    public static async Task RunAsync(string databasePath, string credentialPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var observations = new List<object>();
        void Report(object value)
        {
            observations.Add(value);
            var line = JsonSerializer.Serialize(value);
            Console.WriteLine(line);
            File.AppendAllText(Path.Combine(outputDirectory, "latency.jsonl"), line + Environment.NewLine);
        }
        using var account = new WindowsDeviceCredentialStore(credentialPath).OpenClient();
        var origin = new Uri(account.Connection.Origin);
        var expectedPin = Convert.FromBase64String(account.Connection.SpkiPin);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var timer = Stopwatch.StartNew();
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(origin.Host, deadline.Token);
                var dns = timer.Elapsed.TotalMilliseconds;
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(addresses.First(x => x.AddressFamily == AddressFamily.InterNetwork), origin.Port, deadline.Token);
                var connected = timer.Elapsed.TotalMilliseconds;
                using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    using var cert = new X509Certificate2(certificate);
                    return CryptographicOperations.FixedTimeEquals(cert.PublicKey.ExportSubjectPublicKeyInfo(), expectedPin);
                });
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = origin.Host }, deadline.Token);
                Report(new { stage = "connection", iteration, dnsMs = dns, tcpMs = connected - dns, tlsMs = timer.Elapsed.TotalMilliseconds - connected });
            }
            catch (Exception error) { Report(new { stage = "connection", iteration, elapsedMs = timer.Elapsed.TotalMilliseconds, error = error.GetType().Name }); }
        }
        var open = Stopwatch.StartNew();
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        database.Open();
        Report(new { stage = "sqlite-open", elapsedMs = open.Elapsed.TotalMilliseconds });
        var frameIds = new List<long>();
        foreach (var videoId in new long[] { 1, 256 })
        {
            using var query = database.CreateCommand();
            query.CommandText = "SELECT k.id FROM video_keyframes k JOIN videos v ON v.content_id=k.video_id WHERE v.id=$id ORDER BY k.timestamp_seconds LIMIT 1";
            query.Parameters.AddWithValue("$id", videoId);
            if (query.ExecuteScalar() is long frame) frameIds.Add(frame);
        }
        var queries = new Dictionary<string, string>
        {
            ["video-single"] = "SELECT * FROM videos WHERE id=1",
            ["video-all-used-by-frame-loader"] = "SELECT id,source_id,path,file_size,modified_utc,duration_seconds,width,height,taken_utc,gps,status,person_analysis_status,person_analyzed_utc,sha256 FROM videos ORDER BY path",
            ["transcript"] = "SELECT transcript_json FROM video_transcripts WHERE video_id=(SELECT content_id FROM videos WHERE id=1)",
            ["summary"] = "SELECT summary FROM video_summaries WHERE video_id=(SELECT content_id FROM videos WHERE id=1)",
            ["keyframe-metadata"] = "SELECT timestamp_seconds,path,dhash,id FROM video_keyframes WHERE video_id=(SELECT content_id FROM videos WHERE id=1) ORDER BY timestamp_seconds",
            ["people-list"] = """
                SELECT p.id,p.display_name,
                  COUNT(DISTINCT CASE WHEN a.state='confirmed' THEN 'p:'||a.face_id END)+COUNT(DISTINCT CASE WHEN vf.assignment_state='confirmed' THEN 'v:'||vf.id END),
                  COUNT(DISTINCT CASE WHEN a.state='confirmed' THEN 'p:'||f.image_id END)+COUNT(DISTINCT CASE WHEN vf.assignment_state='confirmed' THEN 'v:'||vf.video_id END)
                FROM people p LEFT JOIN face_person_assignments a ON a.person_id=p.id
                LEFT JOIN detected_faces f ON f.id=a.face_id
                LEFT JOIN video_detected_faces vf ON vf.assigned_person_id=p.id AND EXISTS(SELECT 1 FROM video_effective_faces owned WHERE owned.id=vf.id)
                GROUP BY p.id,p.display_name ORDER BY p.display_name COLLATE NOCASE
                """
        };
        for (var iteration = 0; iteration < 3; iteration++)
            foreach (var (stage, sql) in queries)
            {
                var timer = Stopwatch.StartNew();
                using var query = database.CreateCommand(); query.CommandText = sql;
                using var reader = query.ExecuteReader(); var rows = 0;
                while (reader.Read()) { for (var i = 0; i < reader.FieldCount; i++) _ = reader.GetValue(i); rows++; }
                Report(new { stage, iteration, rows, elapsedMs = timer.Elapsed.TotalMilliseconds });
            }
        using var client = account.CreateSpaceClient(TimeSpan.FromSeconds(90));
        for (var iteration = 0; iteration < 3; iteration++)
        {
            await Measure("http-video", "api/gateway/v1/videos/1", true, iteration);
            await Measure("http-sources", "api/gateway/v1/sources", true, iteration);
            foreach (var frameId in frameIds.Distinct())
                await Measure("http-keyframe-" + frameId, "api/gateway/v1/video-keyframes/" + frameId, false, iteration);
        }
        await Measure("http-full-metadata", "api/gateway/v1/metadata?includeEmbeddings=false&includeFaceModels=false", true, 0);
        async Task Measure(string stage, string route, bool json, int iteration)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var startedUtc = DateTimeOffset.UtcNow;
            var timer = Stopwatch.StartNew();
            try
            {
                using var response = await client.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var headers = timer.Elapsed.TotalMilliseconds;
                var body = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                var downloaded = timer.Elapsed.TotalMilliseconds;
                if (json && response.IsSuccessStatusCode) { using var document = JsonDocument.Parse(body); }
                Report(new { stage, iteration, startedUtc, status = (int)response.StatusCode, headersMs = headers,
                    bodyMs = downloaded - headers, parseMs = timer.Elapsed.TotalMilliseconds - downloaded, bytes = body.Length,
                    serverTiming = response.Headers.TryGetValues("Server-Timing", out var values) ? string.Join(',', values) : null });
            }
            catch (Exception error) { Report(new { stage, iteration, startedUtc, elapsedMs = timer.Elapsed.TotalMilliseconds, error = error.GetType().Name }); }
        }
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "latency.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
    }
}
