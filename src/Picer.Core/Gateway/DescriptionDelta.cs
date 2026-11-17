namespace Picer.Core.Gateway;

public sealed record DescriptionDeltaCursor(string Epoch, string Scope, long Sequence);
public sealed record DescriptionDeltaRequest(DescriptionDeltaCursor? Cursor, long? Through = null);
public sealed record DescriptionDeltaEntry(long Sequence, bool Deleted, DescriptionVersion Version);
public sealed record DescriptionDeltaPage(DescriptionDeltaCursor Cursor, long After, long Through,
    IReadOnlyList<DescriptionDeltaEntry> Entries);
