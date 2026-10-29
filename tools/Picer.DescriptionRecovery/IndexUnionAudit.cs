using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Gateway;
using Picer.Core.Identity;

[SupportedOSPlatform("windows")]
internal static class IndexUnionAudit
{
    public static async Task RunAsync(string databasePath, string credentialPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var account = new WindowsDeviceCredentialStore(credentialPath).OpenClient();
        using var gateway = new HttpIndexGateway(account.CreateSpaceClient(), ownsClient: true);
        var cut = await gateway.GetChangesAsync(new(), deadline.Token);
        try
        {
            var remoteImages = new List<Picer.Core.Models.ImageRecord>();
            var remoteVideos = new List<Picer.Core.Video.VideoRecord>();
            string? cursor = null;
            do
            {
                var page = await gateway.ListImagesAsync(includeMissing: true, pageSize: 500, cursor: cursor, cancellationToken: deadline.Token);
                remoteImages.AddRange(page.Items); cursor = page.NextCursor;
            } while (cursor is not null);
            do
            {
                var page = await gateway.ListVideosAsync(pageSize: 500, cursor: cursor, cancellationToken: deadline.Token);
                remoteVideos.AddRange(page.Items); cursor = page.NextCursor;
            } while (cursor is not null);
            var remoteMetadata = await gateway.GetMetadataAsync(deadline.Token);
            var sources = await gateway.ListSourcesAsync(deadline.Token);
            using var local = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            local.Open();
            using var transaction = local.BeginTransaction(deferred: true);
            List<Dictionary<string, object?>> Read(string sql)
            {
                using var query = local.CreateCommand(); query.Transaction = transaction; query.CommandText = sql;
                using var reader = query.ExecuteReader(); var result = new List<Dictionary<string, object?>>();
                while (reader.Read())
                {
                    var row = new Dictionary<string, object?>();
                    for (var i = 0; i < reader.FieldCount; i++) row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
                    result.Add(row);
                }
                return result;
            }
            var checkpoint = Read("SELECT epoch,media_revision,metadata_revision FROM mirror_checkpoint WHERE id=1").Single();
            if ((string)checkpoint["epoch"]! != cut.Epoch) throw new InvalidOperationException("audit_epoch_mismatch");
            var images = Read("SELECT id,source_id,path,file_size,modified_utc,sha256,status FROM images ORDER BY id");
            var videos = Read("SELECT id,source_id,path,file_size,modified_utc,sha256,status,content_id FROM videos ORDER BY id");
            var photos = Read("SELECT * FROM cloud_vision_analyses WHERE status='completed' ORDER BY image_id,provider");
            var summaries = Read("SELECT * FROM video_summaries ORDER BY video_id");
            var edits = Read("SELECT * FROM metadata_edits");
            var hasHistory = Read("SELECT name FROM sqlite_master WHERE type='table' AND name='description_versions'").Count != 0;
            var localVersions = hasHistory ? Read("SELECT payload FROM description_versions WHERE epoch=(SELECT epoch FROM mirror_checkpoint WHERE id=1)")
                .Select(row => JsonSerializer.Deserialize<DescriptionVersion>((string)row["payload"]!)!).Where(IsCurrent).ToArray() : [];
            var remoteVersions = remoteMetadata.DescriptionVersions;
            var pending = hasHistory ? Read("SELECT SUM(acknowledged=0) AS pending,SUM(acknowledged=1) AS awaiting_readback FROM description_outbox").Single() : null;
            bool IsCurrent(DescriptionVersion version)
            {
                var row = (version.Photo is not null ? images : videos).FirstOrDefault(x => (long)x["id"]! == version.MediaId);
                if (row is null || (long)row["file_size"]! != (version.Photo?.SourceFileSize ?? version.Video?.SourceFileSize)) return false;
                var expected = version.Photo?.SourceModifiedUtc ?? version.Video?.SourceModifiedUtc;
                var hashMatches = version.Sha256 is { Length: 64 } hash && hash.Equals(row["sha256"] as string, StringComparison.OrdinalIgnoreCase);
                if (version.Sha256 is not null && !hashMatches) return false;
                return version.Video is not null && hashMatches || DateTimeOffset.TryParse(expected, out var modified) &&
                    DateTimeOffset.Parse((string)row["modified_utc"]!) == modified;
            }
            // These are private recovery artifacts, never console diagnostics or uploads.
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "local-index-evidence.json"),
                JsonSerializer.Serialize(new { checkpoint, images, videos, photos, summaries, edits, descriptionVersions = localVersions, pending }), deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "remote-index-evidence.json"),
                JsonSerializer.Serialize(new { cut.Epoch, cut.Through, cut.MetadataRevision, sources,
                    images = remoteImages, videos = remoteVideos, metadata = remoteMetadata }), deadline.Token);
            var localImages = images.Select(row => (long)row["id"]!).ToHashSet();
            var localVideos = videos.Select(row => (long)row["id"]!).ToHashSet();
            var remoteImageIds = remoteImages.Select(row => row.Id).ToHashSet();
            var remoteVideoIds = remoteVideos.Select(row => row.Id).ToHashSet();
            var summary = new
            {
                readOnly = true, sameEpoch = true, remoteSnapshot = cut.Snapshot is not null,
                localMediaRevision = checkpoint["media_revision"], remoteMediaRevision = cut.Through,
                localMetadataRevision = checkpoint["metadata_revision"], remoteMetadataRevision = cut.MetadataRevision,
                images = new { local = localImages.Count, remote = remoteImageIds.Count,
                    localOnly = localImages.Except(remoteImageIds).Count(), remoteOnly = remoteImageIds.Except(localImages).Count() },
                videos = new { local = localVideos.Count, remote = remoteVideoIds.Count,
                    localOnly = localVideos.Except(remoteVideoIds).Count(), remoteOnly = remoteVideoIds.Except(localVideos).Count() },
                photoDescriptions = new { local = photos.Count, remote = remoteMetadata.CloudVisionAnalysesOrEmpty.Count },
                videoDescriptions = new { local = summaries.Count, remote = remoteMetadata.VideoSummaries.Count },
                descriptionVersions = new { localSupportsHistory = hasHistory, remoteSupportsHistory = remoteVersions is not null,
                    local = localVersions.Length, remote = remoteVersions?.Count,
                    localOnly = remoteVersions is null ? (int?)null : localVersions.Select(x => x.VersionId).Except(remoteVersions.Select(x => x.VersionId)).Count(),
                    remoteOnly = remoteVersions is null ? (int?)null : remoteVersions.Select(x => x.VersionId).Except(localVersions.Select(x => x.VersionId)).Count(), pending }
            };
            var report = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "audit-summary.json"), report, deadline.Token);
            Console.WriteLine(report);
        }
        finally { await gateway.ReleaseReadSnapshotAsync(CancellationToken.None); }
    }
}
