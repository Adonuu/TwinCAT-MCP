using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwinCatMcp.Safety;
using Xunit;

namespace TwinCatMcp.Safety.Tests;

public sealed class SafetyGateTests : IDisposable
{
    private readonly string _logPath;

    public SafetyGateTests()
    {
        _logPath = Path.Combine(Path.GetTempPath(), $"twincat-mcp-audit-{Guid.NewGuid():N}.jsonl");
    }

    public void Dispose()
    {
        if (File.Exists(_logPath))
            File.Delete(_logPath);
    }

    private SafetyGate CreateGate(Action<SafetyOptions>? configure = null)
    {
        var options = new SafetyOptions();
        configure?.Invoke(options);
        var audit = new OperationAuditLog(_logPath, NullLogger<OperationAuditLog>.Instance);
        return new SafetyGate(Options.Create(options), audit);
    }

    [Fact]
    public void Symbol_write_denied_by_default()
    {
        var gate = CreateGate();

        var decision = gate.CheckSymbolWrite("MAIN.cmd_start");

        Assert.Equal(SafetyVerdict.Denied, decision.Verdict);
        Assert.Contains("AllowAdsWrites", decision.Reason);
    }

    [Fact]
    public void Symbol_write_allowed_when_allow_ads_writes_enabled()
    {
        var gate = CreateGate(o => o.AllowAdsWrites = true);

        var decision = gate.CheckSymbolWrite("MAIN.cmd_start");

        Assert.Equal(SafetyVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    public void State_transition_denied_by_default()
    {
        var gate = CreateGate();

        var decision = gate.CheckStateTransition("Run", "Stop");

        Assert.Equal(SafetyVerdict.Denied, decision.Verdict);
        Assert.Contains("AllowRuntimeStateChanges", decision.Reason);
    }

    [Fact]
    public void State_transition_allowed_when_runtime_state_changes_enabled()
    {
        var gate = CreateGate(o => o.AllowRuntimeStateChanges = true);

        var decision = gate.CheckStateTransition("Run", "Config");

        Assert.Equal(SafetyVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    public void Activate_configuration_denied_by_default()
    {
        var gate = CreateGate();

        var decision = gate.CheckAutomationOperation("ActivateConfiguration");

        Assert.Equal(SafetyVerdict.Denied, decision.Verdict);
        Assert.Contains("AllowDownloads", decision.Reason);
    }

    [Fact]
    public void Activate_configuration_allowed_when_downloads_enabled()
    {
        var gate = CreateGate(o => o.AllowDownloads = true);

        var decision = gate.CheckAutomationOperation("ActivateConfiguration");

        Assert.Equal(SafetyVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    public void Restart_twincat_denied_unless_runtime_state_changes_enabled()
    {
        var gate = CreateGate(o => o.AllowAdsWrites = true); // another toggle being on must not help

        var denied = gate.CheckAutomationOperation("RestartTwinCat");
        Assert.Equal(SafetyVerdict.Denied, denied.Verdict);

        var allowed = CreateGate(o => o.AllowRuntimeStateChanges = true).CheckAutomationOperation("RestartTwinCat");
        Assert.Equal(SafetyVerdict.Allowed, allowed.Verdict);
    }

    [Fact]
    public void Toggles_are_independent()
    {
        var gate = CreateGate(o => o.AllowAdsWrites = true);

        var write = gate.CheckSymbolWrite("MAIN.nCounter");
        var download = gate.CheckAutomationOperation("ActivateConfiguration");
        var stateChange = gate.CheckStateTransition("Run", "Stop");

        Assert.Equal(SafetyVerdict.Allowed, write.Verdict);
        Assert.Equal(SafetyVerdict.Denied, download.Verdict);
        Assert.Equal(SafetyVerdict.Denied, stateChange.Verdict);
    }

    [Fact]
    public void Source_edit_is_never_gated()
    {
        var gate = CreateGate();

        var decision = gate.CheckSourceEdit("POUs/MAIN.TcPOU");

        Assert.Equal(SafetyVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    public void Ungated_automation_operations_are_never_gated()
    {
        var gate = CreateGate();

        Assert.Equal(SafetyVerdict.Allowed, gate.CheckAutomationOperation("Build").Verdict);
        Assert.Equal(SafetyVerdict.Allowed, gate.CheckAutomationOperation("SaveProject").Verdict);
        Assert.Equal(SafetyVerdict.Allowed, gate.CheckAutomationOperation("DeletePlcObject").Verdict);
        Assert.Equal(SafetyVerdict.Allowed, gate.CheckAutomationOperation("WritePlcObjectDeclaration").Verdict);
    }

    [Fact]
    public void Every_check_is_recorded_to_the_audit_log()
    {
        var gate = CreateGate(o => o.AllowAdsWrites = true);

        gate.CheckSymbolWrite("MAIN.cmd_start");
        gate.CheckStateTransition("Run", "Stop");
        gate.CheckAutomationOperation("ActivateConfiguration");
        gate.CheckSourceEdit("POUs/MAIN.TcPOU");

        var lines = File.ReadAllLines(_logPath);
        Assert.Equal(4, lines.Length);
        Assert.Contains("WriteSymbol", lines[0]);
        Assert.Contains("Allowed", lines[0]);
        Assert.Contains("SetPlcState", lines[1]);
        Assert.Contains("Automation", lines[2]);
        Assert.Contains("Denied", lines[2]); // activation denied by default
        Assert.Contains("EditSource", lines[3]);
    }
}