namespace Picer.Core.Models;

public sealed record ImageLocationHierarchyRow(
    long ImageId,
    bool HasGps,
    string? Country,
    string? Province,
    string? City,
    string? County,
    string? District,
    string? Town,
    string? Village);
