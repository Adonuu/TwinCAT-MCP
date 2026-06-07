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
        var options = new SafetyOptions { SafeMode = false };
        configure?.Invoke(options);
        var audit = new OperationAuditLog(_logPath, NullLogger<OperationAuditLog>.Instance);
        return new SafetyGate(Options.Create(options), audit);
    }

    [Fact]
    public void SafeMode_denies_symbol_write_even_when_allow_listed()
    {
        var gate = CreateGate(o =>
        {
            o.SafeMode = true;
            o.WritableSymbolPatterns.Add("MAIN.*");
        });

        var decision = gate.CheckSymbolWrite("MAIN.cmd_start", confirm: true);

        Assert.Equal(SafetyVerdict.Denied, decision.Verdict);
        Assert.Contains("SafeMode", decision.Reason);
    }

    [Fact]
    public void Symbol_write_denied_when_not_allow_listed()
    {
        var gate = CreateGate(o => o.WritableSymbolPatterns.Add("MAIN.cmd_*"));

        var decision = gate.CheckSymbolWrite("Safety.eStop", confirm: true);

        Assert.Equal(SafetyVerdict.Denied, decision.Verdict);
        Assert.Contains("WritableSymbolPatterns", decision.Reason);
    }

    [Theory]
    [InlineData("MAIN.cmd_start")]
    [InlineData("main.CMD_STOP")] // glob matching is case-insensitive
    public void Symbol_write_allowed_when_glob_matches(string symbolName)
    {
        var gate = CreateGate(o => o.WritableSymbolPatterns.Add("MAIN.cmd_*"));

        var decision = gate.CheckSymbolWrite(symbolName, confirm: true);

        Assert.Equal(SafetyVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    public void Source_edit_denied_when_path_not_allow_listed()
    {
        var gate = CreateGate(o => o.WritableSourcePathPatterns.Add("POUs/Generated/*"));

        var decision = gate.CheckSourceEdit("POUs/MAIN.TcPOU", confirm: true);

        Assert.Equal(SafetyVerdict.Denied, decision.Verdict);
    }

    [Fact]
    public void Source_edit_allowed_when_path_glob_matches()
    {
        var gate = CreateGate(o => o.WritableSourcePathPatterns.Add("POUs/Generated/*"));

        var decision = gate.CheckSourceEdit("POUs/Generated/Foo.TcPOU", confirm: true);

        Assert.Equal(SafetyVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    public void State_transition_denied_when_not_in_allow_list()
    {
        var gate = CreateGate(o => o.AllowedStateTransitions.Add("Run->Stop"));

        var decision = gate.CheckStateTransition("Run", "Reset", confirm: true);

        Assert.Equal(SafetyVerdict.Denied, decision.Verdict);
    }

    [Fact]
    public void State_transition_requires_confirmation_even_when_allow_listed()
    {
        var gate = CreateGate(o =>
        {
            o.AllowedStateTransitions.Add("Run->Stop");
            o.AlwaysConfirmStateTransitions = true;
        });

        var withoutConfirm = gate.CheckStateTransition("Run", "Stop", confirm: false);
        var withConfirm = gate.CheckStateTransition("Run", "Stop", confirm: true);

        Assert.Equal(SafetyVerdict.RequiresConfirmation, withoutConfirm.Verdict);
        Assert.Equal(SafetyVerdict.Allowed, withConfirm.Verdict);
    }

    [Fact]
    public void State_transition_allowed_without_confirmation_when_policy_disables_it()
    {
        var gate = CreateGate(o =>
        {
            o.AllowedStateTransitions.Add("Run->Stop");
            o.AlwaysConfirmStateTransitions = false;
        });

        var decision = gate.CheckStateTransition("Run", "Stop", confirm: false);

        Assert.Equal(SafetyVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    public void Automation_operation_in_always_confirm_list_requires_confirmation()
    {
        var gate = CreateGate(o => o.AlwaysConfirmAutomationOperations = ["RestartTwinCat"]);

        var withoutConfirm = gate.CheckAutomationOperation("RestartTwinCat", confirm: false);
        var withConfirm = gate.CheckAutomationOperation("RestartTwinCat", confirm: true);

        Assert.Equal(SafetyVerdict.RequiresConfirmation, withoutConfirm.Verdict);
        Assert.Equal(SafetyVerdict.Allowed, withConfirm.Verdict);
    }

    [Fact]
    public void Automation_operation_not_in_always_confirm_list_does_not_require_confirmation()
    {
        var gate = CreateGate(o => o.AlwaysConfirmAutomationOperations = ["RestartTwinCat"]);

        var decision = gate.CheckAutomationOperation("BuildProject", confirm: false);

        Assert.Equal(SafetyVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    public void Every_check_is_recorded_to_the_audit_log()
    {
        var gate = CreateGate(o => o.WritableSymbolPatterns.Add("MAIN.*"));

        gate.CheckSymbolWrite("MAIN.cmd_start", confirm: true);
        gate.CheckSymbolWrite("Safety.eStop", confirm: true);

        var lines = File.ReadAllLines(_logPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("MAIN.cmd_start", lines[0]);
        Assert.Contains("Allowed", lines[0]);
        Assert.Contains("Safety.eStop", lines[1]);
        Assert.Contains("Denied", lines[1]);
    }
}
