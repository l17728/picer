using System.Text.Json;
using Picer.Core.Models;
using Picer.Core.Video;
using Picer.Core.Search;
using Picer.Core.People;
using Picer.Core.Gateway;

namespace Picer.Core.Identity;

public sealed record SpaceSourceBindingRequest(string PublisherId, string AgentGrantId, IReadOnlyList<long> LocalSourceIds);

public sealed record SpacePublisherRequest(string LibraryId, string IncarnationId);
public sealed record SpaceRebaselineRequest(string LibraryId, string IncarnationId, string? Epoch = null,
    string AfterKind = "", string AfterKey = "", bool Complete = false);
public sealed record SpaceRebaselinePage(SpacePublisher Publisher, IReadOnlyList<SpaceEntityIdentity> Identities,
    bool HasMore, string AfterKind, string AfterKey);
public sealed record SpacePublisher(string PublisherId, string LibraryId, string IncarnationId, string Epoch, long LastSequence);
public sealed record SpaceEntityIdentity(string Kind, long LocalId, string GlobalId);
public sealed record SpaceSyncBatch(string PublisherId, string Epoch, long Sequence, string OperationId,
    string Kind, JsonElement Payload, IReadOnlyList<SpaceEntityIdentity> Identities);
public sealed record SpaceSyncReceipt(long Sequence, string OperationId, int Status, string Code, int Accepted);
public sealed record SpaceImageBatch(long SourceId, string SourcePath, IReadOnlyList<ImageRecord> Images);
public sealed record SpaceVideoBatch(long SourceId, string SourcePath, IReadOnlyList<VideoRecord> Videos);
public sealed record SpaceSourceBatch(long SourceId, string SourcePath);
public sealed record SpaceCatalogBatch(IReadOnlyList<Tag> Tags, IReadOnlyList<Album> Albums, IReadOnlyList<PersonSummary> People);
public sealed record SpaceMetadataBatch(GatewayMetadata Metadata);
public sealed record SpaceMetadataEditsBatch(IReadOnlyList<Picer.Core.Data.MetadataEdit> Edits);
public sealed record SpaceUploadScope(IReadOnlyList<long> SourceIds, bool Photos = true, bool Videos = true,
    bool People = false, bool PreciseLocation = false);
