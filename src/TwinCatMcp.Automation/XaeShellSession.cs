using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using TwinCatMcp.Automation.Models;

namespace TwinCatMcp.Automation;

/// <summary>
/// Owns the lifecycle of a single TwinCAT XAE Shell instance, driven through its COM-based Automation
/// Interface (<c>DTE</c> + <c>ITcSysManager</c>).
///
/// This is COM late-binding via <c>dynamic</c> rather than a compile-time reference to the <c>EnvDTE</c>/
/// <c>EnvDTE80</c>/<c>TcatSysManagerLib</c> interop assemblies — those ship with the local TwinCAT/Visual
/// Studio install rather than NuGet, so a hard reference would make this project (and the whole solution)
/// fail to restore/build anywhere except a fully-provisioned Windows engineering workstation. Every COM
/// touch happens inside <see cref="StaThreadDispatcher.RunAsync{T}"/> — see that type for why.
///
/// Hard Windows-only requirement: <see cref="EnsureSupportedPlatform"/> fails fast with a clear message
/// everywhere else, so the rest of the server (Source/Runtime tools, which are portable) keeps working.
/// </summary>
public sealed class XaeShellSession : IAsyncDisposable
{
    private readonly StaThreadDispatcher _dispatcher;
    private readonly AutomationOptions _options;
    private readonly ILogger<XaeShellSession> _logger;

    private dynamic? _dte;
    private object? _sysManager;
    private string? _openProjectPath;

