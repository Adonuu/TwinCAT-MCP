namespace TwinCatMcp.Automation.Models;

/// <summary>Result of opening (or reporting on) the XAE Shell project.</summary>
public sealed record ProjectInfo(bool Open, string? Path, string? FullName);

/// <summary>Outcome of a build or clean operation.</summary>
public sealed record BuildResult(bool Succeeded, int ErrorCount, int WarningCount, IReadOnlyList<BuildErrorInfo> Errors);

/// <summary>One build error/warning entry as reported by the IDE's error list.</summary>
public sealed record BuildErrorInfo(string Severity, string Description, string? Project, string? FileName, int Line);

/// <summary>One I/O device under the project's "I/O Configuration^I/O Devices" ('TIID') tree node.</summary>
public sealed record IoDevice(string Name, bool Enabled);

/// <summary>Outcome of a gated automation operation — including the <see cref="TwinCatMcp.Safety.SafetyDecision"/> that gated it.</summary>
public sealed record AutomationOperationResult(bool Applied, bool Succeeded, string? Error, string SafetyReason, string? Detail);

/// <summary>One node in a PLC project's object tree (folder, POU, GVL, DUT, ...), with its children.</summary>
public sealed record PlcTreeNode(string Name, string TreePath, string Kind, IReadOnlyList<PlcTreeNode> Children);

/// <summary>
/// Outcome of creating a new PLC object (POU/GVL/DUT/folder) directly in the project tree via the
/// Automation Interface — a successful creation here means the object is already registered with the
/// IDE (correct GUID/.xti sync), so <see cref="TreePath"/> and <see cref="Guid"/> reflect the real,
/// IDE-recognized object.
/// </summary>
public sealed record PlcObjectCreationResult(bool Applied, bool Succeeded, string? TreePath, string? Guid, string SafetyReason, string? Error);

/// <summary>
/// Result of resolving a tree path via LookupTreeItem without touching anything — used by mutation
/// tools' dryRun mode so a caller can validate a path before committing to a real, gated call.
/// </summary>
public sealed record TreePathValidation(bool Exists, string? TreePath, string? Kind, string? Error);

/// <summary>
/// Declaration/implementation text of a PLC tree item, read live from the IDE's in-memory project —
/// the source of truth while a session is open. <see cref="Implementation"/> is null for items that
/// have no executable body (GVLs/DUTs are declaration-only).
/// </summary>
public sealed record PlcObjectCodeResult(bool Succeeded, string TreePath, string? Kind, string? Declaration, string? Implementation, string? Error);

/// <summary>
/// Outcome of writing declaration/implementation text through the Automation Interface.
/// <see cref="PreviousText"/> carries the text as it was before the write (or, on dryRun, the current
/// text) so every write is self-documenting and recoverable from the transcript.
/// </summary>
public sealed record PlcCodeWriteResult(bool Applied, bool Succeeded, string TreePath, string? PreviousText, string SafetyReason, string? Error);
