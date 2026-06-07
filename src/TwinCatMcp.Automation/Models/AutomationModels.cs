namespace TwinCatMcp.Automation.Models;

/// <summary>Result of opening (or reporting on) the XAE Shell project.</summary>
public sealed record ProjectInfo(bool Open, string? Path, string? FullName);

/// <summary>Outcome of a build or clean operation.</summary>
public sealed record BuildResult(bool Succeeded, int ErrorCount, int WarningCount, IReadOnlyList<BuildErrorInfo> Errors);

/// <summary>One build error/warning entry as reported by the IDE's error list.</summary>
public sealed record BuildErrorInfo(string Severity, string Description, string? Project, string? FileName, int Line);

/// <summary>One named hardware/PLC configuration available for activation.</summary>
public sealed record HardwareConfiguration(string Name, bool IsActive);

/// <summary>Outcome of a gated automation operation — including the <see cref="TwinCatMcp.Safety.SafetyDecision"/> that gated it.</summary>
public sealed record AutomationOperationResult(bool Applied, bool Succeeded, string? Error, string SafetyReason, string? Detail);

/// <summary>One node in a PLC project's object tree (folder, POU, GVL, DUT, ...), with its children.</summary>
public sealed record PlcTreeNode(string Name, string TreePath, string Kind, IReadOnlyList<PlcTreeNode> Children);

/// <summary>
/// Outcome of creating a new PLC object (POU/GVL/DUT/folder) directly in the project tree via the
/// Automation Interface — unlike the file-based <c>CreatePou</c> tool, a successful creation here means
/// the object is already registered with the IDE (correct GUID/.xti sync), so <see cref="TreePath"/> and
/// <see cref="Guid"/> reflect the real, IDE-recognized object.
/// </summary>
public sealed record PlcObjectCreationResult(bool Applied, bool Succeeded, string? TreePath, string? Guid, string SafetyReason, string? Error);
