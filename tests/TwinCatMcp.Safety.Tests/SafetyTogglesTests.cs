using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwinCatMcp.Safety;
using Xunit;

namespace TwinCatMcp.Safety.Tests;

public sealed class SafetyTogglesTests : IDisposable
{
    private readonly string _logPath;

    public SafetyTogglesTests()
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
    public void SetToggles_flips_each_toggle_independently()
    {
        var gate = CreateGate();

        gate.SetToggles(allowAdsWrites: true, allowDownloads: null, allowRuntimeStateChanges: null);

        var state = gate.Snapshot();
        Assert.True(state.AllowAdsWrites);
        Assert.False(state.AllowDownloads);
        Assert.False(state.AllowRuntimeStateChanges);

        gate.SetToggles(null, true, true);
        state = gate.Snapshot();
        Assert.True(state.AllowAdsWrites);
        Assert.True(state.AllowDownloads);
        Assert.True(state.AllowRuntimeStateChanges);
    }

    [Fact]
    public void SetToggles_off_reblocks_a_previously_allowed_operation()
    {
        var gate = CreateGate(o => o.AllowAdsWrites = true);
        Assert.Equal(SafetyVerdict.Allowed, gate.CheckSymbolWrite("MAIN.nCounter").Verdict);

        gate.SetToggles(allowAdsWrites: false, null, null);
        Assert.Equal(SafetyVerdict.Denied, gate.CheckSymbolWrite("MAIN.nCounter").Verdict);
    }

    [Fact]
    public void SetToggles_records_policy_change_to_the_audit_log()
    {
        var gate = CreateGate();

        gate.SetToggles(allowAdsWrites: true, allowRuntimeStateChanges: true, allowDownloads: null);

        var lines = File.ReadAllLines(_logPath);
        Assert.Single(lines);
        Assert.Contains("PolicyChanged", lines[0]);
        Assert.Contains("AllowAdsWrites=True", lines[0]);
        Assert.Contains("AllowRuntimeStateChanges=True", lines[0]);
    }

    [Fact]
    public void SetToggles_with_no_changes_is_still_audited()
    {
        var gate = CreateGate();

        gate.SetToggles(null, null, null);

        var lines = File.ReadAllLines(_logPath);
        Assert.Single(lines);
        Assert.Contains("no changes requested", lines[0]);
    }

    [Fact]
    public void Snapshot_is_a_copy_not_the_live_policy()
    {
        var gate = CreateGate();

        var snap = gate.Snapshot();
        snap = snap with { AllowAdsWrites = true };

        Assert.False(gate.Snapshot().AllowAdsWrites);
    }
}