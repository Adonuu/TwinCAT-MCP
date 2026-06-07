using System.Collections.Concurrent;
using TwinCAT.Ads;
using TwinCatMcp.Runtime.Models;

namespace TwinCatMcp.Runtime;

/// <summary>
/// Bridges ADS's push-based change notifications to MCP's request/response model.
///
/// An MCP tool call can't receive a server-initiated push — there's no open channel to push *through*
/// between calls. So instead of handing notifications straight to the caller, this hub subscribes on the
/// caller's behalf, buffers every sample that arrives in memory, and lets <c>PollSubscription</c> drain
/// that buffer on demand. <c>SubscribeToChanges</c> → repeated <c>PollSubscription</c> → <c>Unsubscribe</c>
/// is the resulting MCP-side shape of "watch this symbol for changes".
/// </summary>
public sealed class NotificationHub : IAsyncDisposable
{
    private const int MaxBufferedSamplesPerSubscription = 1000;

    private readonly AdsConnectionManager _connections;
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();
    private readonly object _hookGate = new();
    private AdsClient? _hookedClient;

    public NotificationHub(AdsConnectionManager connections)
    {
        _connections = connections;
    }

    public async Task<SubscriptionInfo> SubscribeAsync(string symbolPath, AdsTransMode mode, int cycleTimeMs, int dataSize, CancellationToken ct)
    {
        var client = await _connections.EnsureConnectedAsync(ct: ct);
        HookClient(client);

        var settings = new NotificationSettings(mode, cycleTimeMs, cycleTimeMs);
        var result = await client.AddDeviceNotificationAsync(symbolPath, dataSize, settings, userData: null, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"AddDeviceNotification failed for '{symbolPath}': {result.ErrorCode} ({result.ErrorCode:D})");

        var id = Guid.NewGuid().ToString("N");
        var subscription = new Subscription(id, symbolPath, result.Handle, mode, cycleTimeMs, client);
        _subscriptions[id] = subscription;

        return new SubscriptionInfo(id, symbolPath, mode.ToString(), cycleTimeMs);
    }

    public PollResult? Poll(string subscriptionId, int maxSamples)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
            return null;

        var drained = subscription.Drain(maxSamples);
        return new PollResult(subscriptionId, drained, StillActive: true);
    }

    public async Task<bool> UnsubscribeAsync(string subscriptionId, CancellationToken ct)
    {
        if (!_subscriptions.TryRemove(subscriptionId, out var subscription))
            return false;

        try
        {
            await subscription.Client.DeleteDeviceNotificationAsync(subscription.Handle, ct);
        }
        catch (AdsErrorException)
        {
            // The connection may already have dropped — the subscription is gone from our side either way.
        }

        return true;
    }

    public IReadOnlyList<SubscriptionInfo> ListSubscriptions() =>
        _subscriptions.Values
            .Select(s => new SubscriptionInfo(s.Id, s.SymbolPath, s.Mode.ToString(), s.CycleTimeMs))
            .ToArray();

    private void HookClient(AdsClient client)
    {
        lock (_hookGate)
        {
            if (ReferenceEquals(_hookedClient, client))
                return;

            if (_hookedClient is { } previous)
                previous.AdsNotification -= OnNotification;

            client.AdsNotification += OnNotification;
            _hookedClient = client;
        }
    }

    private void OnNotification(object? sender, AdsNotificationEventArgs e)
    {
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.Handle == e.Handle)
            {
                subscription.Buffer(new NotificationSample(e.TimeStamp, e.Data.ToArray()));
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_hookGate)
        {
            if (_hookedClient is { } client)
                client.AdsNotification -= OnNotification;
            _hookedClient = null;
        }

        foreach (var id in _subscriptions.Keys.ToArray())
            await UnsubscribeAsync(id, CancellationToken.None);
    }

    private sealed class Subscription(string id, string symbolPath, uint handle, AdsTransMode mode, int cycleTimeMs, AdsClient client)
    {
        private readonly Queue<NotificationSample> _samples = new();
        private readonly object _lock = new();

        public string Id { get; } = id;
        public string SymbolPath { get; } = symbolPath;
        public uint Handle { get; } = handle;
        public AdsTransMode Mode { get; } = mode;
        public int CycleTimeMs { get; } = cycleTimeMs;
        public AdsClient Client { get; } = client;

        public void Buffer(NotificationSample sample)
        {
            lock (_lock)
            {
                _samples.Enqueue(sample);
                while (_samples.Count > MaxBufferedSamplesPerSubscription)
                    _samples.Dequeue();
            }
        }

        public IReadOnlyList<NotificationSample> Drain(int maxSamples)
        {
            lock (_lock)
            {
                var take = Math.Min(maxSamples, _samples.Count);
                var result = new List<NotificationSample>(take);
                for (var i = 0; i < take; i++)
                    result.Add(_samples.Dequeue());
                return result;
            }
        }
    }
}
