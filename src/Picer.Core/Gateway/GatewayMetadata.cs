using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.Core.Gateway;

public sealed record GatewayAlbumImageLink(long AlbumId, long ImageId);
public sealed record GatewayImageTagLink(long ImageId, long TagId);
public sealed record GatewayMetadata(
    IReadOnlyList<GatewayAlbumImageLink> AlbumImages,
    IReadOnlyList<GatewayImageTagLink> ImageTags,
    IReadOnlyList<ImageLocationHierarchyRow> Locations);
