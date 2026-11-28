using System.IO;
using System.Security.Cryptography;
using System.Text;
using Picer.App.Infrastructure;
using Picer.Core.Gateway;

namespace Picer.App.Services;

/// <summary>One persistent store for image viewers, previews and external open operations.</summary>
public static class SharedImageAssets
{
    internal static readonly Lazy<PlayableMediaCache> Cache = new(() => new(Path.Combine(AppPaths.DataRoot, "remote-assets", "playable")));

    public static async Task<string> GetAsync(IMediaAssetGateway gateway, long id, string path, string? hash,
        bool preview, CancellationToken token)
    {
        var scope = (gateway as HttpIndexGateway)?.MediaCacheScope ?? AppServices.RemoteDataRoot;
        if (hash is { Length: 64 } && hash.All(Uri.IsHexDigit))
            return await GetCachedAsync(Cache.Value, scope, hash.ToLowerInvariant(), gateway, id, path, hash, preview, token);

        var snapshot = await UiWork.ReadSnapshotAsync("ImageAsset.Identity", AppServices.Db,
            db => (Record: db.GetImage(id), Epoch: db.GetMirrorCheckpoint()?.Epoch), token);
        var record = snapshot.Record;
        hash = record?.ContentSha256 ?? record?.Sha256 ?? hash;
        var identity = hash is { Length: 64 } ? hash.ToLowerInvariant()
            : $"{id}:{record?.FileSize}:{record?.ModifiedUtc.Ticks}:{snapshot.Epoch}";
        if (record is null && hash is not { Length: 64 } && gateway is IRangeMediaGateway range)
            identity += ":" + (await range.GetMediaInfoAsync(id, false, token)).ETag;
        return await GetCachedAsync(Cache.Value, scope, identity, gateway, id, path, hash, preview, token);
    }

    internal static Task<string> GetCachedAsync(PlayableMediaCache cache, string scope, string identity,
        IMediaAssetGateway gateway, long id, string path, string? hash, bool preview, CancellationToken token)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}:{identity}:{(preview ? "preview-1600-v1" : "original")}"))).ToLowerInvariant();
        return cache.GetAsync(key, preview ? ".jpg" : Path.GetExtension(path), preview ? null : hash,
            async (target, cancellation) =>
            {
                await using var input = preview && gateway is HttpIndexGateway remote
                    ? await remote.OpenPreviewAsync(id, cancellation)
                    : await gateway.OpenOriginalImageAsync(id, cancellationToken: cancellation);
                if (input is null) throw new IOException("image_asset_unavailable");
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
                var buffer = new byte[131072];
                var limit = preview ? 8L * 1024 * 1024 : 20L * 1024 * 1024 * 1024;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    if (output.Length + count > limit) throw new IOException("image_asset_too_large");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
                if (output.Length == 0) throw new InvalidDataException("image_asset_empty");
                await output.DisposeAsync();
                if (preview) await Task.Run(() => ThumbnailBitmapLoader.Load(target), cancellation);
            }, token);
    }
}
