using System.Net;
using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Storage;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class IndexReferenceInspectorTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"picer-reference-inspection-"+Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database=new(":memory:");
    public IndexReferenceInspectorTests() => Directory.CreateDirectory(_root);
    private ImageRecord Photo(long id,long source,string path)
    {
        var image=new ImageRecord { Id=id,SourceId=source,Path=path,FileSize=10,ModifiedUtc=DateTime.UtcNow,IndexedUtc=DateTime.UtcNow };
        _database.UpsertMirroredImage(image); return image;
    }

    [Fact]
    public async Task OnlySelectedReferencesAreCheckedAndBothMediaKindsKeepTheirIndexWhenMissing()
    {
        var source=_database.AddSource(_root); var other=_database.AddSource(Path.Combine(_root,"private"));
        var gone=Photo(1,source.Id,Path.Combine(_root,"gone.jpg"));
        var retained=Photo(2,source.Id,Path.Combine(_root,"retained.jpg")); File.WriteAllBytes(retained.Path,new byte[10]);
        Photo(3,other.Id,Path.Combine(_root,"private","private.jpg"));
        new VideoRepository(_database).UpsertMirrored(new VideoRecord(10,source.Id,Path.Combine(_root,"gone.mp4"),10,DateTime.UtcNow,
            null,0,0,null,null,"active"),source.Id);
        var result=await new IndexReferenceInspector(_database,new References()).InspectAsync([source.Id]);
        Assert.True(result.Complete); Assert.Equal(2,result.CheckedPhotos); Assert.Equal(1,result.CheckedVideos);
        Assert.Equal(1,result.MissingPhotos); Assert.Equal(1,result.MissingVideos);
        Assert.Equal(ImageStatus.Missing,_database.GetImage(gone.Id)!.Status);
        Assert.Equal(ImageStatus.Active,_database.GetImage(3)!.Status);
        using var video=_database.Command("SELECT status FROM videos WHERE id=10"); Assert.Equal("missing",video.ExecuteScalar());
        Assert.Equal(new byte[10],File.ReadAllBytes(retained.Path));
        Assert.Equal(3,_database.GetImages(includeMissing:true).Count);
    }

    [Fact]
    public async Task OfflineRootAndForbiddenFileNeverBecomeEvidenceOfDeletion()
    {
        var offline=_database.AddSource(Path.Combine(_root,"offline")); Photo(1,offline.Id,Path.Combine(offline.Path,"one.jpg"));
        var result=await new IndexReferenceInspector(_database,new References()).InspectAsync([offline.Id]);
        Assert.False(result.Complete); Assert.Equal(offline.Id,Assert.Single(result.UnavailableSources));
        Assert.Equal(ImageStatus.Active,_database.GetImage(1)!.Status);
        var online=_database.AddSource(_root); Photo(2,online.Id,Path.Combine(_root,"denied.jpg"));
        var denied=new References("webdav",(path,_)=>path==_root ? Task.FromResult(true) :
            Task.FromException<bool>(new HttpRequestException("forbidden",null,HttpStatusCode.Forbidden)));
        var forbidden=await new IndexReferenceInspector(_database,denied).InspectAsync([online.Id]);
        Assert.False(forbidden.Complete); Assert.Equal(1,forbidden.FailedItems);
        Assert.Equal(ImageStatus.Active,_database.GetImage(2)!.Status);
    }

    [Fact]
    public async Task ConcurrentRepairAndCancellationCannotMarkAChangedReferenceMissing()
    {
        var source=_database.AddSource(_root); var old=Photo(1,source.Id,Path.Combine(_root,"old.jpg"));
        var moved=Path.Combine(_root,"new.jpg"); File.WriteAllBytes(moved,new byte[10]);
        var storage=new References("local",(_,_)=>
        {
            using var repair=_database.Command("UPDATE images SET path=$path WHERE id=1"); repair.Parameters.AddWithValue("$path",moved); repair.ExecuteNonQuery();
            return Task.FromResult(false);
        });
        var result=await new IndexReferenceInspector(_database,storage).InspectAsync([source.Id]);
        Assert.Equal(0,result.MissingPhotos); Assert.Equal(moved,_database.GetImage(old.Id)!.Path);
        Assert.Equal(ImageStatus.Active,_database.GetImage(old.Id)!.Status);
        using var stop=new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new IndexReferenceInspector(_database,storage).InspectAsync([source.Id],cancellationToken:stop.Token));
        Assert.Equal(ImageStatus.Active,_database.GetImage(old.Id)!.Status);
    }

    private sealed class References(string scheme="local",Func<string,CancellationToken,Task<bool>>? exists=null) : IStorageProvider
    {
        public string Scheme=>scheme;
        public Task<bool> ExistsAsync(string key,CancellationToken ct=default) => exists?.Invoke(key,ct)??Task.FromResult(File.Exists(key));
        public bool Exists(string key)=>throw new InvalidOperationException("synchronous_probe_not_expected");
        public IEnumerable<StorageFileInfo> EnumerateFiles(string root)=>throw new InvalidOperationException("directory_enumeration_not_allowed");
        public Task<IEnumerable<StorageFileInfo>> EnumerateFilesAsync(string root,CancellationToken ct=default)=>throw new InvalidOperationException("directory_enumeration_not_allowed");
        public Stream OpenRead(string key)=>throw new InvalidOperationException("original_read_not_allowed");
        public Task<Stream> OpenReadAsync(string key,CancellationToken ct=default)=>throw new InvalidOperationException("original_read_not_allowed");
        public StorageFileInfo? GetInfo(string key)=>throw new InvalidOperationException("content_probe_not_allowed");
        public Task<StorageFileInfo?> GetInfoAsync(string key,CancellationToken ct=default)=>throw new InvalidOperationException("content_probe_not_allowed");
    }
    public void Dispose() { _database.Dispose(); Directory.Delete(_root,true); }
}
