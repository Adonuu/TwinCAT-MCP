using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwinCatMcp.Runtime;
using TwinCatMcp.Safety;

namespace TwinCatMcp.Runtime.Tests;

public sealed class AdsConnectionManagerTargetTests : IDisposable
{
    private readonly string _logPath;

    public AdsConnectionManagerTargetTests()
    {
        _logPath = Path.Combine(Path.GetTempPath(), $"twincat-mcp-audit-{Guid.NewGuid():N}.jsonl");
    }

    public void Dispose()
    {
        if (File.Exists(_logPath))
            File.Delete(_logPath);
    }

    private AdsConnectionManager CreateManager(Action<RuntimeOptions>? configure = null)
    {
        var options = new RuntimeOptions();
        configure?.Invoke(options);
        var audit = new OperationAuditLog(_logPath, NullLogger<OperationAuditLog>.Instance);
        return new AdsConnectionManager(Options.Create(options), audit, NullLogger<AdsConnectionManager>.Instance);
    }

    [Fact]
    public void SetTarget_updates_both_defaults()
    {
        var manager = CreateManager();

        var target = manager.SetTarget("192.168.10.1.1.1", 852);

        Assert.Equal("192.168.10.1.1.1", manager.CurrentTarget.AmsNetId);
        Assert.Equal(852, manager.CurrentTarget.AmsPort);
        Assert.Equal(target, manager.CurrentTarget);
    }

    [Fact]
    public void SetTarget_omit_leaves_each_setting_unchanged()
    {
        var manager = CreateManager(o => { o.AmsNetId = "192.168.10.1.1.1"; o.AmsPort = 852; });

        var onlyNetId = manager.SetTarget("10.0.0.2.1.1", null);
        Assert.Equal("10.0.0.2.1.1", onlyNetId.AmsNetId);
        Assert.Equal(852, onlyNetId.AmsPort);

        var onlyPort = manager.SetTarget(null, 851);
        Assert.Equal("10.0.0.2.1.1", onlyPort.AmsNetId);
        Assert.Equal(851, onlyPort.AmsPort);
    }

    [Fact]
    public void SetTarget_blank_netid_clears_default_to_local_loopback()
    {
        var manager = CreateManager(o => o.AmsNetId = "192.168.10.1.1.1");

        var cleared = manager.SetTarget("", null);

        Assert.Null(cleared.AmsNetId);
    }

    [Fact]
    public void SetTarget_invalid_netid_throws()
    {
        var manager = CreateManager();

        Assert.Throws<ArgumentException>(() => manager.SetTarget("not-a-net-id", null));
    }

    [Fact]
    public void SetTarget_rejects_out_of_range_ports()
    {
        var manager = CreateManager();

        Assert.Throws<ArgumentOutOfRangeException>(() => manager.SetTarget(null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => manager.SetTarget(null, 65536));
    }

    [Fact]
    public void SetTarget_records_change_to_the_audit_log()
    {
        var manager = CreateManager();

        manager.SetTarget("192.168.10.1.1.1", 852);

        var lines = File.ReadAllLines(_logPath);
        Assert.Single(lines);
        Assert.Contains("SetRuntimeTarget", lines[0]);
        Assert.Contains("AmsNetId=192.168.10.1.1.1", lines[0]);
        Assert.Contains("AmsPort=852", lines[0]);
    }

    [Fact]
    public void SetTarget_with_no_changes_is_still_audited()
    {
        var manager = CreateManager();

        manager.SetTarget(null, null);

        var lines = File.ReadAllLines(_logPath);
        Assert.Single(lines);
        Assert.Contains("no changes requested", lines[0]);
    }

    [Fact]
    public void SetTarget_does_not_alter_the_connected_address()
    {
        var manager = CreateManager();

        manager.SetTarget("192.168.10.1.1.1", 852);

        Assert.Null(manager.CurrentAddress); // no connect attempted; left to the next EnsureConnectedAsync
    }
}