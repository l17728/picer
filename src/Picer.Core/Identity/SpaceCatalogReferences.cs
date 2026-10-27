using System.Globalization;
using System.Text.Json;
using Picer.Core.Data;

namespace Picer.Core.Identity;

/// <summary>Catalog references carried by a frozen batch, independently of local integer IDs.</summary>
public static class SpaceCatalogReferences
{
    public static IReadOnlyList<(string Kind,long LocalId)> Required(SpaceSyncBatch batch)
    {
        var keys=new HashSet<(string Kind,long LocalId)>();
        void Add(string kind,long id)
        {
            if(id<=0) throw new ArgumentException("invalid_catalog_reference");
            keys.Add((kind,id));
            if(keys.Count>4000) throw new ArgumentException("too_many_catalog_references");
        }
        if(batch.Kind=="catalog")
        {
            var data=batch.Payload.Deserialize<SpaceCatalogBatch>(DeviceAuthorization.Json) ?? throw new ArgumentException("invalid_catalog");
            if(data.Tags is null || data.Albums is null || data.People is null) throw new ArgumentException("invalid_catalog");
            foreach(var tag in data.Tags) Add("tag",tag?.Id ?? throw new ArgumentException("invalid_catalog_reference"));
            foreach(var album in data.Albums) Add("album",album?.Id ?? throw new ArgumentException("invalid_catalog_reference"));
            foreach(var person in data.People) Add("person",person?.Id ?? throw new ArgumentException("invalid_catalog_reference"));
        }
        else if(batch.Kind=="metadata")
        {
            var data=batch.Payload.Deserialize<SpaceMetadataBatch>(DeviceAuthorization.Json)?.Metadata ?? throw new ArgumentException("invalid_metadata");
            if(data.ImageTags is null || data.AlbumImages is null || data.FaceAssignments is null) throw new ArgumentException("invalid_metadata");
            foreach(var link in data.ImageTags) Add("tag",link?.TagId ?? throw new ArgumentException("invalid_catalog_reference"));
            foreach(var link in data.AlbumImages) Add("album",link?.AlbumId ?? throw new ArgumentException("invalid_catalog_reference"));
            foreach(var face in data.FaceAssignments) Add("person",face?.PersonId ?? throw new ArgumentException("invalid_catalog_reference"));
            foreach(var face in data.VideoFacesOrEmpty)
            {
                if(face is null) throw new ArgumentException("invalid_catalog_reference");
                if(face.AssignedPersonId is { } assigned) Add("person",assigned);
                if(face.SuggestedPersonId is { } suggested) Add("person",suggested);
            }
            foreach(var prototype in data.FacePersonPrototypesOrEmpty) Add("person",prototype?.PersonId ?? throw new ArgumentException("invalid_catalog_reference"));
            foreach(var policy in data.FacePersonPoliciesOrEmpty) Add("person",policy?.PersonId ?? throw new ArgumentException("invalid_catalog_reference"));
        }
        else if(batch.Kind=="metadata-edits")
        {
            var edits=batch.Payload.Deserialize<SpaceMetadataEditsBatch>(DeviceAuthorization.Json)?.Edits ?? throw new ArgumentException("invalid_metadata_edits");
            foreach(var edit in edits)
            {
                if(edit is null) throw new ArgumentException("invalid_catalog_reference");
                PicerDatabase.ValidateMetadataEdit(edit);
                switch(edit.Kind)
                {
                    case "person": Add("person",edit.EntityId);break;
                    case "album-link": Add("album",edit.EntityId);break;
                    case "tag-link":case "video-tag-link": Add("tag",long.Parse(edit.Field,CultureInfo.InvariantCulture));break;
                    case "face-rejection":case "video-face-rejection": Add("person",long.Parse(edit.Field,CultureInfo.InvariantCulture));break;
                    case "face-state":case "video-face-state":
                        if(PicerDatabase.ParseFaceManualState(edit.Value).PersonId is { } person) Add("person",person);
                        break;
                }
            }
        }
        return keys.OrderBy(key=>key.Kind,StringComparer.Ordinal).ThenBy(key=>key.LocalId).ToArray();
    }
}
