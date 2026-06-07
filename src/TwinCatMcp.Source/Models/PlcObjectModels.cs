namespace TwinCatMcp.Source.Models;

/// <summary>A lightweight index entry for a discovered POU/GVL/DUT file — enough to locate and identify it without parsing.</summary>
public sealed record PlcObjectSummary(
    string Name,
    PlcObjectKind Kind,
    string RelativePath,
    string Guid,
    DateTimeOffset LastModifiedUtc);

/// <summary>The full text content of a POU/GVL/DUT, as stored in its &lt;Declaration&gt; / &lt;Implementation&gt; CDATA blocks.</summary>
public sealed record PlcObjectSource(
    string Name,
    PlcObjectKind Kind,
    string RelativePath,
    string Guid,
    string Declaration,
    string? Implementation,
    string? ImplementationLanguage);

public sealed record SearchHit(
    string Name,
    PlcObjectKind Kind,
    string RelativePath,
    string Region,
    int LineNumber,
    string Snippet);

/// <summary>Result of a (possibly dry-run) edit to a PLC object's declaration or implementation text.</summary>
public sealed record EditResult(
    bool Changed,
    bool Applied,
    string RelativePath,
    string? DiffPreview,
    IReadOnlyList<string> Warnings);

public sealed record CreateResult(
    bool Created,
    string RelativePath,
    string Guid);
