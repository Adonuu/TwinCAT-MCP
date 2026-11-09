using System.Globalization;
using System.Xml.Linq;
using TwinCatMcp.Automation.Models;

namespace TwinCatMcp.Automation;

public sealed partial class XaeShellSession
{
    private readonly IConfigurationDispatch _configurationDispatch;
    private readonly Func<object> _configurationSystemManager;
    private string? _selectedTwinCatProject;

    private Task<T> ConfigurationAsync<T>(Func<T> work, CancellationToken ct) => RunAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        RefreshAttachedProject();
        return work();
    });

    private object CManager() => _configurationSystemManager();
    private object CItem(string path) => CCall(CManager(), "LookupTreeItem", path)
        ?? throw new InvalidOperationException($"No tree item at '{path}'.");
    private object? CCall(object target, string member, params object?[] args) => _configurationDispatch.Invoke(target, member, args);
    private object? CGet(object target, string member) => _configurationDispatch.Get(target, member);
    private string CXml(object item) => (string)CCall(item, "ProduceXml", false)!;
    private XaeTreeItemInfo CInfo(object item) => new((string)CGet(item, "Name")!, (string)CGet(item, "PathName")!,
        Convert.ToInt32(CGet(item, "ItemType")), Convert.ToInt32(CGet(item, "ItemSubType")));
    private T WithItem<T>(string path, Func<object, T> use)
    {
        var item = CItem(path);
        try { return use(item); }
        finally { RunningXaeShells.Release(item); }
    }
    private void CConsume(object item, string xml)
    {
        CCall(item, "ConsumeXml", xml);
        var error = (string?)CCall(item, "GetLastXmlError");
        if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException($"TwinCAT rejected the XML configuration: {error}");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['^', '/', '\\']) >= 0 || name is "." or "..")
            throw new ArgumentException("A name must be nonempty and cannot contain tree or directory separators.");
    }

    public Task<IReadOnlyList<TwinCatProjectInfo>> ListTwinCatProjectsAsync(CancellationToken ct) =>
        ConfigurationAsync<IReadOnlyList<TwinCatProjectInfo>>(() =>
        {
            if (_dte is null || _openProjectPath is null) throw new InvalidOperationException("Open or attach to a solution first.");
            var result = new List<TwinCatProjectInfo>();
            dynamic projects = _dte.Solution.Projects;
            for (var index = 1; index <= (int)projects.Count; index++)
            {
                dynamic project = projects.Item(index);
                string path = project.FullName;
                if (!path.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase)) continue;
                string name = project.Name;
                result.Add(new(name, path, _selectedTwinCatProject is null ? result.Count == 0 :
                    string.Equals(name, _selectedTwinCatProject, StringComparison.OrdinalIgnoreCase)));
            }
            return result;
        }, ct);

    public Task<TwinCatProjectInfo> SelectTwinCatProjectAsync(string name, CancellationToken ct) => ConfigurationAsync(() =>
    {
        if (_dte is null) throw new InvalidOperationException("Open or attach to a solution first.");
        dynamic projects = _dte.Solution.Projects;
        for (var index = 1; index <= (int)projects.Count; index++)
        {
            dynamic project = projects.Item(index);
            string path = project.FullName;
            if (path.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase) && string.Equals((string)project.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseComObject(ref _sysManager);
                _selectedTwinCatProject = (string)project.Name;
                return new TwinCatProjectInfo(_selectedTwinCatProject, path, true);
            }
        }
        throw new ArgumentException($"No TwinCAT project named '{name}'.");
    }, ct);

    public Task<ProjectInfo> CreateXaeSolutionAsync(string solutionPath, CancellationToken ct) => ConfigurationAsync(() =>
    {
        var fullPath = Path.GetFullPath(solutionPath);
        if (!Path.IsPathFullyQualified(solutionPath) || !fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("solutionPath must be an absolute .sln path.");
        if (File.Exists(fullPath)) throw new IOException("The solution already exists; use open_xae_project instead.");
        if (_dte is not null) throw new InvalidOperationException("Disconnect the current XAE session before creating a solution.");
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        dynamic dte = GetOrCreateDte();
        RetryIfComBusy(() => dte.Solution.Create(directory, Path.GetFileNameWithoutExtension(fullPath)));
        RetryIfComBusy(() => dte.Solution.SaveAs(fullPath));
        _openProjectPath = fullPath;
        ProjectOpened?.Invoke(directory);
        return new ProjectInfo(true, fullPath, fullPath);
    }, ct);

    public Task<TwinCatProjectInfo> AddTwinCatProjectAsync(string name, string templatePath, CancellationToken ct) => ConfigurationAsync(() =>
    {
        ValidateName(name);
        if (_dte is null || _openProjectPath is null) throw new InvalidOperationException("Open or create a solution first.");
        var template = Path.GetFullPath(templatePath);
        if (!template.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The template must be a TwinCAT .tsproj file.");
        if (!File.Exists(template)) throw new FileNotFoundException("TwinCAT project template not found.", template);
        dynamic existingProjects = _dte.Solution.Projects;
        for (var index = 1; index <= (int)existingProjects.Count; index++)
            if (string.Equals((string)existingProjects.Item(index).Name, name, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"The solution already contains a project named '{name}'.");
        var directory = Path.Combine(Path.GetDirectoryName(_openProjectPath)!, name);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Project directory is not empty.");
        dynamic project = RetryIfComBusy(() => _dte.Solution.AddFromTemplate(template, directory + Path.DirectorySeparatorChar, name));
        // AddFromTemplate can return null; look up the project only after the mutation, never retry it.
        if (project is null)
        {
            dynamic projects = _dte.Solution.Projects;
            for (var i = 1; i <= (int)projects.Count; i++)
            {
                dynamic candidate = projects.Item(i);
                if (string.Equals((string)candidate.Name, name, StringComparison.OrdinalIgnoreCase)) { project = candidate; break; }
            }
        }
        if (project is null) throw new InvalidOperationException("Project creation returned no project; inspect the solution before retrying.");
        ReleaseComObject(ref _sysManager);
        _selectedTwinCatProject = name;
        return new TwinCatProjectInfo(name, (string)project.FullName, true);
    }, ct);

    public Task<string> GetXaeTreeXmlAsync(string treePath, CancellationToken ct) =>
        ConfigurationAsync(() => WithItem(treePath, CXml), ct);

    public Task<string> GetXaeTargetNetIdAsync(CancellationToken ct) =>
        ConfigurationAsync(() => (string)CCall(CManager(), "GetTargetNetId")!, ct);

    public Task<string> SetXaeTargetNetIdAsync(string netId, CancellationToken ct) => ConfigurationAsync(() =>
    {
        var parts = netId.Split('.');
        if (parts.Length != 6 || parts.Any(p => !byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new ArgumentException("An AMS Net ID must have six dot-separated bytes.");
        CCall(CManager(), "SetTargetNetId", netId);
        return netId;
    }, ct);

    public Task<string> SetXaeTargetPlatformAsync(string platform, CancellationToken ct) => ConfigurationAsync(() =>
    {
        if (!new[] { "TwinCAT OS (ARMV7-A)", "TwinCAT OS (ARMV7-M)", "TwinCAT OS (ARMV8-A)", "TwinCAT OS (x64)", "TwinCAT OS (x64-E)", "TwinCAT RT (x64)", "TwinCAT RT (x86)" }.Contains(platform, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported target platform; use the exact platform string from the tool description.");
        CCall(CManager(), "Configuration.SetTargetPlatform", platform);
        return platform;
    }, ct);

    private XaeTreeItemInfo CCreate(string parentPath, string name, int subtype, object? info, string? before = null)
    {
        ValidateName(name);
        return WithItem(parentPath, parent =>
        {
            var child = CCall(parent, "CreateChild", name, subtype, before ?? "", info)!;
            try { return CInfo(child); }
            finally { RunningXaeShells.Release(child); }
        });
    }

    public Task<XaeTreeItemInfo> CreateEtherCatMasterAsync(string name, bool simulation, CancellationToken ct) =>
        ConfigurationAsync(() => CCreate("TIID", name, simulation ? 144 : 111, null), ct);

    public Task<XaeTreeItemInfo> CreateEtherCatBoxAsync(string parentPath, string name, string productInfo, string? before, CancellationToken ct) =>
        ConfigurationAsync(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(productInfo);
            return CCreate(parentPath, name, 9099, productInfo, before);
        }, ct);

    public Task<DeviceAdapterInfo> GetDeviceAdapterAsync(string treePath, CancellationToken ct) => ConfigurationAsync(() => WithItem(treePath, item =>
    {
        var document = ConfigurationXml.Parse(CXml(item), "TreeItem");
        var adapter = document.Root!.Element("DeviceDef")?.Element("AddressInfo")?.Element("Pnp");
        return new DeviceAdapterInfo(adapter?.Element("DeviceDesc")?.Value ??
            document.Descendants("EcatSimu").Elements("GeneralSettings").FirstOrDefault()?.Attribute("DeviceDesc")?.Value,
            adapter?.Element("DeviceName")?.Value, adapter?.Element("DeviceData")?.Value);
    }), ct);

    public Task<string> SetDeviceAdapterAsync(string treePath, string description, string deviceGuid, string mac, CancellationToken ct) => ConfigurationAsync(() =>
    {
        var xml = ConfigurationXml.Adapter(description, deviceGuid, mac);
        return WithItem(treePath, item => { CConsume(item, xml); return treePath; });
    }, ct);

    public Task<string> TransferEniAsync(string treePath, string path, bool import, CancellationToken ct) => ConfigurationAsync(() =>
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("ENI path must be absolute.");
        var fullPath = Path.GetFullPath(path);
        if (import)
        {
            if (!File.Exists(fullPath)) throw new FileNotFoundException("ENI file not found.", fullPath);
            // Reject DTDs/entities before the IDE consumes the file.
            ConfigurationXml.Parse(File.ReadAllText(fullPath), "EtherCATConfig");
        }
        else
        {
            if (File.Exists(fullPath)) throw new IOException("Export destination exists. Choose a new path.");
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        }
        WithItem(treePath, item =>
        {
            CConsume(item, ConfigurationXml.Tree("EtherCAT", new XElement(import ? "ImportConfigurationFile" : "ExportConfigurationFile", fullPath)));
            return true;
        });
        if (!import && !File.Exists(fullPath)) throw new IOException("The IDE accepted the export but did not create the ENI file.");
        return fullPath;
    }, ct);

    public Task<string> GetMappingInfoAsync(CancellationToken ct) =>
        ConfigurationAsync(() => (string)CCall(CManager(), "ProduceMappingInfo")!, ct);

    public Task<string> ApplyMappingInfoAsync(string xml, CancellationToken ct) => ConfigurationAsync(() =>
    {
        ConfigurationXml.Parse(xml, "VarLinks");
        CCall(CManager(), "ConsumeMappingInfo", xml);
        return "Mapping XML applied. Save the project to persist.";
    }, ct);

    public Task<string> LinkVariablesAsync(string source, string target, bool unlink, CancellationToken ct) => ConfigurationAsync(() =>
    {
        // Resolve both paths first so invalid paths fail before a mutation.
        WithItem(source, _ => true); WithItem(target, _ => true);
        if (unlink) CCall(CManager(), "UnlinkVariables", source, target);
        else CCall(CManager(), "LinkVariables", source, target, 0, 0, 0);
        return $"{source} -> {target}";
    }, ct);

    public Task<string> RunMappingCommandAsync(bool checkOnly, CancellationToken ct) => ConfigurationAsync(() =>
    {
        if (_dte is null || _openProjectPath is null) throw new InvalidOperationException("Open or attach to a solution first.");
        var projectName = _selectedTwinCatProject;
        if (projectName is null)
        {
            dynamic projects = _dte.Solution.Projects;
            for (var i = 1; i <= (int)projects.Count; i++)
            {
                dynamic project = projects.Item(i);
                if (((string)project.FullName).EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase)) { projectName = (string)project.Name; break; }
            }
        }
        if (projectName is null) throw new InvalidOperationException("No TwinCAT project is available.");
        // Select explicitly; never run against whichever project happens to have UI focus.
        dynamic explorer = _dte.ToolWindows.SolutionExplorer;
        RetryIfComBusy(() => explorer.GetItem(Path.GetFileNameWithoutExtension(_openProjectPath) + "\\" + projectName).Select(1));
        var command = checkOnly ? "TwinCAT.CheckVariableLinks" : "TwinCAT.GenerateMappings";
        RetryIfComBusy(() => _dte.ExecuteCommand(command));
        return command;
    }, ct);

    public Task<XaeTreeItemInfo> CreateRealtimeTaskAsync(string name, bool withImage, CancellationToken ct) =>
        ConfigurationAsync(() => CCreate("TIRT", name, withImage ? 0 : 1, null), ct);

    public Task<XaeTreeItemInfo> AddTaskImageVariableAsync(string taskPath, string name, string dataType, bool output, CancellationToken ct) =>
        ConfigurationAsync(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dataType);
            return CCreate(taskPath + (output ? "^Outputs" : "^Inputs"), name, -1, dataType);
        }, ct);

    public Task<TaskConfiguration> GetTaskSettingsAsync(string treePath, CancellationToken ct) => ConfigurationAsync(() => WithItem(treePath, item =>
    {
        var definition = ConfigurationXml.Parse(CXml(item), "TreeItem").Root!.Element("TaskDef")
            ?? throw new ArgumentException("Tree item has no TaskDef.");
        return new TaskConfiguration((long?)definition.Element("CycleTime") / 10 ?? 0,
            (int?)definition.Element("Priority") ?? 0, definition.Element("CpuAffinity")?.Value);
    }), ct);

    public Task<string> ConfigureTaskAsync(string treePath, int? cycleTimeUs, int? priority, int? coreId, CancellationToken ct) => ConfigurationAsync(() => WithItem(treePath, item =>
    {
        if (cycleTimeUs is null && priority is null && coreId is null) throw new ArgumentException("Specify at least one task setting.");
        CConsume(item, ConfigurationXml.TaskSettings(CXml(item), cycleTimeUs, priority, coreId));
        return treePath;
    }), ct);

    public Task<string> ConfigureRealtimeCoresAsync(int total, int isolated, CpuCoreConfiguration[] cores, CancellationToken ct) => ConfigurationAsync(() =>
    {
        var xml = ConfigurationXml.RealtimeCores(total, isolated, cores);
        return WithItem("TIRS", item => { CConsume(item, xml); return "Project CPU settings updated; no SetOnTarget command was issued."; });
    }, ct);

    public Task<XaeTreeItemInfo> AddPlcProjectAsync(string name, string template, CancellationToken ct) =>
        ConfigurationAsync(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(template);
            return CCreate("TIPC", name, 0, template);
        }, ct);

    private static string PlcRoot(string name) { ValidateName(name); return $"TIPC^{name}^{name} Project"; }

    public Task<string> ChangePlcReferenceAsync(string plcName, string library, string version, string company, string? placeholder, bool remove, CancellationToken ct) =>
        ConfigurationAsync(() => WithItem(PlcRoot(plcName) + "^References", item =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(library);
            if (remove) CCall(item, "Library.RemoveReference", library, version, company);
            else if (placeholder is not null) CCall(item, "Library.AddPlaceholder", placeholder, library, version, company);
            else CCall(item, "Library.AddLibrary", library, version, company);
            return library;
        }), ct);

    public Task<string[]> ImportPlcFilesAsync(string parentPath, string[] paths, CancellationToken ct) => ConfigurationAsync(() =>
    {
        if (paths.Length == 0) throw new ArgumentException("Specify at least one PLC source file.");
        var fullPaths = paths.Select(Path.GetFullPath).ToArray();
        foreach (var path in fullPaths)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("PLC source file not found.", path);
            if (!new[] { ".TcPOU", ".TcGVL", ".TcDUT" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Import supports .TcPOU, .TcGVL and .TcDUT files.");
            ConfigurationXml.Parse(File.ReadAllText(path), "TcPlcObject");
        }
        WithItem(parentPath, parent =>
        {
            var imported = CCall(parent, "CreateChild", null, 58, null, fullPaths.Length == 1 ? (object)fullPaths[0] : fullPaths);
            RunningXaeShells.Release(imported);
            return true;
        });
        return fullPaths;
    }, ct);

    public Task<XaeTreeItemInfo> AddPlcTaskAsync(string plcName, string taskName, string programName, CancellationToken ct) => ConfigurationAsync(() =>
    {
        ValidateName(taskName); ValidateName(programName);
        var root = PlcRoot(plcName);
        // Require the real-time task to exist; a failed lookup is never interpreted as permission to create one.
        WithItem("TIRT^" + taskName, _ => true);
        var task = CCreate(root, taskName, 621, null);
        CCreate(task.TreePath, programName, 650, null);
        return task;
    }, ct);

    public Task<string> SetPlcBootProjectAsync(string plcName, bool autostart, CancellationToken ct) => ConfigurationAsync(() =>
    {
        ValidateName(plcName);
        return WithItem("TIPC^" + plcName, item => { CCall(item, "Plc.SetBootProject", autostart); return plcName; });
    }, ct);

    public Task<bool> CheckPlcObjectsAsync(string plcName, CancellationToken ct) => ConfigurationAsync(() =>
        WithItem(PlcRoot(plcName), item => (bool)CCall(item, "Plc.CheckAllObjects")!), ct);

    public Task<string> SetPlcLibraryParameterAsync(string plcName, string libraryItemName, string parameterName, string value, CancellationToken ct) =>
        ConfigurationAsync(() =>
        {
            ValidateName(libraryItemName);
            ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
            return WithItem(PlcRoot(plcName) + "^References^" + libraryItemName, item =>
            {
                var document = ConfigurationXml.Parse(CXml(item), "TreeItem");
                var placeholder = document.Descendants("PlaceholderReference").SingleOrDefault()
                    ?? throw new ArgumentException("Library item is not a placeholder reference.");
                var name = parameterName.ToUpperInvariant();
                var parameterList = placeholder.Element("ParameterList");
                if (parameterList is null) { parameterList = new XElement("ParameterList"); placeholder.Add(parameterList); }
                var parameter = parameterList.Elements("Parameter").SingleOrDefault(p => string.Equals((string?)p.Element("Name"), name, StringComparison.OrdinalIgnoreCase));
                if (parameter is null) { parameter = new XElement("Parameter", new XElement("Name", name)); parameterList.Add(parameter); }
                parameter.SetElementValue("Value", value);
                // Only send the library placeholder section, never unrelated executable XML Methods.
                var definition = document.Root!.Element("PlcLibPlaceholder") ?? throw new ArgumentException("Missing PlcLibPlaceholder section.");
                CConsume(item, new XElement("TreeItem", new XElement(definition)).ToString());
                var readback = ConfigurationXml.Parse(CXml(item), "TreeItem").Descendants("Parameter")
                    .FirstOrDefault(p => string.Equals((string?)p.Element("Name"), name, StringComparison.OrdinalIgnoreCase));
                if ((string?)readback?.Element("Value") != value)
                    throw new InvalidOperationException("Library parameter did not match after ConsumeXml; inspect the placeholder before retrying.");
                return name;
            });
        }, ct);
}