    public XaeShellSession(StaThreadDispatcher dispatcher, IOptions<AutomationOptions> options, ILogger<XaeShellSession> logger)
    {
        _dispatcher = dispatcher;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsProjectOpen => _openProjectPath is not null;

    /// <summary>
    /// Raised (on the STA thread) with the opened solution's directory after a successful
    /// <see cref="OpenProjectAsync"/> — lets the server re-point the file-based source index at the
    /// project actually being worked on (wired up in Program.cs; Automation doesn't reference Source).
    /// </summary>
    public event Action<string>? ProjectOpened;

    public Task<ProjectInfo> OpenProjectAsync(string path, CancellationToken ct) =>
        RunAsync(() =>
        {
            var dte = GetOrCreateDte();
            dte.MainWindow.Visible = _options.ShowIde;
            RetryIfComBusy(() => dte.Solution.Open(path));
            _openProjectPath = path;
            _sysManager = null; // re-resolved lazily once a project is open — see GetOrCreateSysManager

            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } solutionDir)
                ProjectOpened?.Invoke(solutionDir);

            string? fullName = TryGetString(() => (string)dte.Solution.FullName);
            return new ProjectInfo(Open: true, path, fullName);
        });

    public Task<ProjectInfo> CloseProjectAsync(CancellationToken ct) =>
        RunAsync(() =>
        {
            if (_dte is { } dte && _openProjectPath is { } path)
            {
                RetryIfComBusy(() => dte.Solution.Close(SaveFirst: false));
                var closed = new ProjectInfo(Open: false, path, FullName: null);
                _openProjectPath = null;
                _sysManager = null;
                return closed;
            }

            return new ProjectInfo(Open: false, Path: null, FullName: null);
        });

    public Task<ProjectInfo> GetProjectStatusAsync(CancellationToken ct) =>
        RunAsync(() => new ProjectInfo(
            Open: _openProjectPath is not null,
            Path: _openProjectPath,
            FullName: _dte is { } dte && _openProjectPath is not null ? TryGetString(() => (string)dte.Solution.FullName) : null));

    public Task<AutomationOperationResult> SaveAllAsync(string safetyReason, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                // Tree mutations (CreateChild/DeleteChild) update the IDE's in-memory project; the
                // .plcproj/.tsproj on disk only follow on a save (builds and configuration
                // activation save implicitly). Without an explicit save, CloseProject discards the
                // structural changes while the created/deleted files on disk stay — leaving the
                // project referencing deleted objects or orphaning new ones.
                var dte = GetOrCreateDte();
                RetryIfComBusy(() => dte.ExecuteCommand("File.SaveAll"));
                return new AutomationOperationResult(Applied: true, Succeeded: true, Error: null, safetyReason, "Saved all open documents and project files.");
            }
            catch (Exception ex)
            {
                return new AutomationOperationResult(Applied: true, Succeeded: false, ex.Message, safetyReason, Detail: null);
            }
        });

    public Task<BuildResult> BuildAsync(string? configuration, CancellationToken ct) =>
        RunAsync(() => RunBuildOperation(build: true, configuration));

    public Task<BuildResult> CleanAsync(string? configuration, CancellationToken ct) =>
        RunAsync(() => RunBuildOperation(build: false, configuration));

    public Task<BuildResult> GetBuildErrorsAsync(CancellationToken ct) =>
        RunAsync(() => new BuildResult(Succeeded: true, 0, 0, ReadErrorList()));

    public Task<IReadOnlyList<IoDevice>> ListIoDevicesAsync(CancellationToken ct) =>
        RunAsync(() =>
        {
            var sysManager = GetOrCreateSysManager();
            var devices = (IReadOnlyList<IoDevice>?)null;

            // "TIID" is Beckhoff's documented LookupTreeItem shortcut for "I/O Configuration^I/O Devices" —
            // its children are the configured I/O devices, each with a Name and a Disabled flag. This
            // degrades to an empty list with a logged warning rather than throwing, keeping read-only
            // browsing resilient to shell/version differences.
            try
            {
                var ioDevices = new List<IoDevice>();
                var root = ComInvoke(sysManager, "LookupTreeItem", "TIID")!;
                var childCount = Convert.ToInt32(ComGet(root, "ChildCount"));
                for (var i = 1; i <= childCount; i++)
                {
                    var child = ComGetIndexed(root, "Child", i)!;
                    var disabled = (bool)ComGet(child, "Disabled")!;
                    ioDevices.Add(new IoDevice((string)ComGet(child, "Name")!, Enabled: !disabled));
                }
                devices = ioDevices;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not enumerate I/O devices from ITcSysManager — returning an empty list.");
            }

            return devices ?? [];
        });

    public Task<AutomationOperationResult> ActivateConfigurationAsync(string safetyReason, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                // ITcSysManager::ActivateConfiguration() takes no arguments — it activates the project's
                // current configuration (the IDE's "Activate Configuration" / "Save to Registry" command).
                // There is no documented concept of multiple named configurations to choose between.
                var sysManager = GetOrCreateSysManager();
                ComInvoke(sysManager, "ActivateConfiguration");
                return new AutomationOperationResult(Applied: true, Succeeded: true, Error: null, safetyReason, "Activated the project's current configuration.");
            }
            catch (Exception ex)
            {
                return new AutomationOperationResult(Applied: true, Succeeded: false, ex.Message, safetyReason, Detail: null);
            }
        });

    public Task<AutomationOperationResult> RestartTwinCatAsync(string safetyReason, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                // ITcSysManager::StartRestartTwinCAT() takes no arguments — it starts/restarts the
                // TwinCAT runtime (full cold restart). There is no documented "reload only" variant in
                // the Automation Interface.
                var sysManager = GetOrCreateSysManager();
                ComInvoke(sysManager, "StartRestartTwinCAT");
                return new AutomationOperationResult(Applied: true, Succeeded: true, Error: null, safetyReason, "Requested a TwinCAT restart.");
            }
            catch (Exception ex)
            {
                return new AutomationOperationResult(Applied: true, Succeeded: false, ex.Message, safetyReason, Detail: null);
            }
        });

    // PLC object subtype constants for ITcSmTreeItem.CreateChild's nSubType parameter — confirmed
    // against Beckhoff's InfoSys "Accessing, creating and handling PLC POUs" reference table
    // (infosys.beckhoff.com/content/1033/tc3_automationinterface/242732427.html). The Automation
    // Interface does not expose these as a named enum; they are plain integer literals.
    private const int SubTypeFolder = 601;
    private const int SubTypeProgram = 602;
    private const int SubTypeFunction = 603;
    private const int SubTypeFunctionBlock = 604;
    private const int SubTypeDutStruct = 606;
    private const int SubTypeGvl = 615;

    // IECLanguageTypes.IECLANGUAGE_ST — the implementation-language constant CreateChild expects as its
    // vInfo parameter for new POUs (infosys.beckhoff.com/content/1033/tc3_automationinterface/242861707.html).
    // Structured Text is the only language this server generates/edits.
    private const int IecLanguageStructuredText = 1;

    public Task<PlcTreeNode?> GetProjectTreeAsync(string? treePath, int maxDepth, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                // "TIPC" is Beckhoff's documented LookupTreeItem shortcut for the PLC configuration
                // node — the same convention as the "TIID" lookup in ListIoDevicesAsync. Exact child
                // navigation below a given path is otherwise driven entirely by what LookupTreeItem
                // returns, so any valid PathName reported by a previous browse can be passed back in.
                var sysManager = GetOrCreateSysManager();
                var root = ComInvoke(sysManager, "LookupTreeItem", string.IsNullOrWhiteSpace(treePath) ? "TIPC" : treePath)!;
                return WalkTree(sysManager, root, Math.Max(0, maxDepth));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not browse the PLC project tree at '{TreePath}'.", treePath ?? "TIPC");
                return null;
            }
        });

    public Task<PlcObjectCreationResult> CreatePlcObjectAsync(
        string parentTreePath, string name, string kind, string? pouType, string? returnType, string safetyReason, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                var subType = ResolveSubType(kind, pouType);
                var parent = ComInvoke(GetOrCreateSysManager(), "LookupTreeItem", parentTreePath)!;

                // vInfo is CreateChild's optional VARIANT parameter, per Beckhoff's "Accessing,
                // creating and handling PLC POUs" reference: the IEC implementation language for
                // programs/function blocks; for functions a two-element array of [language, return
                // type] — both required there; "not provided" (VT_EMPTY via null) for kinds that
                // don't take one, where an explicit 0 could be misread as a language/template selector.
                object? vInfo = subType switch
                {
                    SubTypeFunction => new object[] { IecLanguageStructuredText, returnType ?? "BOOL" },
                    SubTypeProgram or SubTypeFunctionBlock => IecLanguageStructuredText,
                    _ => null,
                };

                // A single CreateChild call both writes the file and registers it with the IDE
                // (correct GUID/.xti sync) — which is why object creation goes through the
                // Automation Interface rather than writing template files to disk.
                var child = ComInvoke(parent, "CreateChild", name, subType, "", vInfo)!;
                var newTreePath = TryGetString(() => (string)ComGet(child, "PathName")!) ?? $"{parentTreePath}^{name}";
                var guid = TryGetGuidFromXml(child);

                return new PlcObjectCreationResult(Applied: true, Succeeded: true, newTreePath, guid, safetyReason, Error: null);
            }
            catch (Exception ex)
            {
                return new PlcObjectCreationResult(Applied: true, Succeeded: false, TreePath: null, Guid: null, safetyReason, ex.Message);
            }
        });

    /// <summary>
    /// Resolves a tree path read-only (LookupTreeItem) and reports what it found — the COM error text
    /// on a miss is surfaced verbatim, since it often hints at the valid sibling/parent names.
    /// </summary>
    public Task<TreePathValidation> ValidateTreePathAsync(string treePath, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                var item = ComInvoke(GetOrCreateSysManager(), "LookupTreeItem", treePath)!;
                var resolvedPath = TryGetString(() => (string)ComGet(item, "PathName")!) ?? treePath;
                var kind = DescribeSubType(TryGetInt(() => Convert.ToInt32(ComGet(item, "ItemSubType"))));
                return new TreePathValidation(Exists: true, resolvedPath, kind, Error: null);
            }
            catch (Exception ex)
            {
                return new TreePathValidation(Exists: false, TreePath: null, Kind: null, ex.Message);
            }
        });

    public Task<PlcObjectCodeResult> ReadPlcObjectCodeAsync(string treePath, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                var item = ComInvoke(GetOrCreateSysManager(), "LookupTreeItem", treePath)!;
                var kind = DescribeSubType(TryGetInt(() => Convert.ToInt32(ComGet(item, "ItemSubType"))));

                // The cast/'as' on the RCW is a QueryInterface — it decides what the item supports:
                // POUs (and their method/property/action children) expose both aspects, GVLs/DUTs only
                // the declaration, folders neither.
                var declaration = item is ITcPlcDeclaration decl
                    ? RetryIfComBusy(() => decl.DeclarationText) : null;
                var implementation = item is ITcPlcImplementation impl
                    ? RetryIfComBusy(() => impl.ImplementationText) : null;

                if (declaration is null && implementation is null)
                    return new PlcObjectCodeResult(Succeeded: false, treePath, kind, Declaration: null, Implementation: null,
                        Error: $"'{treePath}' ({kind}) exposes neither a declaration nor an implementation — folders and non-IEC items have no code.");

                return new PlcObjectCodeResult(Succeeded: true, treePath, kind, declaration, implementation, Error: null);
            }
            catch (Exception ex)
            {
                return new PlcObjectCodeResult(Succeeded: false, treePath, Kind: null, Declaration: null, Implementation: null, ex.Message);
            }
        });

    public Task<PlcCodeWriteResult> WritePlcObjectCodeAsync(
        string treePath, bool implementation, string newText, bool dryRun, string safetyReason, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                var item = ComInvoke(GetOrCreateSysManager(), "LookupTreeItem", treePath)!;
                string previous;
                if (!implementation)
                {
                    if (item is not ITcPlcDeclaration decl)
                        return Fail($"'{treePath}' has no declaration part — is it a folder?");
                    previous = RetryIfComBusy(() => decl.DeclarationText);
                    if (!dryRun)
                        RetryIfComBusy(() => decl.DeclarationText = newText);
                }
                else
                {
                    if (item is not ITcPlcImplementation impl)
                        return Fail($"'{treePath}' has no implementation part — GVLs/DUTs/folders are declaration-only.");
                    previous = RetryIfComBusy(() => impl.ImplementationText);
                    if (!dryRun)
                        RetryIfComBusy(() => impl.ImplementationText = newText);
                }

                return new PlcCodeWriteResult(Applied: !dryRun, Succeeded: true, treePath, previous, safetyReason, Error: null);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }

            PlcCodeWriteResult Fail(string error) =>
                new(Applied: false, Succeeded: false, treePath, PreviousText: null, safetyReason, error);
        });

    public Task<AutomationOperationResult> DeletePlcObjectAsync(string treePath, string safetyReason, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                // ITcSmTreeItem has no "Delete" member on the item itself — per Beckhoff's
                // Automation Interface, the parent's DeleteChild(name) removes a child by name.
                var item = ComInvoke(GetOrCreateSysManager(), "LookupTreeItem", treePath)!;
                var parent = ComGet(item, "Parent")!;
                var itemName = (string)ComGet(item, "Name")!;
                ComInvoke(parent, "DeleteChild", itemName);
                return new AutomationOperationResult(Applied: true, Succeeded: true, Error: null, safetyReason, $"Deleted '{treePath}'.");
            }
            catch (Exception ex)
            {
                return new AutomationOperationResult(Applied: true, Succeeded: false, ex.Message, safetyReason, Detail: null);
            }
        });

    public Task<AutomationOperationResult> ImportPlcObjectAsync(
        string parentTreePath, string exportFilePath, string safetyReason, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                // PLCopen XML import on the nested IEC project (see ExportPlcObjectAsync for why the
                // PLCopen route). ITcPlcIECProject::PlcOpenImport(bstrFile, options, bstrSelection,
                // bSubTree): options 0 = none (a name conflict fails instead of silently renaming),
                // empty selection = everything in the file, bSubTree true = keep folder structure —
                // imported objects land at the paths recorded in the file, relative to the project root.
                var (projectPath, _) = SplitPlcProjectPath(parentTreePath);
                var iecProject = ComInvoke(GetOrCreateSysManager(), "LookupTreeItem", projectPath)!;
                ComInvoke(iecProject, "PlcOpenImport", exportFilePath, 0, "", true);
                return new AutomationOperationResult(Applied: true, Succeeded: true, Error: null, safetyReason,
                    $"Imported '{exportFilePath}' into PLC project '{projectPath}' (objects land at the paths recorded in the file).");
            }
            catch (Exception ex)
            {
                return new AutomationOperationResult(Applied: true, Succeeded: false, ex.Message, safetyReason, Detail: null);
            }
        });

    public Task<AutomationOperationResult> ExportPlcObjectAsync(
        string treePath, string exportFilePath, string safetyReason, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                // PLC objects (POUs/GVLs/DUTs/folders) export as PLCopen XML via the nested IEC
                // project's PlcOpenExport(bstrFile, bstrSelection) — ITcSmTreeItem::ExportChild is
                // for whole-project zip archives (.tpzip) and rejects individual PLC items
                // ("doesn't specify a zip archive", observed on TwinCAT.XAE.Automation.17.0).
                // The selection is the item's path relative to the PLC project root in DOT notation
                // (e.g. "MyFolder.FB_Sample"), not the caret notation tree paths use.
                var (projectPath, relativePath) = SplitPlcProjectPath(treePath);
                var iecProject = ComInvoke(GetOrCreateSysManager(), "LookupTreeItem", projectPath)!;
                ComInvoke(iecProject, "PlcOpenExport", exportFilePath, relativePath.Replace('^', '.'));
                return new AutomationOperationResult(Applied: true, Succeeded: true, Error: null, safetyReason, $"Exported '{treePath}' to '{exportFilePath}' (PLCopen XML).");
            }
            catch (Exception ex)
            {
                return new AutomationOperationResult(Applied: true, Succeeded: false, ex.Message, safetyReason, Detail: null);
            }
        });

    /// <summary>
    /// Splits a PLC tree path into the nested IEC project node and the item path relative to it.
    /// Tree paths look like "TIPC^&lt;PlcProject&gt;^&lt;PlcProject&gt; Project^Folder^Item" — the
    /// first three segments address the nested project, the rest the item inside it.
    /// </summary>
    private static (string ProjectPath, string RelativePath) SplitPlcProjectPath(string treePath)
    {
        var segments = treePath.Split('^');
        if (segments.Length < 3)
            throw new ArgumentException(
                $"'{treePath}' is not a path inside a PLC project — expected at least 'TIPC^<project>^<project> Project'.", nameof(treePath));

        return (string.Join('^', segments[..3]), string.Join('^', segments[3..]));
    }

    private static int ResolveSubType(string kind, string? pouType) => kind.Trim().ToLowerInvariant() switch
    {
        "folder" => SubTypeFolder,
        "gvl" => SubTypeGvl,
        "dut" => SubTypeDutStruct,
        "pou" => (pouType ?? "Program").Trim().ToLowerInvariant() switch
        {
            "program" => SubTypeProgram,
            "function" => SubTypeFunction,
            "functionblock" or "function_block" or "fb" => SubTypeFunctionBlock,
            _ => throw new ArgumentException($"Unknown POU type '{pouType}'. Expected 'Program', 'Function', or 'FunctionBlock'.", nameof(pouType)),
        },
        _ => throw new ArgumentException($"Unknown object kind '{kind}'. Expected 'Folder', 'Pou', 'Gvl', or 'Dut'.", nameof(kind)),
    };

    private static PlcTreeNode WalkTree(object sysManager, object item, int remainingDepth)
    {
        // Convert.ToInt32 rather than a direct cast: numeric properties come back as whatever VARTYPE
        // the shell chose (VT_I2 has been observed for ItemSubType) and unboxing a short as int throws.
        var name = TryGetString(() => (string)ComGet(item, "Name")!) ?? "(unknown)";
        var path = TryGetString(() => (string)ComGet(item, "PathName")!) ?? name;
        var kind = DescribeSubType(TryGetInt(() => Convert.ToInt32(ComGet(item, "ItemSubType"))));

        IReadOnlyList<PlcTreeNode> children = [];
        if (remainingDepth > 0)
        {
            var list = new List<PlcTreeNode>();
            var count = TryGetInt(() => Convert.ToInt32(ComGet(item, "ChildCount")));
            for (var i = 1; i <= count; i++)
            {
                var child = ComGetIndexed(item, "Child", i)!;
                list.Add(WalkTree(sysManager, child, remainingDepth - 1));
            }

            // A PLC project node does NOT enumerate its nested IEC project — the node where
            // POUs/GVLs/DUTs live and where CreatePlcObject parents belong — among its children
            // (only '<name> Instance' shows up). Probe for it by Beckhoff's naming convention
            // ('<path>^<name> Project') and graft it in, so browsing yields the tree paths the
            // mutation tools actually accept instead of a dead end.
            var nestedProjectPath = $"{path}^{name} Project";
            if (!list.Any(c => string.Equals(c.TreePath, nestedProjectPath, StringComparison.OrdinalIgnoreCase))
                && TryLookupTreeItem(sysManager, nestedProjectPath) is { } nestedProject)
            {
                list.Add(WalkTree(sysManager, nestedProject, remainingDepth - 1));
            }

            if (list.Count > 0)
                children = list;
        }

        return new PlcTreeNode(name, path, kind, children);
    }

    private static object? TryLookupTreeItem(object sysManager, string treePath)
    {
        try { return ComInvoke(sysManager, "LookupTreeItem", treePath); }
        catch { return null; } // a miss throws — absence is the expected answer for most nodes
    }

    private static string DescribeSubType(int subType) => subType switch
    {
        SubTypeFolder => "Folder",
        SubTypeProgram => "Program",
        SubTypeFunction => "Function",
        SubTypeFunctionBlock => "FunctionBlock",
        SubTypeGvl => "Gvl",
        SubTypeDutStruct => "Dut",
        0 => "Unknown",
        _ => $"Other({subType})",
    };

    private static string? TryGetGuidFromXml(object item)
    {
        try
        {
            string xml = (string)ComInvoke(item, "ProduceXml", false)!;
            return XDocument.Parse(xml).Descendants()
                .Select(e => (string?)e.Attribute("Id"))
                .FirstOrDefault(id => !string.IsNullOrEmpty(id));
        }
        catch
        {
            return null;
        }
    }

    private BuildResult RunBuildOperation(bool build, string? configuration)
    {
        var dte = GetOrCreateDte();
        var solutionBuild = dte.Solution.SolutionBuild;

        if (!string.IsNullOrWhiteSpace(configuration))
            solutionBuild.SolutionConfigurations.Item(configuration).Activate();

        // Positional arguments: the wait parameter is named differently per method (Build's is
        // 'WaitForBuildToFinish', Clean's is 'WaitForClean') and a wrong name surfaces as
        // DISP_E_UNKNOWNNAME from the dynamic binder's named-parameter lookup.
        if (build)
            RetryIfComBusy(() => solutionBuild.Build(true));
        else
            RetryIfComBusy(() => solutionBuild.Clean(true));

        var errors = ReadErrorList();
        var errorCount = errors.Count(e => string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        var warningCount = errors.Count(e => string.Equals(e.Severity, "Warning", StringComparison.OrdinalIgnoreCase));

        // The error list is best-effort (not every shell exposes it — see ReadErrorList), so don't
        // let an unreadable list masquerade as success: LastBuildInfo is the number of projects that
        // failed in the last build and lives on the base SolutionBuild interface.
        var failedProjects = TryGetInt(() => Convert.ToInt32(solutionBuild.LastBuildInfo));

        return new BuildResult(Succeeded: errorCount == 0 && failedProjects == 0, errorCount, warningCount, errors);
    }

    private List<BuildErrorInfo> ReadErrorList()
    {
        var errors = new List<BuildErrorInfo>();
        if (_dte is not { } dte)
            return errors;

        try
        {
            // 'ToolWindows' lives on the DTE2 interface; some shells' DTE objects don't resolve it
            // by name (observed on TcXaeShell.DTE.17.0). The Windows collection is on the base DTE
            // interface, so go through it with the error list's well-known window kind GUID
            // (EnvDTE80.WindowKinds.vsWindowKindErrorList). In a headless/automation-controlled
            // shell that window may not exist yet — force-create it via the IDE command first.
            const string vsWindowKindErrorList = "{D78612C7-9962-4B83-95D9-268046DAD23A}";
            dynamic errorList;
            try
            {
                errorList = dte.Windows.Item(vsWindowKindErrorList).Object;
            }
            catch
            {
                try
                {
                    dte.ExecuteCommand("View.ErrorList");
                    errorList = dte.Windows.Item(vsWindowKindErrorList).Object;
                }
                catch
                {
                    errorList = dte.ToolWindows.ErrorList;
                }
            }

            dynamic items = errorList.ErrorItems;
            int count = items.Count;

            for (var i = 1; i <= count; i++)
            {
                dynamic item = items.Item(i);
                errors.Add(new BuildErrorInfo(
                    Severity: ErrorLevelToSeverity((int)item.ErrorLevel),
                    Description: (string)item.Description,
                    Project: TryGetString(() => (string)item.Project),
                    FileName: TryGetString(() => (string)item.FileName),
                    Line: TryGetInt(() => (int)item.Line)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the error list from the XAE Shell.");
        }

        return errors;
    }

    private static string ErrorLevelToSeverity(int errorLevel) => errorLevel switch
    {
        4 => "Error",   // vsBuildErrorLevelHigh
        3 => "Warning", // vsBuildErrorLevelMedium
        _ => "Message", // vsBuildErrorLevelLow
    };

    private dynamic GetOrCreateDte()
    {
        if (_dte is not null)
            return _dte;

        // Guarded at the RunAsync entry point by EnsureSupportedPlatform — every path that reaches here has
        // already confirmed OperatingSystem.IsWindows(), so the platform-compatibility warnings below are
        // false positives (covers DiscoverDteProgId's registry access and Type.GetTypeFromProgID).
#pragma warning disable CA1416
        var progId = DiscoverDteProgId()
            ?? throw new InvalidOperationException(
                "No 'TcXaeShell.DTE.<version>' COM ProgID is registered on this machine — " +
                "is the TwinCAT XAE Shell installed here? (Automation requires a local install; " +
                "see HKEY_CLASSES_ROOT for 'TcXaeShell.DTE.*' entries to confirm.)");

        _logger.LogInformation("Creating XAE Shell DTE instance via auto-detected ProgID '{ProgId}'.", progId);
        var type = Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException($"Auto-detected COM ProgID '{progId}' resolved during discovery but not on re-lookup — this shouldn't happen.");
        dynamic dte = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Activator.CreateInstance returned null for ProgID '{progId}'.");
#pragma warning restore CA1416

        // A freshly-launched Visual-Studio-based shell rejects its first several incoming calls with
        // RPC_E_CALL_REJECTED while it finishes its own startup and starts pumping messages — a long
        // -documented DTE automation quirk. Absorb that here with a cheap read-only property access so
        // every caller of GetOrCreateDte() gets back a DTE that's actually ready to be driven.
        _logger.LogInformation("Waiting for the XAE Shell to finish starting up...");
        RetryIfComBusy(() => { _ = (string)dte.Name; });

        // A DTE created via Activator.CreateInstance starts in "automation controlled" mode: it stays
        // invisible regardless of MainWindow.Visible and quits as soon as this process disconnects.
        // Setting UserControl = true makes the shell behave like a normal interactively-launched
        // instance — required for ShowIde to actually display the window.
        if (_options.ShowIde)
            dte.UserControl = true;

        // A modal dialog (license reminder, project-upgrade prompt, ...) would block the STA thread
        // forever when nobody is watching the IDE — tell the shell to fail the operation instead.
        try { dte.SuppressUI = !_options.ShowIde; } catch { /* not all shell versions expose it */ }

        _dte = dte;
        return _dte;
    }

    /// <summary>
    /// Scans HKEY_CLASSES_ROOT for whatever 'TcXaeShell.DTE.&lt;version&gt;' COM ProgID is actually
    /// registered on this machine. Beckhoff registers one such ProgID per installed XAE Shell
    /// generation (e.g. "TcXaeShell.DTE.15.0" for VS2017-based shells) and the exact string varies
    /// across TwinCAT versions — discovering it directly means a deployment never has to find and
    /// configure it by hand. If more than one generation is installed side by side, the highest
    /// version number is preferred as the most likely active one. Returns null if none is found.
    ///
    /// Only ever called from <see cref="GetOrCreateDte"/>, which every caller reaches through
    /// <c>RunAsync</c> → <c>EnsureSupportedPlatform</c> — so the registry/COM access here is always
    /// on Windows, making the platform-compatibility warnings below false positives.
    /// </summary>
    private static string? DiscoverDteProgId()
    {
#pragma warning disable CA1416
        return Registry.ClassesRoot.GetSubKeyNames()
            .Where(name => name.StartsWith("TcXaeShell.DTE.", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(name => Version.TryParse(name["TcXaeShell.DTE.".Length..], out var version) ? version : new Version(0, 0))
            .FirstOrDefault(name => Type.GetTypeFromProgID(name) is not null);
#pragma warning restore CA1416
    }

    private const int RpcECallRejected = unchecked((int)0x80010001);
    private const int RpcEServerCallRetryLater = unchecked((int)0x8001010A);

    /// <summary>
    /// Retries <paramref name="action"/> while it fails with RPC_E_CALL_REJECTED (0x80010001) or
    /// RPC_E_SERVERCALL_RETRYLATER (0x8001010A) — the HRESULTs a busy or still-starting COM server (like
    /// a freshly-launched DTE) returns to reject an incoming call outright. The documented alternative is
    /// registering an <c>IOleMessageFilter</c>, which needs a COM interop type unavailable at compile time
    /// here (see the class doc comment on why this project can't reference TwinCAT's interop assemblies).
    /// A simple sleep-and-retry loop is the standard pragmatic workaround and is generous enough
    /// (<paramref name="maxAttempts"/> × <paramref name="delayMs"/> ≈ 30s by default) to cover a slow
    /// IDE startup.
    /// </summary>
    // 120 × 500ms ≈ 60s: enough to ride out not just IDE startup but also the post-restart window —
    // after StartRestartTwinCAT the shell can keep rejecting calls for well over 30 seconds while it
    // reconnects to the runtime.
    private static void RetryIfComBusy(Action action, int maxAttempts = 120, int delayMs = 500) =>
        RetryIfComBusy<object?>(() => { action(); return null; }, maxAttempts, delayMs);

    private static T RetryIfComBusy<T>(Func<T> func, int maxAttempts = 120, int delayMs = 500)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return func();
            }
            catch (COMException ex) when ((ex.HResult == RpcECallRejected || ex.HResult == RpcEServerCallRetryLater) && attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    private object GetOrCreateSysManager()
    {
        if (_sysManager is not null)
            return _sysManager;

        if (_openProjectPath is null)
            throw new InvalidOperationException("No XAE project is open — call OpenProject first.");

        var dte = GetOrCreateDte();

        // The whole resolution retries on COM-busy: right after Solution.Open the shell may still be
        // loading the tsproj in the background and rejects calls — without the retry, a busy
        // rejection inside the probe would be misread as "this project has no sys manager".
        _sysManager = RetryIfComBusy(() => ResolveSysManager(dte));
        return _sysManager;
    }

    private static object ResolveSysManager(dynamic dte)
    {
        // Prefer the project whose file is the TwinCAT system project (.tsproj) — Projects.Item(1)
        // alone would break the day the solution also contains a non-TwinCAT project.
        dynamic projects = dte.Solution.Projects;
        int projectCount = (int)projects.Count;
        dynamic? tcProject = null;
        for (var i = 1; i <= projectCount; i++)
        {
            dynamic candidate = projects.Item(i);
            var fullName = TryGetString(() => (string)candidate.FullName);
            if (fullName is not null && fullName.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase))
            {
                tcProject = candidate;
                break;
            }
        }
        tcProject ??= projects.Item(1);

        // Beckhoff's documented Automation Interface pattern: the ITcSysManager IS the tsproj
        // project's automation object — `sysMan = (ITcSysManager)project.Object` (InfoSys
        // tc3_automationinterface, "Opening and activating existing configurations"). The legacy
        // ProjectItems.Item("SYSTEM").Object route is undocumented but kept as a fallback; the
        // LooksLikeSysManager probe lets whichever route yields a real sys manager self-select.
        object? sysManager = null;
        try { sysManager = (object)tcProject!.Object; } catch (Exception ex) when (!IsComBusy(ex)) { /* probe + fallback below */ }
        if (sysManager is null || !LooksLikeSysManager(sysManager))
            sysManager = TryGetSystemItemObject(tcProject!);

        if (sysManager is null || !LooksLikeSysManager(sysManager))
            throw new InvalidOperationException(
                "Could not obtain ITcSysManager from the open project — neither project.Object nor the " +
                "legacy SYSTEM project-item exposed an object with a 'LookupTreeItem' member. Is the open " +
                "solution a TwinCAT XAE project?");

        return sysManager;
    }

    private static bool IsComBusy(Exception ex) =>
        ex.HResult is RpcECallRejected or RpcEServerCallRetryLater;

    private static object? TryGetSystemItemObject(dynamic project)
    {
        try { return (object)project.ProjectItems.Item("SYSTEM").Object; }
        catch (Exception ex) when (!IsComBusy(ex)) { return null; }
    }

    /// <summary>
    /// Cheap sanity probe: resolves the DISPID of a known <c>ITcSysManager</c> member from the
    /// candidate's own type info without invoking anything — distinguishes the real sys manager from
    /// some other automation object (e.g. a plain tree item or VS project wrapper).
    /// </summary>
    private static bool LooksLikeSysManager(object candidate)
    {
        try
        {
            var dispatch = (IDispatch)candidate;
            dispatch.GetTypeInfo(0, 0, out var typeInfo);
            return TryFindDispId(ResolveDispatchTypeInfo(typeInfo), "LookupTreeItem", depth: 0, [], out _);
        }
        catch (Exception ex) when (!IsComBusy(ex))
        {
            // "Busy" must propagate to the caller's retry loop — swallowing it here would misread a
            // still-loading shell as "this object is not the sys manager".
            return false;
        }
    }

    /// <summary>
    /// Standard COM <c>IDispatch</c> (IID 00020400-0000-0000-C000-000000000046) — a fixed, universal COM
    /// interface ID (not Beckhoff-specific), so it can be declared here without a TwinCAT interop
    /// reference. Used to call <c>GetTypeInfo</c> (to resolve a member's DISPID from the object's own
    /// type library, since <c>GetIDsOfNames</c> is unreliable here — see <see cref="ComMember"/>) and
    /// <c>Invoke</c> (to actually call/get it).
    /// </summary>
    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatch
    {
        void GetTypeInfoCount(out int count);

        void GetTypeInfo(int index, int lcid, out ITypeInfo typeInfo);

        // Not called directly — declared only to occupy IDispatch vtable slot 5 so Invoke lands on slot 6.
        void GetIDsOfNames(ref Guid riid, [In, MarshalAs(UnmanagedType.LPArray)] string[] names, int count, int lcid, [Out, MarshalAs(UnmanagedType.LPArray)] int[] dispIds);

        // PreserveSig so a failing call still marshals the out EXCEPINFO back — with implicit HRESULT
        // translation the CLR throws before excepInfo is readable, dropping the COM server's actual
        // error text (bstrDescription). ComMember turns the raw HRESULT into a descriptive exception.
        [PreserveSig]
        int Invoke(int dispIdMember, ref Guid riid, int lcid, ushort flags, ref DISPPARAMS dispParams, out object? result, out EXCEPINFO excepInfo, out uint argErr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPPARAMS
    {
        public IntPtr rgvarg;
        public IntPtr rgdispidNamedArgs;
        public int cArgs;
        public int cNamedArgs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPINFO
    {
        public ushort wCode;
        public ushort wReserved;
        [MarshalAs(UnmanagedType.BStr)] public string? bstrSource;
        [MarshalAs(UnmanagedType.BStr)] public string? bstrDescription;
        [MarshalAs(UnmanagedType.BStr)] public string? bstrHelpFile;
        public uint dwHelpContext;
        public IntPtr pvReserved;
        public IntPtr pfnDeferredFillIn;
        public int scode;
    }

    /// <summary>
    /// <c>ITcPlcDeclaration</c> — the declaration-text aspect of a PLC tree item (POU/GVL/DUT, or a
    /// method/property/action child item). Unlike <c>ITcSmTreeItem</c> (which is dual), this is a raw
    /// IUnknown-derived oleautomation interface (verified against TCatSysManager.tlb 2.1), so the
    /// <see cref="IDispatch"/>/<see cref="ComMember"/> plumbing cannot reach it — instead, casting the
    /// tree-item RCW to this declaration performs a QueryInterface for the hardcoded IID and calls
    /// straight through the vtable, exactly what Beckhoff's official TcatSysManagerLib interop does.
    /// Member order MUST match the typelib's vtable order — a wrong order would silently call the
    /// wrong slot (e.g. put where get sits).
    /// </summary>
    [ComImport]
    [Guid("E937E819-2185-420D-91C0-3D8136FCC517")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITcPlcDeclaration
    {
        string DeclarationText
        {
            [return: MarshalAs(UnmanagedType.BStr)] get;
            [param: MarshalAs(UnmanagedType.BStr)] set;
        }
    }

    /// <summary>
    /// <c>ITcPlcImplementation</c> — the implementation-text aspect of a PLC tree item. Same raw
    /// IUnknown/QI-by-cast contract as <see cref="ITcPlcDeclaration"/>; GVLs/DUTs/folders fail the QI
    /// (they have no executable body). <c>ImplementationXml</c> and <c>Language</c> are never called —
    /// they are declared only to keep <c>ImplementationText</c>'s accessors on the correct vtable slots.
    /// </summary>
    [ComImport]
    [Guid("1E70BCEE-A064-4A22-A408-9EA73954AF16")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITcPlcImplementation
    {
        string ImplementationText
        {
            [return: MarshalAs(UnmanagedType.BStr)] get;
            [param: MarshalAs(UnmanagedType.BStr)] set;
        }

        string ImplementationXml
        {
            [return: MarshalAs(UnmanagedType.BStr)] get;
            [param: MarshalAs(UnmanagedType.BStr)] set;
        }

        int Language { get; set; }
    }

    [DllImport("oleaut32.dll")]
    private static extern void VariantClear(IntPtr pvarg);

    private const ushort DispatchMethod = 0x1;
    private const ushort DispatchPropertyGet = 0x2;

    // sizeof(VARIANT): 16 on 32-bit but 24 on 64-bit (8-byte vt/reserved header plus a union whose
    // largest member, BRECORD, is two pointers). Marshal.GetNativeVariantForObject writes — and the
    // callee reads rgvarg as an array with — full native VARIANTs, so the stride must match the
    // process bitness; a 16-byte stride on x64 makes every call with 2+ arguments hand the COM server
    // overlapping garbage variants (observed as "Specified OLE variant is invalid" / DISP_E errors).
    private static readonly int VariantSize = IntPtr.Size == 8 ? 24 : 16;

    private const int DispEException = unchecked((int)0x80020009);
    private const int DispETypeMismatch = unchecked((int)0x80020005);
    private const int DispEParamNotFound = unchecked((int)0x80020004);

    /// <summary>Invokes a method on an <c>ITcSysManager</c>/<c>ITcSmTreeItem</c> COM object by name.</summary>
    private static object? ComInvoke(object comObject, string methodName, params object?[] args) =>
        ComMember(comObject, methodName, DispatchMethod, args);

    /// <summary>
    /// Reads a property on an <c>ITcSysManager</c>/<c>ITcSmTreeItem</c> COM object by name. Passes
    /// METHOD|PROPERTYGET together — the standard OLE Automation read idiom (what VB and the C#
    /// dynamic binder send), since accessors are declared as INVOKE_FUNC in some typelib versions.
    /// </summary>
    private static object? ComGet(object comObject, string propertyName) =>
        ComMember(comObject, propertyName, DispatchMethod | DispatchPropertyGet, []);

    /// <summary>Reads an indexed property (e.g. <c>ITcSmTreeItem.Child[i]</c>) by name.</summary>
    private static object? ComGetIndexed(object comObject, string propertyName, int index) =>
        ComMember(comObject, propertyName, DispatchMethod | DispatchPropertyGet, [index]);

    /// <summary>
    /// Invokes (or reads) a member on an <c>ITcSysManager</c>/<c>ITcSmTreeItem</c> COM object by DISPID,
    /// resolving that DISPID from the object's own type-library info instead of <c>dynamic</c>'s runtime
    /// <c>IDispatch::GetIDsOfNames</c> lookup.
    ///
    /// TwinCAT's <c>IDispatch::GetIDsOfNames</c> implementation does not resolve members of
    /// <c>ITcSysManager</c>/<c>ITcSmTreeItem</c> by name — including <c>ActivateConfiguration</c>,
    /// <c>StartRestartTwinCAT</c>, and <c>CreateChild</c> — so calling them via <c>dynamic</c> throws
    /// <c>RuntimeBinderException: 'System.__ComObject' does not contain a definition for '...'</c> even
    /// though the member exists. The DISPID looked up here from <see cref="ITypeInfo.GetFuncDesc"/> /
    /// <see cref="ITypeInfo.GetDocumentation"/> is the same DISPID the object's own
    /// <c>IDispatch::Invoke</c> recognizes, so calling <c>Invoke</c> directly with it works regardless of
    /// the broken name lookup. Use this (and <see cref="ComInvoke"/>/<see cref="ComGet"/>/
    /// <see cref="ComGetIndexed"/>) for all <c>ITcSysManager</c>/<c>ITcSmTreeItem</c> member access —
    /// including any future IO-mapping tools — so a plain <c>dynamic</c> call doesn't reintroduce this
    /// failure mode.
    /// </summary>
#pragma warning disable CA1416 // only ever reached via RunAsync -> EnsureSupportedPlatform on Windows
    private static object? ComMember(object comObject, string memberName, ushort invokeFlags, object?[] args) =>
        // The shell rejects incoming COM calls with RPC_E_CALL_REJECTED whenever it is busy — most
        // notably while it is still loading a freshly-opened project in the background. A rejected
        // call never started executing, so retrying is safe even for mutating members.
        RetryIfComBusy(() => ComMemberCore(comObject, memberName, invokeFlags, args));

    // If a *dispatch* property ever needs setting (e.g. ITcSmTreeItem.Comment/Disabled, which ARE on
    // the dual interface), the shape is: invokeFlags = 0x4 (DISPATCH_PROPERTYPUT) and DISPPARAMS must
    // carry the value as a named arg — allocate 4 bytes, write DISPID_PROPERTYPUT (-3) into it, set
    // rgdispidNamedArgs to that pointer and cNamedArgs = 1. Do NOT use this for ITcPlcDeclaration/
    // ITcPlcImplementation members: those interfaces are raw IUnknown (no dispinterface), and DISPIDs
    // are interface-scoped — invoking a foreign DISPID against the default dispatch interface can
    // silently hit an unrelated member. Use the [ComImport] cast route instead (see those interfaces).
    private static object? ComMemberCore(object comObject, string memberName, ushort invokeFlags, object?[] args)
    {
        var dispatch = (IDispatch)comObject;
        dispatch.GetTypeInfo(0, 0, out var typeInfo);
        var dispId = FindDispId(ResolveDispatchTypeInfo(typeInfo), memberName);

        var argCount = args.Length;
        var variants = argCount > 0 ? Marshal.AllocHGlobal(VariantSize * argCount) : IntPtr.Zero;
        try
        {
            // Zero the whole buffer first so that if packing throws partway through, the finally
            // block's VariantClear sees VT_EMPTY instead of uninitialized heap garbage.
            for (var offset = 0; offset < VariantSize * argCount; offset += 8)
                Marshal.WriteInt64(variants, offset, 0);

            // DISPPARAMS arguments are passed in reverse order.
            for (var i = 0; i < argCount; i++)
                Marshal.GetNativeVariantForObject(args[argCount - 1 - i], variants + i * VariantSize);

            var dispParams = new DISPPARAMS { rgvarg = variants, rgdispidNamedArgs = IntPtr.Zero, cArgs = argCount, cNamedArgs = 0 };
            var iidNull = Guid.Empty;
            var hr = dispatch.Invoke(dispId, ref iidNull, 0, invokeFlags, ref dispParams, out var result, out var excepInfo, out var argErr);

            if (hr == DispEException)
            {
                // The COM server filled EXCEPINFO with its own error report — surface it verbatim,
                // since "Exception occurred (0x80020009)" alone is useless for diagnosing tool failures.
                var code = excepInfo.scode != 0 ? excepInfo.scode : excepInfo.wCode;
                throw new COMException(
                    $"COM member '{memberName}' raised an error: {excepInfo.bstrDescription ?? "(no description provided)"} " +
                    $"(source: {excepInfo.bstrSource ?? "unknown"}, code: 0x{code:X8})",
                    code != 0 ? code : hr);
            }

            if (hr < 0)
            {
                var argHint = hr is DispETypeMismatch or DispEParamNotFound
                    ? $" (argErr index {argErr}, counting from the last argument — rgvarg is reversed)"
                    : ".";
                throw new COMException($"COM member '{memberName}' failed with HRESULT 0x{hr:X8}{argHint}", hr);
            }

            return result;
        }
        finally
        {
            for (var i = 0; i < argCount; i++)
                VariantClear(variants + i * VariantSize);
            if (variants != IntPtr.Zero)
                Marshal.FreeHGlobal(variants);
        }
    }

    /// <summary>
    /// Some COM objects' <c>IDispatch::GetTypeInfo(0, ...)</c> hands back the type info for their
    /// <em>coclass</em> (e.g. <c>TcSysManager</c>) rather than the dispatch interface they actually expose
    /// (<c>ITcSysManager3</c>) — a coclass's <c>TYPEATTR.cFuncs</c> is always 0 (it lists implemented
    /// interfaces, not methods), which is why <see cref="FindDispId"/> would otherwise fail with "does not
    /// declare a member" for every member. When that happens, walk the coclass's implemented interfaces
    /// and use the default (non-source) one instead.
    /// </summary>
    private static ITypeInfo ResolveDispatchTypeInfo(ITypeInfo typeInfo)
    {
        typeInfo.GetTypeAttr(out var typeAttrPtr);
        TYPEATTR typeAttr;
        try
        {
            typeAttr = Marshal.PtrToStructure<TYPEATTR>(typeAttrPtr);
        }
        finally
        {
            typeInfo.ReleaseTypeAttr(typeAttrPtr);
        }

        if (typeAttr.typekind != TYPEKIND.TKIND_COCLASS)
            return typeInfo;

        for (var i = 0; i < typeAttr.cImplTypes; i++)
        {
            typeInfo.GetImplTypeFlags(i, out var flags);
            if ((flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT) == 0 || (flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE) != 0)
                continue;

            typeInfo.GetRefTypeOfImplType(i, out var href);
            typeInfo.GetRefTypeInfo(href, out var implTypeInfo);
            return implTypeInfo;
        }

        throw new InvalidOperationException("The COM object's coclass type info has no default incoming interface.");
    }

    private static int FindDispId(ITypeInfo typeInfo, string memberName)
    {
        var seen = new List<string>();
        if (TryFindDispId(typeInfo, memberName, depth: 0, seen, out var dispId))
            return dispId;

        throw new InvalidOperationException(
            $"The COM object's interface (and its base interfaces) do not declare a member named '{memberName}'. " +
            $"Members found: {string.Join(", ", seen.Distinct())}.");
    }

    /// <summary>
    /// TwinCAT's type library only declares each member on the interface generation that introduced
    /// it: <c>CreateChild</c>/<c>DeleteChild</c>/... live on base <c>ITcSmTreeItem</c> and
    /// <c>ActivateConfiguration</c>/<c>LookupTreeItem</c>/<c>StartRestartTwinCAT</c> on base
    /// <c>ITcSysManager</c>, while the live objects describe themselves with the latest generation
    /// (e.g. <c>ITcSmTreeItem10</c>, whose own TKIND_INTERFACE type info declares just
    /// <c>PvSimulation</c>). TKIND_DISPATCH type infos flatten inherited members into cFuncs;
    /// TKIND_INTERFACE ones do not — those expose their base via GetRefTypeOfImplType. So a member
    /// missing at the current level must be looked up recursively through the base-interface chain.
    /// </summary>
    private static bool TryFindDispId(ITypeInfo typeInfo, string memberName, int depth, List<string> seen, out int dispId)
    {
        dispId = 0;
        if (depth > 32) // defensive cap; real chains are ITcSysManager18 -> ... -> ITcSysManager -> IDispatch
            return false;

        typeInfo.GetTypeAttr(out var typeAttrPtr);
        int functionCount, implTypeCount;
        try
        {
            var typeAttr = Marshal.PtrToStructure<TYPEATTR>(typeAttrPtr);
            functionCount = typeAttr.cFuncs;
            implTypeCount = typeAttr.cImplTypes;
        }
        finally
        {
            typeInfo.ReleaseTypeAttr(typeAttrPtr);
        }

        for (var i = 0; i < functionCount; i++)
        {
            typeInfo.GetFuncDesc(i, out var funcDescPtr);
            try
            {
                var funcDesc = Marshal.PtrToStructure<FUNCDESC>(funcDescPtr);
                typeInfo.GetDocumentation(funcDesc.memid, out var name, out _, out _, out _);
                // Case-insensitive: COM name lookup (GetIDsOfNames) is case-insensitive, and Beckhoff's
                // own casing varies (e.g. "StartRestartTwinCAT") — don't make casing a failure mode.
                if (string.Equals(name, memberName, StringComparison.OrdinalIgnoreCase))
                {
                    dispId = funcDesc.memid;
                    return true;
                }
                seen.Add(name);
            }
            finally
            {
                typeInfo.ReleaseFuncDesc(funcDescPtr);
            }
        }

        for (var i = 0; i < implTypeCount; i++)
        {
            typeInfo.GetRefTypeOfImplType(i, out var href);
            typeInfo.GetRefTypeInfo(href, out var baseTypeInfo);
            if (TryFindDispId(baseTypeInfo, memberName, depth + 1, seen, out dispId))
                return true;
        }

        return false;
    }
#pragma warning restore CA1416

    private static string? TryGetString(Func<string> accessor)
    {
        try { return accessor(); }
        catch { return null; }
    }

    private static int TryGetInt(Func<int> accessor)
    {
        try { return accessor(); }
        catch { return 0; }
    }

    private Task<T> RunAsync<T>(Func<T> work)
    {
        EnsureSupportedPlatform();
        return _dispatcher.RunAsync(work);
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The TwinCAT XAE Shell Automation Interface is COM-based and only works on Windows, " +
                "with a local TwinCAT XAE Shell install. This server must run on the engineering " +
                "workstation for Automation tools to function — Source and Runtime tools remain available everywhere.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_dte is null)
            return;

        await _dispatcher.RunAsync(() =>
        {
            try
            {
                ReleaseComObject(ref _sysManager);

                if (_dte is { } dte)
                {
                    dte.Quit();
                    ReleaseComObject(ref _dte);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring error while shutting down the XAE Shell session.");
            }
        });
    }

    private static void ReleaseComObject(ref object? comObject)
    {
        if (comObject is null)
            return;

        if (OperatingSystem.IsWindows() && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);

        comObject = null;
    }
}
