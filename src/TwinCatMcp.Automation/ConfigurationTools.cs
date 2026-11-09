using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TwinCatMcp.Automation.Models;
using TwinCatMcp.Safety;

namespace TwinCatMcp.Automation;

/// <summary>Engineering-project wrappers. Every mutation is audited and supports a policy-only dry run.</summary>
[McpServerToolType]
public static class ConfigurationTools
{
    [McpServerTool(Name = "list_twincat_projects"), Description("Lists TwinCAT .tsproj projects in the connected solution and the project selected for all system-manager automation tools.")]
    public static async Task<string> ListTwinCatProjects(XaeShellSession session, CancellationToken cancellationToken = default)
        => JsonSerializer.Serialize(await session.ListTwinCatProjectsAsync(cancellationToken));

    [McpServerTool(Name = "select_twincat_project"), Description("Selects the TwinCAT project used by tree, I/O, mappings, PLC and activation tools. Does not change the ADS runtime target. Audited; dryRun checks policy only.")]
    public static Task<string> SelectTwinCatProject(XaeShellSession session, SafetyGate safety,
        [Description("TwinCAT project name from list_twincat_projects.")] string name, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "SelectTwinCatProject", name, dryRun, () => session.SelectTwinCatProjectAsync(name, cancellationToken));

    [McpServerTool, Description("Creates an empty .sln in a new XAE session. Requires a disconnected session and refuses to overwrite a solution. Then call add_twincat_project. Audited; dryRun checks policy only.")]
    public static Task<string> CreateXaeSolution(XaeShellSession session, SafetyGate safety,
        [Description("Absolute path of the new .sln file.")] string solutionPath, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "CreateXaeSolution", solutionPath, dryRun, () => session.CreateXaeSolutionAsync(solutionPath, cancellationToken));

