namespace Picer.Core.Indexing;

public sealed record IndexProgress(string Phase, int Total, int Done, string CurrentFile);

public sealed record IndexResult(int Scanned, int Indexed, int Skipped, int MarkedMissing);
