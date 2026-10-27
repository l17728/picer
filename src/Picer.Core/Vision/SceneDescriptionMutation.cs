using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Identity;

namespace Picer.Core.Vision;

public sealed record SceneDescriptionMutation(string OperationId,string? Fingerprint,IReadOnlyList<long> ImageIds,
    string DirectoryPath,SceneDescriptionText Text,SceneDescriptionText? ExpectedText,string? ExpectedEpoch=null);

public sealed class SceneDescriptionMutationService(PicerDatabase database)
{
    public string Apply(SceneDescriptionMutation request)
    {
        if(!Guid.TryParseExact(request.OperationId,"N",out _) || request.ImageIds is not {Count:>0 and <=10000}
            || string.IsNullOrWhiteSpace(request.DirectoryPath) || request.DirectoryPath.Length>32768
            || !PicerDatabase.TryReadSceneDescriptionText(JsonSerializer.Serialize(request.Text),out _))throw new ArgumentException("invalid_scene_description");
        using var transaction=database.BeginTransaction();
        using(var epoch=database.Command("SELECT epoch FROM mirror_revision WHERE id=1"))
        {
            epoch.Transaction=transaction;
            if(request.ExpectedEpoch is null || request.ExpectedEpoch!=epoch.ExecuteScalar() as string)throw new InvalidOperationException("space_epoch_reset_required");
        }
        var images=request.ImageIds.Distinct().Select(id=>database.GetImage(id)??throw new ArgumentException("scene_image_missing")).ToArray();
        string? publisher=null;
        foreach(var image in images)
        {
            var normalized=image.Path.Replace('\\','/');var separator=normalized.LastIndexOf('/');
            if(separator<0)throw new ArgumentException("scene_directory_mismatch");
            var directory=normalized[..separator];
            if(!string.Equals(directory,request.DirectoryPath.Replace('\\','/').TrimEnd('/'),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("scene_directory_mismatch");
            if(PublishedMediaPath.TryUnqualify(image.Path,out var owner,out _))
            {
                if(publisher is not null && publisher!=owner)throw new ArgumentException("scene_source_mismatch");
                publisher=owner;
            }
            else if(publisher is not null)throw new ArgumentException("scene_source_mismatch");
        }
        var service=new SceneGroupDescriptionService(database,new CloudVisionRepository(database));
        var plan=service.BuildPlan(request.DirectoryPath,images);
        var local=DeviceAuthorization.Digest("remote-scene:"+request.OperationId);
        var fingerprint=request.Fingerprint??(publisher is null?local:DeviceAuthorization.Digest(publisher+":"+local));
        var existing=database.GetAllMirroredSceneGroupDescriptions(fingerprint).SingleOrDefault();
        if(request.Fingerprint is not null && existing is null)throw new ArgumentException("scene_missing");
        if(existing is not null && !(JsonSerializer.Deserialize<long[]>(existing.ImageIdsJson)??[]).Order().SequenceEqual(images.Select(image=>image.Id).Order()))throw new ArgumentException("scene_members_changed");
        var actual=existing is null || existing.Provider=="picer-local-layout"?null:new SceneDescriptionText(existing.SceneName,existing.Summary,JsonSerializer.Deserialize<string[]>(existing.SearchTermsJson)??[],existing.Provider,existing.ModelVersion,existing.PromptVersion);
        var desired=JsonSerializer.Serialize(request.Text);
        if(actual is not null && JsonSerializer.Serialize(actual)==desired){transaction.Commit();return fingerprint;}
        if(JsonSerializer.Serialize(actual)!=JsonSerializer.Serialize(request.ExpectedText))throw new InvalidOperationException("scene_description_changed");
        var now=DateTime.UtcNow.ToString("O");
        var scene=new GatewaySceneGroupDescription(fingerprint,request.DirectoryPath,plan.StartUtc.ToString("O"),plan.EndUtc.ToString("O"),
            JsonSerializer.Serialize(images.Select(image=>image.Id)),JsonSerializer.Serialize(plan.Representatives.Select(image=>image.Id)),
            request.Text.Provider,request.Text.ModelVersion,request.Text.PromptVersion,request.Text.SceneName,request.Text.Summary,
            JsonSerializer.Serialize(request.Text.SearchTerms),"accepted",existing?.CreatedUtc??now,now);
        database.UpsertMirroredVideoMetadata(new GatewayMetadata([],[],[],[],[],[],[],[],[],SceneGroupDescriptions:[scene]),externalTransaction:transaction);
        if(publisher is not null && database.GetSceneLocalFingerprint(publisher,fingerprint) is null)database.RememberSceneBinding(publisher,local,fingerprint,false);
        database.RecordSceneDescriptionEdit(fingerprint,transaction);
        transaction.Commit();return fingerprint;
    }
}
