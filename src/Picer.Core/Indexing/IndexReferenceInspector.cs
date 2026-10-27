using System.Collections.Concurrent;
using Picer.Core.Data;
using Picer.Core.Storage;

namespace Picer.Core.Indexing;

public sealed record IndexReferenceInspectionResult(long CheckedPhotos,long CheckedVideos,long MissingPhotos,long MissingVideos,
    IReadOnlyList<long> UnavailableSources,long FailedItems)
{
    public bool Complete => UnavailableSources.Count==0 && FailedItems==0;
}

/// <summary>Checks indexed references only; never enumerates a directory tree or reads original media content.</summary>
public sealed class IndexReferenceInspector(PicerDatabase database,IStorageProvider storage)
{
    private sealed record Reference(long Id,string Path,long Size,string Modified);

    public async Task<IndexReferenceInspectionResult> InspectAsync(IReadOnlyList<long>? sourceIds=null,bool photos=true,bool videos=true,
        CancellationToken cancellationToken=default)
    {
        if (!photos && !videos) throw new ArgumentException("inspection_media_scope_empty");
        var sources=database.GetSources().Where(source=>sourceIds is null || sourceIds.Contains(source.Id)).ToArray();
        var unavailable=new HashSet<long>(sourceIds?.Except(sources.Select(source=>source.Id))??[]);
        long checkedPhotos=0,checkedVideos=0,missingPhotos=0,missingVideos=0,failed=0;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await RootAvailableAsync(source.Path,cancellationToken).ConfigureAwait(false)) { unavailable.Add(source.Id); continue; }
            foreach (var table in new[]{"images","videos"}.Where(table=>table=="images" ? photos : videos))
            {
                long after=0,through;
                using (var upper=database.Command($"SELECT COALESCE(MAX(id),0) FROM {table} WHERE source_id=$source"))
                { upper.Parameters.AddWithValue("$source",source.Id); through=Convert.ToInt64(upper.ExecuteScalar()); }
                while (after<through)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page=new List<Reference>();
                    using (var query=database.Command($"SELECT id,path,file_size,modified_utc FROM {table} WHERE source_id=$source AND status='active' AND id>$after AND id<=$through ORDER BY id LIMIT 256"))
                    {
                        query.Parameters.AddWithValue("$source",source.Id); query.Parameters.AddWithValue("$after",after); query.Parameters.AddWithValue("$through",through);
                        using var rows=query.ExecuteReader();
                        while (rows.Read()) page.Add(new(rows.GetInt64(0),rows.GetString(1),rows.GetInt64(2),rows.GetString(3)));
                    }
                    if (page.Count==0) break;
                    after=page[^1].Id;
                    var missing=new ConcurrentBag<Reference>();
                    await Parallel.ForEachAsync(page,new ParallelOptions { MaxDegreeOfParallelism=4,CancellationToken=cancellationToken },async (item,token)=>
                    {
                        try
                        {
                            if (!await storage.ExistsAsync(item.Path,token).ConfigureAwait(false))
                            {
                                if (storage.Scheme=="local")
                                {
                                    try { _=File.GetAttributes(item.Path); return; }
                                    catch (FileNotFoundException) { }
                                    catch (DirectoryNotFoundException) { }
                                }
                                missing.Add(item);
                            }
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException)
                        { Interlocked.Increment(ref failed); }
                    }).ConfigureAwait(false);
                    if (table=="images") checkedPhotos+=page.Count; else checkedVideos+=page.Count;
                    if (missing.IsEmpty) continue;
                    // A disconnected drive/share is not evidence that every indexed
                    // file was deleted. Keep prior statuses and report the root.
                    if (!await RootAvailableAsync(source.Path,cancellationToken).ConfigureAwait(false)) { unavailable.Add(source.Id); break; }
                    using var transaction=database.BeginTransaction();
                    using var update=database.Command($"UPDATE {table} SET status='missing' WHERE id=$id AND source_id=$source AND path=$path AND file_size=$size AND modified_utc=$modified AND status='active'");
                    update.Transaction=transaction;
                    foreach (var item in missing)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); update.Parameters.Clear();
                        update.Parameters.AddWithValue("$id",item.Id); update.Parameters.AddWithValue("$source",source.Id);
                        update.Parameters.AddWithValue("$path",item.Path); update.Parameters.AddWithValue("$size",item.Size);
                        update.Parameters.AddWithValue("$modified",item.Modified);
                        var changed=update.ExecuteNonQuery();
                        if (table=="images") missingPhotos+=changed; else missingVideos+=changed;
                    }
                    transaction.Commit();
                }
                if (unavailable.Contains(source.Id)) break;
            }
        }
        return new(checkedPhotos,checkedVideos,missingPhotos,missingVideos,unavailable.Order().ToArray(),failed);
    }

    private async Task<bool> RootAvailableAsync(string root,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return storage.Scheme=="local" ? Directory.Exists(root) : await storage.ExistsAsync(root,token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException) { return false; }
    }
}
