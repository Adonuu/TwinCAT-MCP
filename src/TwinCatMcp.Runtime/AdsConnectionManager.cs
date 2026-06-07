using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwinCAT.Ads;

namespace TwinCatMcp.Runtime;

/// <summary>
/// Owns the single long-lived <see cref="AdsClient"/> the server talks to a TwinCAT runtime through.
///
/// All ADS access funnels through here rather than each tool creating its own client: connecting is
/// comparatively expensive (it opens an AMS port and, for remote targets, negotiates a route), and a
/// shared connection lets <c>RuntimeTools</c> methods stay simple — "give me a connected client" — while
/// this type owns the lifecycle (lazy connect, target-change reconnects, disposal on shutdown).
/// </summary>
public sealed class AdsConnectionManager : IAsyncDisposable
{
    private readonly RuntimeOptions _options;
    private readonly ILogger<AdsConnectionManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AdsClient? _client;
    private AmsAddress? _connectedAddress;

    public AdsConnectionManager(IOptions<RuntimeOptions> options, ILogger<AdsConnectionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The address of the currently-connected target, or null if not yet connected.</summary>
    public AmsAddress? CurrentAddress => _connectedAddress;

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
