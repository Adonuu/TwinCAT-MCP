using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwinCatMcp.Automation.Models;
using TwinCatMcp.Safety;
using Xunit;

namespace TwinCatMcp.Automation.IntegrationTests;

public sealed class ConfigurationTests
{
    [Fact]
    public void TaskPatchConvertsBothTimeUnitsAndPreservesOmittedSettings()
    {
        const string current = "<TreeItem><TaskDef><CycleTime>10000</CycleTime><Priority>9</Priority></TaskDef><Methods><Unexpected>true</Unexpected></Methods>" +
            "<TcModuleInstance><Module><Contexts><Context id='7'><Id>9</Id><CycleTime>1000000</CycleTime></Context></Contexts></Module></TcModuleInstance></TreeItem>";
        var patch = XDocument.Parse(ConfigurationXml.TaskSettings(current, 2500, null, 63));
        Assert.Equal("25000", patch.Root!.Element("TaskDef")!.Element("CycleTime")!.Value);
        Assert.Null(patch.Root.Element("TaskDef")!.Element("Priority"));
        Assert.Equal("#x8000000000000000", patch.Descendants("CpuAffinity").Single().Value);
        var context = patch.Descendants("Context").Single();
        Assert.Equal("7", (string?)context.Attribute("id"));
        Assert.Equal("9", context.Element("Id")!.Value);
        Assert.Equal("2500000", context.Element("CycleTime")!.Value);
        Assert.Empty(patch.Descendants("Methods"));
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(-1, null, null)]
    [InlineData(null, 0, null)]
    [InlineData(null, 256, null)]
    [InlineData(null, null, -1)]
    [InlineData(null, null, 64)]
    public void TaskSettingsRejectInvalidValues(int? cycle, int? priority, int? core)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ConfigurationXml.TaskSettings("<TreeItem><TaskDef/></TreeItem>", cycle, priority, core));

    [Fact]
    public void XmlRejectsEntitiesAndWrongRootAndAdapterEscapesValues()
    {
        Assert.Throws<XmlException>(() => ConfigurationXml.Parse("<!DOCTYPE TreeItem [<!ENTITY x SYSTEM 'file:///secret'>]><TreeItem>&x;</TreeItem>", "TreeItem"));
        Assert.Throws<ArgumentException>(() => ConfigurationXml.Parse("<TreeItem/>", "VarLinks"));
        var xml = XDocument.Parse(ConfigurationXml.Adapter("A&B <adapter>", "{850112C3-9B3A-4F81-BE3D-02E4EC4917C3}", "001122AABBCC"));
        Assert.Equal("A&B <adapter>", xml.Descendants("DeviceDesc").Single().Value);
        Assert.Equal("001122aabbcc", xml.Descendants("DeviceData").Single().Value);
        Assert.StartsWith(@"\DEVICE\{", xml.Descendants("DeviceName").Single().Value);
        Assert.Throws<ArgumentException>(() => ConfigurationXml.Adapter("Adapter", "invalid", "001122AABBCC"));
        Assert.Throws<ArgumentException>(() => ConfigurationXml.Adapter("Adapter", Guid.Empty.ToString(), "not-a-mac"));
    }

    [Fact]
    public void RealtimePatchSetsZeroIsolationExplicitlyAndUsesLongTimeUnits()
    {
        var patch = XDocument.Parse(ConfigurationXml.RealtimeCores(8, 0, [new(7, int.MaxValue)]));
        Assert.Equal("0", patch.Descendants("MaxCPUs").Single().Attribute("NonWindowsCPUs")!.Value);
        Assert.Equal("21474836470", patch.Descendants("BaseTime").Single().Value);
        Assert.Empty(patch.Descendants("Methods"));
        Assert.Throws<ArgumentException>(() => ConfigurationXml.RealtimeCores(8, 2, [new(7, 100), new(7, 200)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfigurationXml.RealtimeCores(8, 9, []));
    }

    [Fact]
    public async Task WrappersUseStaAndCorrectCreateArgumentsAndSurfaceXmlFailures()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dispatcher = new StaThreadDispatcher();
        var fake = new FakeDispatch();
        await using var session = Session(dispatcher, fake);
        var master = await session.CreateEtherCatMasterAsync("Master", false, default);
        Assert.Equal("TIID^Master", master.TreePath);
        Assert.Equal(111, master.ItemSubType);
        var simulation = await session.CreateEtherCatMasterAsync("Simulation", true, default);
        Assert.Equal(144, simulation.ItemSubType);
        var box = await session.CreateEtherCatBoxAsync(master.TreePath, "Coupler", "EK1100", "Existing sibling", default);
        Assert.Equal(9099, box.ItemSubType);
        var create = fake.Calls.Last(c => c.Member == "CreateChild");
        Assert.Equal(new object?[] { "Coupler", 9099, "Existing sibling", "EK1100" }, create.Args);
        Assert.All(fake.Threads, thread => Assert.Equal(ApartmentState.STA, thread));

        fake.XmlError = "Invalid adapter";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetDeviceAdapterAsync(master.TreePath, "Adapter", Guid.Empty.ToString(), "001122334455", default));
        Assert.Contains("Invalid adapter", error.Message);
    }

    [Fact]
    public async Task MappingsValidateBeforeMutatingAndUseDocumentedSignatures()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dispatcher = new StaThreadDispatcher();
        var fake = new FakeDispatch();
        await using var session = Session(dispatcher, fake);
        await Assert.ThrowsAsync<ArgumentException>(() => session.ApplyMappingInfoAsync("<TreeItem/>", default));
        Assert.DoesNotContain(fake.Calls, c => c.Member == "ConsumeMappingInfo");
        await session.ApplyMappingInfoAsync("<VarLinks/>", default);
        await session.LinkVariablesAsync("Source", "Target", false, default);
        Assert.Equal(new object?[] { "Source", "Target", 0, 0, 0 }, fake.Calls.Single(c => c.Member == "LinkVariables").Args);
        await session.LinkVariablesAsync("Source", "Target", true, default);
        Assert.Equal(new object?[] { "Source", "Target" }, fake.Calls.Single(c => c.Member == "UnlinkVariables").Args);
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.GetMappingInfoAsync(new CancellationToken(true)));
        Assert.DoesNotContain(fake.Calls, c => c.Member == "ProduceMappingInfo");
    }

    [Fact]
    public async Task PlcHelpersUseNativeImportAndRequireAnExistingRealtimeTask()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dispatcher = new StaThreadDispatcher();
        var fake = new FakeDispatch();
        await using var session = Session(dispatcher, fake);
        await session.AddPlcProjectAsync("Controller", "Standard PLC Template", default);
        await session.ChangePlcReferenceAsync("Controller", "Tc2_Standard", "*", "Beckhoff", "Standard", false, default);
        Assert.Equal(new object?[] { "Standard", "Tc2_Standard", "*", "Beckhoff" }, fake.Calls.Single(c => c.Member == "Library.AddPlaceholder").Args);
        fake.MissingPath = "TIRT^Background";
        var count = fake.Calls.Count(c => c.Member == "CreateChild");
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AddPlcTaskAsync("Controller", "Background", "MAIN", default));
        Assert.Equal(count, fake.Calls.Count(c => c.Member == "CreateChild"));
        fake.MissingPath = null;
        await session.AddPlcTaskAsync("Controller", "Background", "MAIN", default);
        Assert.Equal(new[] { 621, 650 }, fake.Calls.Where(c => c.Member == "CreateChild").TakeLast(2).Select(c => (int)c.Args[1]!));

        var path = Path.Combine(Path.GetTempPath(), $"McpImport-{Guid.NewGuid():N}.TcPOU");
        try
        {
            await File.WriteAllTextAsync(path, "<TcPlcObject><POU Name='Example'/></TcPlcObject>");
            await session.ImportPlcFilesAsync("TIPC^Controller^Controller Project", [path], default);
            var call = fake.Calls.Last(c => c.Member == "CreateChild");
            Assert.Null(call.Args[0]);
            Assert.Equal(58, call.Args[1]);
            Assert.Equal(path, call.Args[3]);
            var creations = fake.Calls.Count(c => c.Member == "CreateChild");
            await Assert.ThrowsAsync<FileNotFoundException>(() => session.ImportPlcFilesAsync("TIPC", [path, path + ".missing"], default));
            Assert.Equal(creations, fake.Calls.Count(c => c.Member == "CreateChild"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DryRunsAndBlockedBootGenerationNeverTouchComAndAreAudited()
    {
        var path = Path.Combine(Path.GetTempPath(), $"configuration-audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            var gate = new SafetyGate(Options.Create(new SafetyOptions()), new OperationAuditLog(path, NullLogger<OperationAuditLog>.Instance));
            // A null session proves these paths don't invoke COM, create a thread or start an IDE.
            var dry = JsonDocument.Parse(await ConfigurationTools.CreateEthercatMaster(null!, gate, "Master", dryRun: true));
            Assert.False(dry.RootElement.GetProperty("Applied").GetBoolean());
            Assert.True(dry.RootElement.GetProperty("DryRun").GetBoolean());
            var blocked = JsonDocument.Parse(await ConfigurationTools.SetPlcBootProject(null!, gate, "Controller", true));
            Assert.False(blocked.RootElement.GetProperty("Applied").GetBoolean());
            Assert.False(blocked.RootElement.GetProperty("Succeeded").GetBoolean());
            Assert.Contains("AllowDownloads", blocked.RootElement.GetProperty("SafetyReason").GetString());
            var audit = File.ReadAllText(path);
            Assert.Contains("CreateEthercatMaster: Master", audit);
            Assert.Contains("SetPlcBootProject: Controller", audit);
            Assert.Contains("Denied", audit);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FailedComOperationReportsPossiblePartialChange()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"configuration-audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var dispatcher = new StaThreadDispatcher();
            var fake = new FakeDispatch { MissingPath = "TIID" };
            await using var session = Session(dispatcher, fake);
            var gate = new SafetyGate(Options.Create(new SafetyOptions()), new OperationAuditLog(path, NullLogger<OperationAuditLog>.Instance));
            using var json = JsonDocument.Parse(await ConfigurationTools.CreateEthercatMaster(session, gate, "Master"));
            Assert.False(json.RootElement.GetProperty("Succeeded").GetBoolean());
            Assert.False(json.RootElement.GetProperty("Applied").GetBoolean());
            Assert.True(json.RootElement.GetProperty("MayHaveApplied").GetBoolean());
            Assert.Contains("Missing task", json.RootElement.GetProperty("Error").GetString());
        }
        finally { File.Delete(path); }
    }

    private static XaeShellSession Session(StaThreadDispatcher dispatcher, FakeDispatch fake) =>
        new(dispatcher, Options.Create(new AutomationOptions()), NullLogger<XaeShellSession>.Instance,
            () => [], fake, () => fake.Manager);

    private sealed class Item(string path, int subtype = 0)
    {
        public string Path { get; } = path;
        public int Subtype { get; } = subtype;
    }

    private sealed class FakeDispatch : IConfigurationDispatch
    {
        public object Manager { get; } = new();
        public string? XmlError { get; set; }
        public string? MissingPath { get; set; }
        public List<(string Member, object?[] Args)> Calls { get; } = [];
        public List<ApartmentState> Threads { get; } = [];
        public object? Invoke(object target, string member, params object?[] arguments)
        {
            Calls.Add((member, arguments));
            Threads.Add(Thread.CurrentThread.GetApartmentState());
            return member switch
            {
                "LookupTreeItem" when (string)arguments[0]! == MissingPath => throw new InvalidOperationException("Missing task"),
                "LookupTreeItem" => new Item((string)arguments[0]!),
                "CreateChild" => new Item(((Item)target).Path + "^" + arguments[0], (int)arguments[1]!),
                "GetLastXmlError" => XmlError ?? "",
                _ => null,
            };
        }
        public object? Get(object target, string member) => member switch
        {
            "Name" => ((Item)target).Path.Split('^').Last(),
            "PathName" => ((Item)target).Path,
            "ItemSubType" => ((Item)target).Subtype,
            "ItemType" => 0,
            _ => throw new InvalidOperationException(member),
        };
        public object? GetIndexed(object target, string member, int index) => throw new NotSupportedException();
    }
}
