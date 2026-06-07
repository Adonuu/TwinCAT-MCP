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

    [McpServerTool, Description("Lists the hardware/PLC configurations available in the open project's System Manager tree.")]
    public static async Task<string> ListHardwareConfigurations(XaeShellSession session, CancellationToken cancellationToken = default)
        => Serialize(await session.ListHardwareConfigurationsAsync(cancellationToken));

    [McpServerTool, Description("Activates a named hardware/PLC configuration — this downloads the configuration to the target and can briefly interrupt a running system. " +
        "High-impact: gated by the safety policy and always requires confirm=true. Pass dryRun=true to preview the decision.")]
    public static async Task<string> ActivateConfiguration(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Name of the configuration to activate, as reported by ListHardwareConfigurations.")] string name,
        [Description("If true, evaluate the safety decision and report it without activating anything. Default false.")] bool dryRun = false,
        [Description("Required — activation is always gated behind explicit confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckAutomationOperation("ActivateConfiguration", confirm);
        if (!decision.IsAllowed || dryRun)
            return Serialize(new AutomationOperationResult(Applied: false, Succeeded: false, Error: null, decision.Reason, Detail: null));

        return Serialize(await session.ActivateConfigurationAsync(name, decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Restarts the TwinCAT runtime on the target — 'Restart' performs a full cold restart, 'ReloadOnly' just reloads the active configuration. " +
        "Highest-impact automation operation: it interrupts whatever the runtime is currently doing. Gated by the safety policy and always requires confirm=true.")]
    public static async Task<string> RestartTwinCat(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Restart mode: 'Restart' (full cold restart) or 'ReloadOnly' (reload active configuration). Default 'Restart'.")] string mode = "Restart",
        [Description("If true, evaluate the safety decision and report it without restarting anything. Default false.")] bool dryRun = false,
        [Description("Required — restarting is always gated behind explicit confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckAutomationOperation("RestartTwinCat", confirm);
        if (!decision.IsAllowed || dryRun)
            return Serialize(new AutomationOperationResult(Applied: false, Succeeded: false, Error: null, decision.Reason, Detail: null));

        return Serialize(await session.RestartTwinCatAsync(mode, decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Browses the open project's PLC object tree (folders, POUs, GVLs, DUTs) starting from a tree path " +
        "(e.g. 'TIPC^MyPlcProject^MyPlcProject Project^POUs', as reported by a previous browse) or the PLC configuration root if omitted. " +
        "Returns a nested structure up to maxDepth levels — start shallow to orient, then drill into a specific subtree by path.")]
    public static async Task<string> GetProjectTree(
        XaeShellSession session,
        [Description("Tree path to start from. Omit to start from the PLC configuration root ('TIPC').")] string? treePath = null,
        [Description("How many levels of children to include below the starting node. Default 3.")] int maxDepth = 3,
        CancellationToken cancellationToken = default)
        => Serialize(await session.GetProjectTreeAsync(treePath, maxDepth, cancellationToken));

    [McpServerTool, Description("Creates a new POU/GVL/DUT/folder INSIDE the open project's tree via the Automation Interface. " +
        "Unlike the file-based CreatePou source tool, this registers the object with the IDE in the same step it creates it — correct " +
        "GUID/.xti sync, immediately visible to the IDE and build, no manual 'Add Existing Item' needed. " +
        "Gated by the safety policy — pass dryRun=true to preview without creating anything.")]
    public static async Task<string> CreatePlcObject(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Tree path of the parent folder/project to create the object under, e.g. 'TIPC^MyPlcProject^MyPlcProject Project^POUs'.")] string parentTreePath,
        [Description("The new object's name (must be a valid IEC 61131-3 identifier).")] string name,
        [Description("Object kind: 'Folder', 'Pou', 'Gvl', or 'Dut'.")] string kind,
        [Description("For kind='Pou' only: 'Program', 'Function', or 'FunctionBlock'. Defaults to 'Program'. Ignored for other kinds.")] string? pouType = null,
        [Description("If true, evaluate the safety decision and report it without creating anything. Default false.")] bool dryRun = false,
        [Description("Required if the safety policy says this needs confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckAutomationOperation("CreatePlcObject", confirm);
        if (!decision.IsAllowed || dryRun)
            return Serialize(new PlcObjectCreationResult(Applied: false, Succeeded: false, TreePath: null, Guid: null, decision.Reason, Error: null));

        return Serialize(await session.CreatePlcObjectAsync(parentTreePath, name, kind, pouType, decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Deletes a PLC object (POU/GVL/DUT/folder, with all its children) from the open project's tree. " +
        "Irreversible from the agent's perspective. High-impact: gated by the safety policy and always requires confirm=true. " +
        "Pass dryRun=true to preview the decision.")]
    public static async Task<string> DeletePlcObject(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Tree path of the object to delete, as reported by GetProjectTree, e.g. 'TIPC^MyPlcProject^MyPlcProject Project^POUs^OldPou'.")] string treePath,
        [Description("If true, evaluate the safety decision and report it without deleting anything. Default false.")] bool dryRun = false,
        [Description("Required — deletion is always gated behind explicit confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckAutomationOperation("DeletePlcObject", confirm);
        if (!decision.IsAllowed || dryRun)
            return Serialize(new AutomationOperationResult(Applied: false, Succeeded: false, Error: null, decision.Reason, Detail: null));

        return Serialize(await session.DeletePlcObjectAsync(treePath, decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Imports a previously-exported PLC object file into the project tree under the given parent — the supported way " +
        "to bring an object in from another project with correct registration. Gated by the safety policy.")]
    public static async Task<string> ImportPlcObject(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Tree path of the parent folder/project to import into, e.g. 'TIPC^MyPlcProject^MyPlcProject Project^POUs'.")] string parentTreePath,
        [Description("Full path to the export file to import.")] string exportFilePath,
        [Description("If true, evaluate the safety decision and report it without importing anything. Default false.")] bool dryRun = false,
        [Description("Required if the safety policy says this needs confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckAutomationOperation("ImportPlcObject", confirm);
        if (!decision.IsAllowed || dryRun)
            return Serialize(new AutomationOperationResult(Applied: false, Succeeded: false, Error: null, decision.Reason, Detail: null));

        return Serialize(await session.ImportPlcObjectAsync(parentTreePath, exportFilePath, decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Exports a PLC object from the project tree to a portable export file on disk — the supported way to move an " +
        "object to another project. Gated by the safety policy (it writes to disk, like a source edit, even though the project itself doesn't change).")]
    public static async Task<string> ExportPlcObject(
        XaeShellSession session,
        SafetyGate safety,
        [Description("Tree path of the object to export, as reported by GetProjectTree.")] string treePath,
        [Description("Full path to write the export file to.")] string exportFilePath,
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
