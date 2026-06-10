using System.Runtime.InteropServices;
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
    private dynamic? _sysManager;
    private string? _openProjectPath;

    public XaeShellSession(StaThreadDispatcher dispatcher, IOptions<AutomationOptions> options, ILogger<XaeShellSession> logger)
    {
        _dispatcher = dispatcher;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsProjectOpen => _openProjectPath is not null;

    public Task<ProjectInfo> OpenProjectAsync(string path, CancellationToken ct) =>
        RunAsync(() =>
        {
            var dte = GetOrCreateDte();
            dte.MainWindow.Visible = _options.ShowIde;
            dte.Solution.Open(path);
            _openProjectPath = path;
            _sysManager = null; // re-resolved lazily once a project is open — see GetOrCreateSysManager

            string? fullName = TryGetString(() => (string)dte.Solution.FullName);
            return new ProjectInfo(Open: true, path, fullName);
        });

    public Task<ProjectInfo> CloseProjectAsync(CancellationToken ct) =>
        RunAsync(() =>
        {
            if (_dte is { } dte && _openProjectPath is { } path)
            {
                dte.Solution.Close(SaveFirst: false);
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

    public Task<BuildResult> BuildAsync(string? configuration, CancellationToken ct) =>
        RunAsync(() => RunBuildOperation(build: true, configuration));

    public Task<BuildResult> CleanAsync(string? configuration, CancellationToken ct) =>
        RunAsync(() => RunBuildOperation(build: false, configuration));

    public Task<BuildResult> GetBuildErrorsAsync(CancellationToken ct) =>
        RunAsync(() => new BuildResult(Succeeded: true, 0, 0, ReadErrorList()));

    public Task<IReadOnlyList<HardwareConfiguration>> ListHardwareConfigurationsAsync(CancellationToken ct) =>
        RunAsync(() =>
        {
            var sysManager = GetOrCreateSysManager();
            var configs = (IReadOnlyList<HardwareConfiguration>?)null;

            // ITcSysManager exposes configurations through its tree (TreeItem "TIRC" / "Configurations") —
            // the exact navigation path varies by TwinCAT version, so this degrades to an empty list with a
            // logged warning rather than throwing, keeping read-only browsing resilient to shell differences.
            try
            {
                var configurations = new List<HardwareConfiguration>();
                dynamic root = sysManager.LookupTreeItem("TIRC");
                for (var i = 1; i <= (int)root.ChildCount; i++)
                {
                    dynamic child = root.Child[i];
                    configurations.Add(new HardwareConfiguration((string)child.Name, IsActive: false));
                }
                configs = configurations;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not enumerate hardware configurations from ITcSysManager — returning an empty list.");
            }

            return configs ?? [];
        });

    public Task<AutomationOperationResult> ActivateConfigurationAsync(string configurationName, string safetyReason, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                var sysManager = GetOrCreateSysManager();
                sysManager.ActivateConfiguration();
                return new AutomationOperationResult(Applied: true, Succeeded: true, Error: null, safetyReason, $"Activated configuration '{configurationName}'.");
            }
            catch (Exception ex)
            {
                return new AutomationOperationResult(Applied: true, Succeeded: false, ex.Message, safetyReason, Detail: null);
            }
        });

    public Task<AutomationOperationResult> RestartTwinCatAsync(string mode, string safetyReason, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                var sysManager = GetOrCreateSysManager();

                // ITcSysManager3.StartRestartTwinCAT(mode) — mode 0 = TComRestartMode.Restart (cold),
                // 1 = reload only. Surfacing it as a string keeps the MCP tool signature self-describing.
                int restartMode = string.Equals(mode, "ReloadOnly", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                sysManager.StartRestartTwinCAT(restartMode);

                return new AutomationOperationResult(Applied: true, Succeeded: true, Error: null, safetyReason, $"Requested TwinCAT restart (mode='{mode}').");
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
                // "TIPC" is the standard Beckhoff tree-path root for the PLC configuration node — the
                // same convention as the "TIRC" lookup in ListHardwareConfigurationsAsync. Exact child
                // navigation below a given path is otherwise driven entirely by what LookupTreeItem
                // returns, so any valid PathName reported by a previous browse can be passed back in.
                dynamic sysManager = GetOrCreateSysManager();
                // Assigning to 'object' (not 'dynamic') keeps the WalkTree call statically typed —
                // otherwise a dynamic argument makes the whole call (and RunAsync's inferred T) dynamic.
                object root = sysManager.LookupTreeItem(string.IsNullOrWhiteSpace(treePath) ? "TIPC" : treePath);
                return WalkTree(root, Math.Max(0, maxDepth));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not browse the PLC project tree at '{TreePath}'.", treePath ?? "TIPC");
                return null;
            }
        });

    public Task<PlcObjectCreationResult> CreatePlcObjectAsync(
        string parentTreePath, string name, string kind, string? pouType, string safetyReason, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                var subType = ResolveSubType(kind, pouType);
                dynamic parent = GetOrCreateSysManager().LookupTreeItem(parentTreePath);

                object vInfo = subType is SubTypeProgram or SubTypeFunction or SubTypeFunctionBlock
                    ? IecLanguageStructuredText
                    : 0;

                // A single CreateChild call both writes the file and registers it with the IDE
                // (correct GUID/.xti sync) — this is the whole point of going through the Automation
                // Interface instead of the file-based CreatePou tool (see SourceTools.CreatePou).
                dynamic child = parent.CreateChild(name, subType, "", vInfo);
                var newTreePath = TryGetString(() => (string)child.PathName) ?? $"{parentTreePath}^{name}";
                // Cast to 'object' first — TryGetGuidFromXml(child) with a dynamic argument would make
                // the whole call (and its result) dynamic, losing the string? we need for the record.
                var guid = TryGetGuidFromXml((object)child);

                return new PlcObjectCreationResult(Applied: true, Succeeded: true, newTreePath, guid, safetyReason, Error: null);
            }
            catch (Exception ex)
            {
                return new PlcObjectCreationResult(Applied: true, Succeeded: false, TreePath: null, Guid: null, safetyReason, ex.Message);
            }
        });

    public Task<AutomationOperationResult> DeletePlcObjectAsync(string treePath, string safetyReason, CancellationToken ct) =>
        RunAsync(() =>
        {
            try
            {
                dynamic item = GetOrCreateSysManager().LookupTreeItem(treePath);
                item.Delete();
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
                // Whether ImportSubTree lives directly on ITcSmTreeItem or requires casting to a
                // project-level ITcPlcIECProject is version-dependent and unconfirmed (TcatSysManagerLib
                // isn't available at compile time — see the class doc). Calling it on the resolved tree
                // item is the simplest first attempt; a binder/COM exception here is the concrete
                // starting point for finding the right object on a real shell.
                dynamic parent = GetOrCreateSysManager().LookupTreeItem(parentTreePath);
                parent.ImportSubTree(exportFilePath, "");
                return new AutomationOperationResult(Applied: true, Succeeded: true, Error: null, safetyReason, $"Imported '{exportFilePath}' into '{parentTreePath}'.");
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
                dynamic item = GetOrCreateSysManager().LookupTreeItem(treePath);
                item.ExportChild(exportFilePath, "");
                return new AutomationOperationResult(Applied: true, Succeeded: true, Error: null, safetyReason, $"Exported '{treePath}' to '{exportFilePath}'.");
            }
            catch (Exception ex)
            {
                return new AutomationOperationResult(Applied: true, Succeeded: false, ex.Message, safetyReason, Detail: null);
            }
        });

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

    private static PlcTreeNode WalkTree(dynamic item, int remainingDepth)
    {
        var name = TryGetString(() => (string)item.Name) ?? "(unknown)";
        var path = TryGetString(() => (string)item.PathName) ?? name;
        var kind = DescribeSubType(TryGetInt(() => (int)item.ItemSubType));

        IReadOnlyList<PlcTreeNode> children = [];
        if (remainingDepth > 0)
        {
            var count = TryGetInt(() => (int)item.ChildCount);
            if (count > 0)
            {
                var list = new List<PlcTreeNode>(count);
                for (var i = 1; i <= count; i++)
                {
                    object child = item.Child[i];
                    list.Add(WalkTree(child, remainingDepth - 1));
                }
                children = list;
            }
        }

        return new PlcTreeNode(name, path, kind, children);
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

    private static string? TryGetGuidFromXml(dynamic item)
    {
        try
        {
            string xml = (string)item.ProduceXml(false);
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

        if (build)
            solutionBuild.Build(WaitForBuildToFinish: true);
        else
            solutionBuild.Clean(WaitForBuildToFinish: true);

        var errors = ReadErrorList();
        var errorCount = errors.Count(e => string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        var warningCount = errors.Count(e => string.Equals(e.Severity, "Warning", StringComparison.OrdinalIgnoreCase));

        return new BuildResult(Succeeded: errorCount == 0, errorCount, warningCount, errors);
    }

    private List<BuildErrorInfo> ReadErrorList()
    {
        var errors = new List<BuildErrorInfo>();
        if (_dte is not { } dte)
            return errors;

        try
        {
            dynamic errorList = dte.ToolWindows.ErrorList;
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
    private static void RetryIfComBusy(Action action, int maxAttempts = 60, int delayMs = 500)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (COMException ex) when ((ex.HResult == RpcECallRejected || ex.HResult == RpcEServerCallRetryLater) && attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    private dynamic GetOrCreateSysManager()
    {
        if (_sysManager is not null)
            return _sysManager;

        if (_openProjectPath is null)
            throw new InvalidOperationException("No XAE project is open — call OpenProject first.");

        var dte = GetOrCreateDte();

        // The system manager is reached through the open project's "System" sub-item, cast to
        // ITcSysManager via its automation object — this is the standard Beckhoff sample pattern.
        dynamic project = dte.Solution.Projects.Item(1);
        dynamic systemItem = project.ProjectItems.Item("SYSTEM");
        _sysManager = systemItem.Object;

        return _sysManager;
    }

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

    private static void ReleaseComObject(ref dynamic? comObject)
    {
        if (comObject is null)
            return;

        if (OperatingSystem.IsWindows() && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);

        comObject = null;
    }
}
