using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwinCAT.Ads;
using TwinCatMcp.Runtime.Models;
using TwinCatMcp.Safety;

namespace TwinCatMcp.Runtime;

/// <summary>
/// Owns the single long-lived <see cref="AdsClient"/> the server talks to a TwinCAT runtime through.
///
/// All ADS access funnels through here rather than each tool creating its own client: connecting is
/// comparatively expensive (it opens an AMS port and, for remote targets, negotiates a route), and a
/// shared connection lets <c>RuntimeTools</c> methods stay simple — "give me a connected client" — while
/// this type owns the lifecycle (lazy connect, target-change reconnects, disposal on shutdown).
///
/// This type also owns the session's configured default target (<see cref="RuntimeOptions"/>): it can be
/// changed live with <see cref="SetTarget"/> (audited, reset to the configured defaults on restart) — the
/// same pattern as the safety toggles.
/// </summary>
public sealed class AdsConnectionManager : IAsyncDisposable
{
    private readonly RuntimeOptions _options;
    private readonly OperationAuditLog _audit;
    private readonly ILogger<AdsConnectionManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AdsClient? _client;
    private AmsAddress? _connectedAddress;

    public AdsConnectionManager(IOptions<RuntimeOptions> options, OperationAuditLog audit, ILogger<AdsConnectionManager> logger)
    {
        _options = options.Value;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>The address of the currently-connected target, or null if not yet connected.</summary>
    public AmsAddress? CurrentAddress => _connectedAddress;

    /// <summary>Snapshot of the session's configured default target — what a tool call uses when it doesn't specify one.</summary>
    public RuntimeTargetState CurrentTarget => new(_options.AmsNetId, _options.AmsPort);

    /// <summary>
    /// Changes the session's default ADS target live — the values tools fall back to when a call doesn't
    /// pass an explicit target. Each <c>null</c> argument leaves that setting unchanged; passing a blank
    /// <paramref name="amsNetId"/> clears the default so the local (loopback) runtime is used. The change
    /// is audit-logged and is session-scoped — a server restart resets the defaults to configuration.
    /// </summary>
    public RuntimeTargetState SetTarget(string? amsNetId, int? port)
    {
        var changes = new List<string>(2);
        if (amsNetId is not null)
        {
            if (string.IsNullOrWhiteSpace(amsNetId))
            {
                _options.AmsNetId = null;
                changes.Add("AmsNetId=null (local loopback)");
            }
            else if (AmsNetId.TryParse(amsNetId, out var parsed))
            {
                _options.AmsNetId = parsed.ToString();
                changes.Add($"AmsNetId={_options.AmsNetId}");
            }
            else
            {
                throw new ArgumentException($"Invalid AMS Net ID '{amsNetId}' — expected the dotted form like '127.0.0.1.1.1'.", nameof(amsNetId));
            }
        }

        if (port is { } p)
        {
            if (p is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), p, "AMS port must be between 1 and 65535.");

            _options.AmsPort = p;
            changes.Add($"AmsPort={p}");
        }

        _audit.Record(
            "SetRuntimeTarget",
            changes.Count > 0 ? string.Join(", ", changes) : "no changes requested",
            SafetyDecision.Allow("Runtime target updated for this session; reset to the configured default on server restart."));

        return CurrentTarget;
    }

    /// <summary>
    /// Returns a connected <see cref="AdsClient"/> for the given target, (re)connecting if necessary —
    /// either because this is the first call, the previous connection dropped, or the caller asked for a
    /// different target than the one currently connected.
    /// </summary>
    public async Task<AdsClient> EnsureConnectedAsync(string? amsNetId = null, int? port = null, CancellationToken ct = default)
    {
        var address = ResolveAddress(amsNetId, port);

        await _gate.WaitAsync(ct);
        try
        {
            if (_client is { IsConnected: true } client && Equals(_connectedAddress, address))
                return client;

            DisposeClientLocked();

            var newClient = new AdsClient();
            await newClient.ConnectAsync(address, ct);
            _client = newClient;
            _connectedAddress = address;
            _logger.LogInformation("Connected to ADS target {NetId}:{Port}", address.NetId, address.Port);
            return newClient;
        }
        finally
        {
            _gate.Release();
        }
    }

    private AmsAddress ResolveAddress(string? amsNetId, int? port)
    {
        var netId = !string.IsNullOrWhiteSpace(amsNetId)
            ? AmsNetId.Parse(amsNetId)
            : !string.IsNullOrWhiteSpace(_options.AmsNetId)
                ? AmsNetId.Parse(_options.AmsNetId)
                : AmsNetId.Local;

        return new AmsAddress(netId, port ?? _options.AmsPort);
    }

    private void DisposeClientLocked()
    {
        if (_client is null)
            return;

        try { _client.Disconnect(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Ignoring error while disconnecting previous ADS client."); }

        _client.Dispose();
        _client = null;
        _connectedAddress = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { DisposeClientLocked(); }
        finally { _gate.Release(); }
    }
}