    [McpServerTool(Name = "add_twincat_project"), Description("Adds and selects a TwinCAT project in the connected solution using a local TwinCAT .tsproj template. Refuses a nonempty destination directory. Save the project afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> AddTwinCatProject(XaeShellSession session, SafetyGate safety, string name,
        [Description("Full path to the installed TwinCAT Project.tsproj template.")] string templatePath, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "AddTwinCatProject", name, dryRun, () => session.AddTwinCatProjectAsync(name, templatePath, cancellationToken));

    [McpServerTool, Description("Reads ProduceXml(false) for a tree item in the selected TwinCAT project. Use TIID for I/O, TIRS for real-time CPU settings, TIRT for tasks, TIRR for routes, TIRC for system settings, or an exact tree path. No ConsumeXml commands are issued.")]
    public static async Task<string> GetXaeTreeXml(XaeShellSession session, string treePath, CancellationToken cancellationToken = default)
        => JsonSerializer.Serialize(new { TreePath = treePath, Xml = await session.GetXaeTreeXmlAsync(treePath, cancellationToken) });

    [McpServerTool, Description("Reads the selected engineering project's target AMS Net ID. This is separate from the MCP ADS connection target.")]
    public static async Task<string> GetXaeTargetNetId(XaeShellSession session, CancellationToken cancellationToken = default)
        => JsonSerializer.Serialize(new { AmsNetId = await session.GetXaeTargetNetIdAsync(cancellationToken) });

    [McpServerTool, Description("Sets the engineering project's target AMS Net ID without downloading or changing the MCP ADS target. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> SetXaeTargetNetId(XaeShellSession session, SafetyGate safety, string amsNetId, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "SetXaeTargetNetId", amsNetId, dryRun, () => session.SetXaeTargetNetIdAsync(amsNetId, cancellationToken));

    [McpServerTool, Description("Sets the engineering project's compilation platform: 'TwinCAT OS (ARMV7-A)', 'TwinCAT OS (ARMV7-M)', 'TwinCAT OS (ARMV8-A)', 'TwinCAT OS (x64)', 'TwinCAT OS (x64-E)', 'TwinCAT RT (x64)' or 'TwinCAT RT (x86)'. Does not download. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> SetXaeTargetPlatform(XaeShellSession session, SafetyGate safety, string platform, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "SetXaeTargetPlatform", platform, dryRun, () => session.SetXaeTargetPlatformAsync(platform, cancellationToken));

    [McpServerTool, Description("Creates a configured EtherCAT master under TIID, or a TE1111 simulation device when simulation=true. Does not scan or activate hardware. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> CreateEthercatMaster(XaeShellSession session, SafetyGate safety, string name, bool simulation = false, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "CreateEthercatMaster", name, dryRun, () => session.CreateEtherCatMasterAsync(name, simulation, cancellationToken));

    [McpServerTool, Description("Creates an EtherCAT coupler/box/terminal under an existing master or coupler using the installed ESI product catalog. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> CreateEthercatBox(XaeShellSession session, SafetyGate safety, string parentTreePath, string name,
        [Description("ESI product name such as EL1008 or full revision EL1008-0000-0018.")] string productInfo,
        [Description("Optional sibling name to insert before; omitted appends.")] string? before = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "CreateEthercatBox", parentTreePath + "^" + name, dryRun, () => session.CreateEtherCatBoxAsync(parentTreePath, name, productInfo, before, cancellationToken));

    [McpServerTool, Description("Reads the configured network adapter binding of an Ethernet-based I/O device.")]
    public static async Task<string> GetDeviceAdapter(XaeShellSession session, string treePath, CancellationToken cancellationToken = default)
        => JsonSerializer.Serialize(await session.GetDeviceAdapterAsync(treePath, cancellationToken));

    [McpServerTool, Description("Sets the project's network adapter binding using DeviceDef/AddressInfo/Pnp; does not activate hardware. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> SetDeviceAdapter(XaeShellSession session, SafetyGate safety, string treePath, string description,
        [Description("NDIS adapter GUID, optionally prefixed with \\DEVICE\\.")] string deviceGuid,
        [Description("12 hexadecimal MAC digits without separators.")] string mac, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "SetDeviceAdapter", treePath, dryRun, () => session.SetDeviceAdapterAsync(treePath, description, deviceGuid, mac, cancellationToken));

    [McpServerTool, Description("Exports an EtherCAT master's ENI configuration to a new absolute file path. Refuses overwriting. Audited; dryRun checks policy only.")]
    public static Task<string> ExportEthercatEni(XaeShellSession session, SafetyGate safety, string treePath, string filePath, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "ExportEthercatEni", treePath + " -> " + filePath, dryRun, () => session.TransferEniAsync(treePath, filePath, false, cancellationToken));

    [McpServerTool, Description("Imports an existing ENI configuration file into a TE1111 EtherCAT simulation device in the project. Does not activate hardware. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> ImportEthercatEni(XaeShellSession session, SafetyGate safety, string treePath, string filePath, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "ImportEthercatEni", filePath + " -> " + treePath, dryRun, () => session.TransferEniAsync(treePath, filePath, true, cancellationToken));

    [McpServerTool, Description("Reads the selected TwinCAT project's VarLinks XML mapping information.")]
    public static async Task<string> GetMappingInfo(XaeShellSession session, CancellationToken cancellationToken = default)
        => JsonSerializer.Serialize(new { Xml = await session.GetMappingInfoAsync(cancellationToken) });

    [McpServerTool, Description("Applies VarLinks XML variable mappings to the engineering project. Build the PLC first so process-image symbols exist. XML must have a VarLinks root and no DTD/entities. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> ApplyMappingInfo(XaeShellSession session, SafetyGate safety, string xml, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "ApplyMappingInfo", "VarLinks", dryRun, () => session.ApplyMappingInfoAsync(xml, cancellationToken));

    [McpServerTool, Description("Links two process-image variables by exact tree path in the engineering project. Build the PLC first. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> LinkXaeVariables(XaeShellSession session, SafetyGate safety, string sourceTreePath, string targetTreePath, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "LinkXaeVariables", sourceTreePath + " -> " + targetTreePath, dryRun, () => session.LinkVariablesAsync(sourceTreePath, targetTreePath, false, cancellationToken));

    [McpServerTool, Description("Removes a mapping between two process-image variable tree paths in the engineering project. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> UnlinkXaeVariables(XaeShellSession session, SafetyGate safety, string sourceTreePath, string targetTreePath, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "UnlinkXaeVariables", sourceTreePath + " -> " + targetTreePath, dryRun, () => session.LinkVariablesAsync(sourceTreePath, targetTreePath, true, cancellationToken));

    [McpServerTool, Description("Runs TwinCAT.GenerateMappings with the selected TwinCAT project focused in Solution Explorer. The shell must expose that command; unavailable commands are reported as failures. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> GenerateXaeMappings(XaeShellSession session, SafetyGate safety, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "GenerateXaeMappings", "selected project", dryRun, () => session.RunMappingCommandAsync(false, cancellationToken));

    [McpServerTool, Description("Runs TwinCAT.CheckVariableLinks with the selected TwinCAT project focused. Reports command invocation, not a validation verdict; inspect build/errors afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> CheckXaeVariableLinks(XaeShellSession session, SafetyGate safety, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "CheckXaeVariableLinks", "selected project", dryRun, () => session.RunMappingCommandAsync(true, cancellationToken));

    [McpServerTool, Description("Creates a real-time task under TIRT, with or without process image. Does not change the running target. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> CreateRealtimeTask(XaeShellSession session, SafetyGate safety, string name, bool withImage = true, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "CreateRealtimeTask", name, dryRun, () => session.CreateRealtimeTaskAsync(name, withImage, cancellationToken));

    [McpServerTool, Description("Adds an input or output process-image variable to a real-time task. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> AddTaskImageVariable(XaeShellSession session, SafetyGate safety, string taskTreePath, string name, string dataType, bool output = false, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "AddTaskImageVariable", taskTreePath + "^" + name, dryRun, () => session.AddTaskImageVariableAsync(taskTreePath, name, dataType, output, cancellationToken));

    [McpServerTool, Description("Reads a real-time task's cycle time in microseconds, priority and CPU affinity from the project.")]
    public static async Task<string> GetXaeTaskSettings(XaeShellSession session, string treePath, CancellationToken cancellationToken = default)
        => JsonSerializer.Serialize(await session.GetTaskSettingsAsync(treePath, cancellationToken));

    [McpServerTool, Description("Updates specified real-time task settings in the project: positive cycleTimeUs, priority 1..255, zero-based coreId 0..63. Omitted settings are preserved. Does not change the running target. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> ConfigureXaeTask(XaeShellSession session, SafetyGate safety, string treePath, int? cycleTimeUs = null, int? priority = null, int? coreId = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "ConfigureXaeTask", treePath, dryRun, () => session.ConfigureTaskAsync(treePath, cycleTimeUs, priority, coreId, cancellationToken));

    [McpServerTool, Description("Updates project CPU counts and per-core base times. Each core has zero-based CoreId and positive BaseTimeUs. Does not execute SetOnTarget or change running CPU isolation. Read TIRS XML to inspect current settings; save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> ConfigureXaeRealtimeCores(XaeShellSession session, SafetyGate safety, int totalCores, int isolatedCores, CpuCoreConfiguration[] cores, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "ConfigureXaeRealtimeCores", "TIRS", dryRun, () => session.ConfigureRealtimeCoresAsync(totalCores, isolatedCores, cores, cancellationToken));

    [McpServerTool, Description("Adds a PLC project to the selected TwinCAT project using an installed template name. TwinCAT may create its default PlcTask. Save afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> AddPlcProject(XaeShellSession session, SafetyGate safety, string name, string template = "Standard PLC Template", bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "AddPlcProject", name, dryRun, () => session.AddPlcProjectAsync(name, template, cancellationToken));

    [McpServerTool, Description("Adds an installed direct library reference to a PLC project. Save and build afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> AddPlcLibrary(XaeShellSession session, SafetyGate safety, string plcName, string libraryName, string version = "*", string company = "", bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "AddPlcLibrary", plcName + ":" + libraryName, dryRun, () => session.ChangePlcReferenceAsync(plcName, libraryName, version, company, null, false, cancellationToken));

    [McpServerTool, Description("Adds a PLC library placeholder and its default library resolution. Save and build afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> AddPlcPlaceholder(XaeShellSession session, SafetyGate safety, string plcName, string placeholderName, string libraryName, string version = "*", string company = "", bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "AddPlcPlaceholder", plcName + ":" + placeholderName, dryRun, () => session.ChangePlcReferenceAsync(plcName, libraryName, version, company, placeholderName, false, cancellationToken));

    [McpServerTool, Description("Removes a PLC library or placeholder reference by name. Leave version/company empty for name-only removal; if supplied, they must match the reference exactly. Save and build afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> RemovePlcReference(XaeShellSession session, SafetyGate safety, string plcName, string libraryName, string version = "", string company = "", bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "RemovePlcReference", plcName + ":" + libraryName, dryRun, () => session.ChangePlcReferenceAsync(plcName, libraryName, version, company, null, true, cancellationToken));

    [McpServerTool, Description("Imports native .TcPOU/.TcGVL/.TcDUT files into an existing nested PLC project or folder using CreateChild subtype 58. Validates all files before importing; no PLCopen conversion. Save and build afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> ImportPlcSourceFiles(XaeShellSession session, SafetyGate safety, string parentTreePath, string[] filePaths, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "ImportPlcSourceFiles", parentTreePath, dryRun, () => session.ImportPlcFilesAsync(parentTreePath, filePaths, cancellationToken));

    [McpServerTool, Description("Adds a referenced task and program call under the nested PLC project. The real-time TIRT task must already exist; create_realtime_task with withImage=false first. Save and build afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> AddPlcTask(XaeShellSession session, SafetyGate safety, string plcName, string taskName, string programName, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "AddPlcTask", plcName + ":" + taskName, dryRun, () => session.AddPlcTaskAsync(plcName, taskName, programName, cancellationToken));

    [McpServerTool, Description("Sets PLC boot-project autostart and invokes GenerateBootProject(true). Can write boot files to the selected target; requires AllowDownloads. Build first. Audited; dryRun checks policy only.")]
    public static Task<string> SetPlcBootProject(XaeShellSession session, SafetyGate safety, string plcName, bool autostart, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "SetPlcBootProject", plcName, dryRun, () => session.SetPlcBootProjectAsync(plcName, autostart, cancellationToken));

    [McpServerTool, Description("Runs CheckAllObjects on the nested PLC project without downloading. Value contains the compiler's boolean verdict; Succeeded only reports completion of the invocation. Read get_build_errors for diagnostics. Audited; dryRun checks policy only.")]
    public static Task<string> CheckPlcObjects(XaeShellSession session, SafetyGate safety, string plcName, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "CheckPlcObjects", plcName, dryRun, () => session.CheckPlcObjectsAsync(plcName, cancellationToken));

    [McpServerTool, Description("Sets a parameter on a PLC library placeholder and verifies it by reading the project XML back. parameterName is the qualified constant name. Save and build afterwards. Audited; dryRun checks policy only.")]
    public static Task<string> SetPlcLibraryParameter(XaeShellSession session, SafetyGate safety, string plcName, string libraryItemName, string parameterName, string value, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(safety, "SetPlcLibraryParameter", plcName + ":" + libraryItemName + ":" + parameterName, dryRun, () => session.SetPlcLibraryParameterAsync(plcName, libraryItemName, parameterName, value, cancellationToken));

    private static async Task<string> Mutate<T>(SafetyGate safety, string operation, string subject, bool dryRun, Func<Task<T>> run)
    {
        var decision = safety.CheckAutomationOperation(operation, subject);
        if (!decision.IsAllowed || dryRun)
            return JsonSerializer.Serialize(new ConfigurationOperationResult(false, decision.IsAllowed, dryRun, decision.Reason, null, null));
        try
        {
            var result = await run();
            return JsonSerializer.Serialize(new ConfigurationOperationResult(true, true, false, decision.Reason, result, null));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A multi-step COM operation may already have applied part of the requested change.
            return JsonSerializer.Serialize(new ConfigurationOperationResult(false, false, false, decision.Reason, null, ex.Message, MayHaveApplied: true));
        }
    }
}
