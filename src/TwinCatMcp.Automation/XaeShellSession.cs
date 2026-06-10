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
                var childCount = (int)ComGet(root, "ChildCount")!;
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
                var parent = ComInvoke(GetOrCreateSysManager(), "LookupTreeItem", parentTreePath)!;

                object vInfo = subType is SubTypeProgram or SubTypeFunction or SubTypeFunctionBlock
                    ? IecLanguageStructuredText
                    : 0;

                // A single CreateChild call both writes the file and registers it with the IDE
                // (correct GUID/.xti sync) — this is the whole point of going through the Automation
                // Interface instead of the file-based CreatePou tool (see SourceTools.CreatePou).
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
                // ITcSmTreeItem::ImportChild(bstrFile, bstrBefore = "", bReconnect = true, bstrName = "")
                // — called on the parent item; the trailing optional arguments default per the type
                // library when omitted, matching the Beckhoff sample `item.ImportChild("c:\...\box1.tce")`.
                var parent = ComInvoke(GetOrCreateSysManager(), "LookupTreeItem", parentTreePath)!;
                ComInvoke(parent, "ImportChild", exportFilePath);
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
                // ITcSmTreeItem::ExportChild(name, file) — called on the parent item with the
                // exported child's own name, per Beckhoff's Automation Interface.
                var item = ComInvoke(GetOrCreateSysManager(), "LookupTreeItem", treePath)!;
                var parent = ComGet(item, "Parent")!;
                var itemName = (string)ComGet(item, "Name")!;
                ComInvoke(parent, "ExportChild", itemName, exportFilePath);
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

    private static PlcTreeNode WalkTree(object item, int remainingDepth)
    {
        var name = TryGetString(() => (string)ComGet(item, "Name")!) ?? "(unknown)";
        var path = TryGetString(() => (string)ComGet(item, "PathName")!) ?? name;
        var kind = DescribeSubType(TryGetInt(() => (int)ComGet(item, "ItemSubType")!));

        IReadOnlyList<PlcTreeNode> children = [];
        if (remainingDepth > 0)
        {
            var count = TryGetInt(() => (int)ComGet(item, "ChildCount")!);
            if (count > 0)
            {
                var list = new List<PlcTreeNode>(count);
                for (var i = 1; i <= count; i++)
                {
                    var child = ComGetIndexed(item, "Child", i)!;
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

    private object GetOrCreateSysManager()
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
        _sysManager = (object)systemItem.Object;

        return _sysManager;
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

        void Invoke(int dispIdMember, ref Guid riid, int lcid, ushort flags, ref DISPPARAMS dispParams, out object? result, out EXCEPINFO excepInfo, out uint argErr);
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

    [DllImport("oleaut32.dll")]
    private static extern void VariantClear(IntPtr pvarg);

    private const ushort DispatchMethod = 0x1;
    private const ushort DispatchPropertyGet = 0x2;
    private const int VariantSize = 16; // sizeof(VARIANT) — fixed on both 32- and 64-bit.

    /// <summary>Invokes a method on an <c>ITcSysManager</c>/<c>ITcSmTreeItem</c> COM object by name.</summary>
    private static object? ComInvoke(object comObject, string methodName, params object?[] args) =>
        ComMember(comObject, methodName, DispatchMethod, args);

    /// <summary>Reads a property on an <c>ITcSysManager</c>/<c>ITcSmTreeItem</c> COM object by name.</summary>
    private static object? ComGet(object comObject, string propertyName) =>
        ComMember(comObject, propertyName, DispatchPropertyGet, []);

    /// <summary>Reads an indexed property (e.g. <c>ITcSmTreeItem.Child[i]</c>) by name.</summary>
    private static object? ComGetIndexed(object comObject, string propertyName, int index) =>
        ComMember(comObject, propertyName, DispatchPropertyGet, [index]);

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
    private static object? ComMember(object comObject, string memberName, ushort invokeFlags, object?[] args)
    {
        var dispatch = (IDispatch)comObject;
        dispatch.GetTypeInfo(0, 0, out var typeInfo);
        var dispId = FindDispId(ResolveDispatchTypeInfo(typeInfo), memberName);

        var argCount = args.Length;
        var variants = argCount > 0 ? Marshal.AllocHGlobal(VariantSize * argCount) : IntPtr.Zero;
        try
        {
            // DISPPARAMS arguments are passed in reverse order.
            for (var i = 0; i < argCount; i++)
                Marshal.GetNativeVariantForObject(args[argCount - 1 - i], variants + i * VariantSize);

            var dispParams = new DISPPARAMS { rgvarg = variants, rgdispidNamedArgs = IntPtr.Zero, cArgs = argCount, cNamedArgs = 0 };
            var iidNull = Guid.Empty;
            dispatch.Invoke(dispId, ref iidNull, 0, invokeFlags, ref dispParams, out var result, out _, out _);
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
        typeInfo.GetTypeAttr(out var typeAttrPtr);
        int functionCount;
        try
        {
            functionCount = Marshal.PtrToStructure<TYPEATTR>(typeAttrPtr).cFuncs;
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
                if (string.Equals(name, memberName, StringComparison.Ordinal))
                    return funcDesc.memid;
            }
            finally
            {
                typeInfo.ReleaseFuncDesc(funcDescPtr);
            }
        }

        throw new InvalidOperationException($"The COM object's interface does not declare a member named '{memberName}'.");
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
