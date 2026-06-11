using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TwinCatMcp.Automation.Models;
using TwinCatMcp.Safety;

namespace TwinCatMcp.Automation;

/// <summary>
/// MCP tools for driving the TwinCAT XAE Shell (the Visual-Studio-based engineering IDE) through its
/// COM-based Automation Interface — opening/building/cleaning a project, reading build errors, listing and
/// activating hardware configurations, and restarting the runtime.
///
/// Hard requirement: Windows + a local TwinCAT XAE Shell install (see <see cref="XaeShellSession"/> for
/// why this can't be remoted). On any other platform every tool here reports that plainly rather than
/// pretending to work — Source and Runtime tools remain fully available regardless.
///
/// Build/clean/read-only browsing are not allow-list-gated (they don't mutate the PLC project source —
/// see <see cref="SafetyGate.CheckAutomationOperation"/>), but SafeMode still blocks them, and
/// <c>ActivateConfiguration</c>/<c>RestartTwinCat</c> always require explicit confirmation: both can
/// disrupt a running physical system.
/// </summary>
[McpServerToolType]
public static class AutomationTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [McpServerTool, Description("Opens a TwinCAT XAE project (.sln or .tsproj) in the XAE Shell, ready for build/automation operations. " +
        "Requires Windows with a local TwinCAT XAE Shell install.")]
    public static async Task<string> OpenXaeProject(
        XaeShellSession session,
        [Description("Full path to the .sln or .tsproj file to open.")] string path,
        CancellationToken cancellationToken = default)
        => Serialize(await session.OpenProjectAsync(path, cancellationToken));

    [McpServerTool, Description("Closes the currently-open XAE project without saving.")]
    public static async Task<string> CloseProject(XaeShellSession session, CancellationToken cancellationToken = default)
        => Serialize(await session.CloseProjectAsync(cancellationToken));

    [McpServerTool, Description("Saves all pending changes in the open XAE project (project files and open documents). " +
        "IMPORTANT after CreatePlcObject/DeletePlcObject/ImportPlcObject: tree changes only persist to the .plcproj/.tsproj on a save " +
        "(Build and ActivateConfiguration save implicitly) — closing without one discards them. Gated by the safety policy.")]
    public static async Task<string> SaveProject(
        XaeShellSession session,
        SafetyGate safety,
        [Description("If true, evaluate the safety decision and report it without saving. Default false.")] bool dryRun = false,
        [Description("Required if the safety policy says this needs confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckAutomationOperation("SaveProject", confirm);
        if (!decision.IsAllowed || dryRun)
            return Serialize(new AutomationOperationResult(Applied: false, Succeeded: false, Error: null, decision.Reason, Detail: null));

        return Serialize(await session.SaveAllAsync(decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Reports whether a project is currently open in the XAE Shell, and which one.")]
    public static async Task<string> GetProjectStatus(XaeShellSession session, CancellationToken cancellationToken = default)
        => Serialize(await session.GetProjectStatusAsync(cancellationToken));

    [McpServerTool, Description("Builds the open XAE project (or one of its solution configurations) and reports the resulting error/warning list. " +
        "Gated by the safety policy — SafeMode blocks it, since a build can run code generation that touches the project tree.")]
    public static async Task<string> BuildProject(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Solution configuration to build, e.g. 'Release|TwinCAT RT (x64)'. Omit to build the active configuration.")] string? configuration = null,
        [Description("If true, evaluate the safety decision and report it without building. Default false.")] bool dryRun = false,
        [Description("Required if the safety policy says this needs confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
        => await GuardedBuildOperation(session, safety, "Build", configuration, dryRun, confirm,
            (s, cfg, ct) => s.BuildAsync(cfg, ct), cancellationToken);

    [McpServerTool, Description("Cleans the open XAE project (or one of its solution configurations). " +
        "Gated by the safety policy — SafeMode blocks it, like Build.")]
    public static async Task<string> CleanProject(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Solution configuration to clean, e.g. 'Release|TwinCAT RT (x64)'. Omit to clean the active configuration.")] string? configuration = null,
        [Description("If true, evaluate the safety decision and report it without cleaning. Default false.")] bool dryRun = false,
        [Description("Required if the safety policy says this needs confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
        => await GuardedBuildOperation(session, safety, "Clean", configuration, dryRun, confirm,
            (s, cfg, ct) => s.CleanAsync(cfg, ct), cancellationToken);

    [McpServerTool, Description("Reads the XAE Shell's current error list (from the last build) without triggering a new build.")]
    public static async Task<string> GetBuildErrors(XaeShellSession session, CancellationToken cancellationToken = default)
        => Serialize(await session.GetBuildErrorsAsync(cancellationToken));

    [McpServerTool, Description("Lists the I/O devices configured in the open project's 'I/O Configuration^I/O Devices' ('TIID') tree node, with their enabled/disabled state.")]
    public static async Task<string> ListIoDevices(XaeShellSession session, CancellationToken cancellationToken = default)
        => Serialize(await session.ListIoDevicesAsync(cancellationToken));

    [McpServerTool, Description("Activates the open project's current configuration (the IDE's 'Activate Configuration' / 'Save to Registry' command) — this downloads it to the " +
        "target and can briefly interrupt a running system. There is no concept of multiple named configurations to choose between; this always activates the project as it " +
        "currently stands. High-impact: gated by the safety policy and always requires confirm=true. Pass dryRun=true to preview the decision.")]
    public static async Task<string> ActivateConfiguration(
        XaeShellSession session,
        SafetyGate safety,
        [Description("If true, evaluate the safety decision and report it without activating anything. Default false.")] bool dryRun = false,
        [Description("Required — activation is always gated behind explicit confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckAutomationOperation("ActivateConfiguration", confirm);
        if (!decision.IsAllowed || dryRun)
            return Serialize(new AutomationOperationResult(Applied: false, Succeeded: false, Error: null, decision.Reason, Detail: null));

        return Serialize(await session.ActivateConfigurationAsync(decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Performs a full cold restart of the TwinCAT runtime on the target. " +
        "Highest-impact automation operation: it interrupts whatever the runtime is currently doing. Gated by the safety policy and always requires confirm=true.")]
    public static async Task<string> RestartTwinCat(
        XaeShellSession session,
        SafetyGate safety,
        [Description("If true, evaluate the safety decision and report it without restarting anything. Default false.")] bool dryRun = false,
        [Description("Required — restarting is always gated behind explicit confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckAutomationOperation("RestartTwinCat", confirm);
        if (!decision.IsAllowed || dryRun)
            return Serialize(new AutomationOperationResult(Applied: false, Succeeded: false, Error: null, decision.Reason, Detail: null));

        return Serialize(await session.RestartTwinCatAsync(decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Browses the open project's PLC object tree (folders, POUs, GVLs, DUTs) starting from a tree path " +
        "(e.g. 'TIPC^MyPlcProject^MyPlcProject Project^POUs', as reported by a previous browse) or the PLC configuration root if omitted. " +
        "Returns a nested structure up to maxDepth levels; every node carries the exact caret-delimited TreePath the mutation tools " +
        "(CreatePlcObject/DeletePlcObject/ReadPlcObjectCode/WritePlcObject*) accept — browse first, then copy paths from the result. " +
        "The nested IEC project node ('TIPC^<PlcProject>^<PlcProject> Project'), where POUs/GVLs/DUTs live and where CreatePlcObject " +
        "parents belong, is included in the walk even though the shell doesn't enumerate it as a child (visible from the root at maxDepth>=2; " +
        "the default maxDepth=3 also shows its top-level folders/objects).")]
    public static async Task<string> GetProjectTree(
        XaeShellSession session,
        [Description("Tree path to start from. Omit to start from the PLC configuration root ('TIPC').")] string? treePath = null,
        [Description("How many levels of children to include below the starting node. Default 3.")] int maxDepth = 3,
        CancellationToken cancellationToken = default)
        => Serialize(await session.GetProjectTreeAsync(treePath, maxDepth, cancellationToken));

    [McpServerTool, Description("Creates a new POU/GVL/DUT/folder INSIDE the open project's tree via the Automation Interface — the only " +
        "way to create PLC objects: a single call both writes the file and registers it with the IDE (correct GUID/.xti sync), immediately " +
        "visible to the IDE and build, no manual 'Add Existing Item' needed. " +
        "Gated by the safety policy — pass dryRun=true to preview without creating anything.")]
    public static async Task<string> CreatePlcObject(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Tree path of the parent folder/project to create the object under, e.g. 'TIPC^MyPlcProject^MyPlcProject Project^POUs'.")] string parentTreePath,
        [Description("The new object's name (must be a valid IEC 61131-3 identifier).")] string name,
        [Description("Object kind: 'Folder', 'Pou', 'Gvl', or 'Dut'.")] string kind,
        [Description("For kind='Pou' only: 'Program', 'Function', or 'FunctionBlock'. Defaults to 'Program'. Ignored for other kinds.")] string? pouType = null,
        [Description("For pouType='Function' only: the function's return type (a PLC data type, e.g. 'BOOL', 'DINT', 'LREAL'). Defaults to 'BOOL'. Ignored otherwise.")] string? returnType = null,
        [Description("If true, validate that parentTreePath resolves (read-only LookupTreeItem) and report the safety decision, without creating anything. Default false.")] bool dryRun = false,
        [Description("Required if the safety policy says this needs confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckAutomationOperation("CreatePlcObject", confirm);
        if (dryRun)
        {
            // Read-only probe: a dry run should answer "would this parent path work?", not just echo
            // the safety verdict — path discovery is the hard part of using this tool.
            var parent = await session.ValidateTreePathAsync(parentTreePath, cancellationToken);
            return Serialize(new PlcObjectCreationResult(Applied: false, Succeeded: parent.Exists, parent.TreePath, Guid: null, decision.Reason,
                Error: parent.Exists ? null : $"Parent tree path did not resolve: {parent.Error} Browse with GetProjectTree to find a valid path."));
        }

        if (!decision.IsAllowed)
            return Serialize(new PlcObjectCreationResult(Applied: false, Succeeded: false, TreePath: null, Guid: null, decision.Reason, Error: null));

        return Serialize(await session.CreatePlcObjectAsync(parentTreePath, name, kind, pouType, returnType, decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Reads the declaration and implementation (ST) source of a PLC object (POU/GVL/DUT, or a method/property/action " +
        "child item) from the open XAE session — the IDE's live in-memory text, which is the source of truth while a project is open. " +
        "PREFER this over the file-based ReadPouSource whenever an XAE session has the project open: on-disk .TcPOU files can be stale " +
        "until the IDE saves. Implementation is null for GVLs/DUTs (declaration-only).")]
    public static async Task<string> ReadPlcObjectCode(
        XaeShellSession session,
        [Description("Tree path of the object, as reported by GetProjectTree, e.g. 'TIPC^MyPlcProject^MyPlcProject Project^POUs^FB_Sample'.")] string treePath,
        CancellationToken cancellationToken = default)
        => Serialize(await session.ReadPlcObjectCodeAsync(treePath, cancellationToken));

    [McpServerTool, Description("Replaces the declaration block (e.g. 'FUNCTION_BLOCK ...' header plus VAR sections) of a PLC object through " +
        "the open XAE session's Automation Interface — the change lands in the IDE's in-memory project, exactly as if typed in the editor. " +
        "PREFER this over the file-based WritePouDeclaration whenever an XAE session has the project open: direct file edits are invisible " +
        "to the IDE and get overwritten on its next save. Write the COMPLETE block including the header line, matching what ReadPlcObjectCode " +
        "returns. REMEMBER: call SaveProject afterwards to persist to disk, and BuildProject to validate. Pass dryRun=true to preview the " +
        "safety decision and get the current text back without changing anything. Gated by the safety policy and requires confirm=true by default.")]
    public static async Task<string> WritePlcObjectDeclaration(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Tree path of the object, as reported by GetProjectTree.")] string treePath,
        [Description("The complete new declaration text, replacing the existing block verbatim.")] string newDeclarationText,
        [Description("If true, validate the tree path and return the current text without writing — read-only, so it works even before confirm. Default false.")] bool dryRun = false,
        [Description("Required if the safety policy says this needs confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        // A dry run never mutates (it resolves the path and returns the current text), so it proceeds
        // regardless of the safety verdict — the verdict still rides along in SafetyReason so the
        // caller learns whether the real write would be allowed.
        var decision = safety.CheckAutomationOperation("WritePlcObjectDeclaration", confirm);
        if (!dryRun && !decision.IsAllowed)
            return Serialize(new PlcCodeWriteResult(Applied: false, Succeeded: false, treePath, PreviousText: null, decision.Reason, Error: null));

        return Serialize(await session.WritePlcObjectCodeAsync(treePath, implementation: false, newDeclarationText, dryRun, decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Replaces the implementation body (the executable ST code) of a POU through the open XAE session's Automation " +
        "Interface — the change lands in the IDE's in-memory project, exactly as if typed in the editor. GVLs and DUTs have no implementation " +
        "and will be rejected. PREFER this over the file-based WritePouImplementation whenever an XAE session has the project open: direct " +
        "file edits are invisible to the IDE and get overwritten on its next save. REMEMBER: call SaveProject afterwards to persist to disk, " +
        "and BuildProject to validate. Pass dryRun=true to preview the safety decision and get the current text back without changing anything. " +
        "Gated by the safety policy and requires confirm=true by default.")]
    public static async Task<string> WritePlcObjectImplementation(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Tree path of the POU, as reported by GetProjectTree.")] string treePath,
        [Description("The complete new implementation source text, replacing the existing body verbatim.")] string newImplementationText,
        [Description("If true, validate the tree path and return the current text without writing — read-only, so it works even before confirm. Default false.")] bool dryRun = false,
        [Description("Required if the safety policy says this needs confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        // Same read-only dry-run contract as WritePlcObjectDeclaration.
        var decision = safety.CheckAutomationOperation("WritePlcObjectImplementation", confirm);
        if (!dryRun && !decision.IsAllowed)
            return Serialize(new PlcCodeWriteResult(Applied: false, Succeeded: false, treePath, PreviousText: null, decision.Reason, Error: null));

        return Serialize(await session.WritePlcObjectCodeAsync(treePath, implementation: true, newImplementationText, dryRun, decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Deletes a PLC object (POU/GVL/DUT/folder, with all its children) from the open project's tree. " +
        "Irreversible from the agent's perspective. High-impact: gated by the safety policy and always requires confirm=true. " +
        "Pass dryRun=true to preview the decision.")]
    public static async Task<string> DeletePlcObject(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Tree path of the object to delete, as reported by GetProjectTree, e.g. 'TIPC^MyPlcProject^MyPlcProject Project^POUs^OldPou'.")] string treePath,
        [Description("If true, validate that treePath resolves (read-only LookupTreeItem) and report the safety decision, without deleting anything. Default false.")] bool dryRun = false,
        [Description("Required — deletion is always gated behind explicit confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckAutomationOperation("DeletePlcObject", confirm);
        if (dryRun)
        {
            var target = await session.ValidateTreePathAsync(treePath, cancellationToken);
            return Serialize(new AutomationOperationResult(Applied: false, Succeeded: target.Exists,
                Error: target.Exists ? null : $"Tree path did not resolve: {target.Error}",
                decision.Reason,
                Detail: target.Exists ? $"Would delete '{target.TreePath}' ({target.Kind})." : null));
        }

        if (!decision.IsAllowed)
            return Serialize(new AutomationOperationResult(Applied: false, Succeeded: false, Error: null, decision.Reason, Detail: null));

        return Serialize(await session.DeletePlcObjectAsync(treePath, decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Imports a previously-exported PLCopen XML file into the PLC project — the supported way " +
        "to bring an object in from another project with correct registration. Imported objects land at the paths recorded " +
        "in the file (relative to the PLC project root), not under the given parent. Gated by the safety policy.")]
    public static async Task<string> ImportPlcObject(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Tree path identifying the target PLC project (any path inside it works), e.g. 'TIPC^MyPlcProject^MyPlcProject Project'.")] string parentTreePath,
        [Description("Full path to the PLCopen XML file to import (from a previous ExportPlcObject).")] string exportFilePath,
        [Description("If true, evaluate the safety decision and report it without importing anything. Default false.")] bool dryRun = false,
        [Description("Required if the safety policy says this needs confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckAutomationOperation("ImportPlcObject", confirm);
        if (!decision.IsAllowed || dryRun)
            return Serialize(new AutomationOperationResult(Applied: false, Succeeded: false, Error: null, decision.Reason, Detail: null));

        return Serialize(await session.ImportPlcObjectAsync(parentTreePath, exportFilePath, decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Exports a PLC object (POU/GVL/DUT/folder) from the project tree to a PLCopen XML file on disk — the supported " +
        "way to move an object to another project. Gated by the safety policy (it writes to disk, like a source edit, even though the project itself doesn't change).")]
    public static async Task<string> ExportPlcObject(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Tree path of the object to export, as reported by GetProjectTree.")] string treePath,
        [Description("Full path to write the PLCopen XML export to (conventionally '.xml').")] string exportFilePath,
        [Description("If true, evaluate the safety decision and report it without exporting anything. Default false.")] bool dryRun = false,
        [Description("Required if the safety policy says this needs confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckAutomationOperation("ExportPlcObject", confirm);
        if (!decision.IsAllowed || dryRun)
            return Serialize(new AutomationOperationResult(Applied: false, Succeeded: false, Error: null, decision.Reason, Detail: null));

        return Serialize(await session.ExportPlcObjectAsync(treePath, exportFilePath, decision.Reason, cancellationToken));
    }

    private static async Task<string> GuardedBuildOperation(
        XaeShellSession session,
        SafetyGate safety,
        string operationName,
        string? configuration,
        bool dryRun,
        bool confirm,
        Func<XaeShellSession, string?, CancellationToken, Task<BuildResult>> run,
        CancellationToken cancellationToken)
    {
        var decision = safety.CheckAutomationOperation(operationName, confirm);
        if (!decision.IsAllowed || dryRun)
            return Serialize(new { Applied = false, decision.Reason, Result = (BuildResult?)null });

        var result = await run(session, configuration, cancellationToken);
        return Serialize(new { Applied = true, decision.Reason, Result = result });
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
