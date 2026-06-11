using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using TwinCatMcp.Runtime.Models;
using TwinCatMcp.Safety;

namespace TwinCatMcp.Runtime;

/// <summary>
/// MCP tools for live access to a running TwinCAT runtime over ADS — reading and writing symbol values,
/// browsing the symbol tree, invoking RPC methods, watching for changes, and controlling run state.
/// Read/browse tools are always available; anything that writes a value or changes runtime state is
/// routed through <see cref="SafetyGate"/> first (see that type and <c>SAFETY.md</c> for the policy).
/// </summary>
[McpServerToolType]
public static class RuntimeTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [McpServerTool, Description("Connects (or reconnects) to a TwinCAT ADS target and reports the resulting connection. " +
        "Most other Runtime tools call this implicitly, so you only need it to switch targets or verify connectivity.")]
    public static async Task<string> ConnectAds(
        AdsConnectionManager connections,
        [Description("AMS Net ID of the target, e.g. '127.0.0.1.1.1'. Omit to use the configured default (or the local runtime).")] string? amsNetId = null,
        [Description("AMS port of the target, e.g. 851 for PLC runtime 1. Omit to use the configured default.")] int? port = null,
        CancellationToken cancellationToken = default)
    {
        var client = await connections.EnsureConnectedAsync(amsNetId, port, cancellationToken);
        var address = connections.CurrentAddress!;
        return Serialize(new ConnectResult(client.IsConnected, address.NetId.ToString(), address.Port, client.IsLocal));
    }

    [McpServerTool, Description("Reads the current value of a single ADS symbol by its dotted instance path (e.g. 'MAIN.nCounter' or 'GVL_Globals.G_MAX_RETRIES'). " +
        "Returns the value as JSON — primitives map directly, structs/arrays come back as nested objects/arrays.")]
    public static async Task<string> ReadSymbol(
        SymbolBrowser browser,
        [Description("Dotted instance path of the symbol to read.")] string symbol,
        CancellationToken cancellationToken = default)
    {
        return Serialize(await ReadOneAsync(browser, symbol, cancellationToken));
    }

    [McpServerTool, Description("Reads several ADS symbols in one call — convenient for grabbing a related group of variables (e.g. a recipe struct's fields) without round-tripping per symbol.")]
    public static async Task<string> ReadSymbolsBatch(
        SymbolBrowser browser,
        [Description("Dotted instance paths of the symbols to read.")] string[] symbols,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SymbolReadResult>(symbols.Length);
        foreach (var symbol in symbols)
            results.Add(await ReadOneAsync(browser, symbol, cancellationToken));

        return Serialize(results);
    }

    [McpServerTool, Description("Writes a value to a live ADS symbol. Gated by the safety policy (SafeMode, WritableSymbolPatterns, confirm) — " +
        "pass dryRun=true to see what the gate would decide without writing anything.")]
    public static async Task<string> WriteSymbol(
        SymbolBrowser browser,
        SafetyGate safety,
        [Description("Dotted instance path of the symbol to write.")] string symbol,
        [Description("The value to write. Primitives are passed directly; for structs/arrays pass a JSON object/array matching the symbol's shape.")] JsonElement value,
        [Description("If true, evaluate the safety decision and report it without writing. Default false.")] bool dryRun = false,
        [Description("Required to proceed once the safety policy says the write is allowed but needs confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var decision = safety.CheckSymbolWrite(symbol, confirm);
        if (!decision.IsAllowed || dryRun)
            return Serialize(new SymbolWriteResult(symbol, Applied: false, Succeeded: false, Error: null, decision.Reason));

        return Serialize(await WriteOneAsync(browser, symbol, value, decision.Reason, cancellationToken));
    }

    [McpServerTool, Description("Writes several ADS symbols in one call, each gated independently by the safety policy. " +
        "Returns one result per write so partial application — and exactly why each write was allowed, denied, or needs confirmation — is visible.")]
    public static async Task<string> WriteSymbolsBatch(
        SymbolBrowser browser,
        SafetyGate safety,
        [Description("Writes to perform, each as {\"symbol\": \"<instance path>\", \"value\": <JSON value>}.")] SymbolWriteRequest[] writes,
        [Description("If true, evaluate every safety decision and report them without writing anything. Default false.")] bool dryRun = false,
        [Description("Required for any write whose safety decision needs confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SymbolWriteResult>(writes.Length);

        foreach (var write in writes)
        {
            var decision = safety.CheckSymbolWrite(write.Symbol, confirm);
            if (!decision.IsAllowed || dryRun)
            {
                results.Add(new SymbolWriteResult(write.Symbol, Applied: false, Succeeded: false, Error: null, decision.Reason));
                continue;
            }

            results.Add(await WriteOneAsync(browser, write.Symbol, write.Value, decision.Reason, cancellationToken));
        }

        return Serialize(results);
    }

    [McpServerTool, Description("Browses the live symbol tree, optionally filtered to instance paths containing a substring (e.g. 'Recipe' to find everything related to a recipe struct). " +
        "Returns each symbol's instance path, type name, comment, read-only flag, and child count — use DescribeType for a deeper look at one symbol's members.")]
    public static async Task<string> BrowseSymbols(
        SymbolBrowser browser,
        [Description("Case-insensitive substring to match against instance paths. Omit to list everything reachable within maxDepth.")] string? prefix = null,
        [Description("How many levels of nested members to descend into from each top-level symbol. Default 1.")] int maxDepth = 1,
        [Description("Maximum number of symbols to return. Default 200.")] int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        var symbols = await browser.BrowseAsync(prefix, maxDepth, maxResults, cancellationToken);
        return Serialize(symbols);
    }

    [McpServerTool, Description("Describes a single symbol by its dotted instance path — its data type name, comment, read-only flag, and how many members it has. " +
        "Use BrowseSymbols with a deeper maxDepth to enumerate those members.")]
    public static async Task<string> DescribeType(
        SymbolBrowser browser,
        [Description("Dotted instance path of the symbol to describe.")] string symbol,
        CancellationToken cancellationToken = default)
    {
        var info = await browser.DescribeAsync(symbol, cancellationToken);
        return info is null
            ? Serialize(new { Found = false, Symbol = symbol })
            : Serialize(info);
    }

    [McpServerTool, Description("Invokes an RPC-enabled method (one declared with a TcRpcEnable pragma) on a live function block instance over ADS. " +
        "Returns the method's return value and any output parameters as JSON.")]
    public static async Task<string> CallMethod(
        AdsConnectionManager connections,
        [Description("Dotted instance path of the function block instance that owns the method, e.g. 'MAIN.fbRecipeManager'.")] string symbolPath,
        [Description("Name of the method to invoke.")] string methodName,
        [Description("Positional input arguments for the method, as JSON values. Omit for parameterless methods.")] JsonElement[]? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var client = await connections.EnsureConnectedAsync(ct: cancellationToken);
        var inParameters = (arguments ?? []).Select(a => ToClrValue(a)!).ToArray();

        try
        {
            var result = await client.InvokeRpcMethodAsync(symbolPath, methodName, inParameters, cancellationToken);
            return Serialize(new RpcCallResult(result.Succeeded, result.ReturnValue, result.OutValues, result.Succeeded ? null : result.ErrorCode.ToString()));
        }
        catch (AdsErrorException ex)
        {
            return Serialize(new RpcCallResult(Succeeded: false, ReturnValue: null, OutValues: null, Error: ex.Message));
        }
    }

    [McpServerTool, Description("Subscribes to value-change notifications for a symbol. Because MCP tools are request/response (the server can't push to the client), " +
        "samples are buffered server-side — call PollSubscription repeatedly to drain them, and Unsubscribe when done.")]
    public static async Task<string> SubscribeToChanges(
        AdsConnectionManager connections,
        NotificationHub notifications,
        [Description("Dotted instance path of the symbol to watch.")] string symbol,
        [Description("Size in bytes of the symbol's value (e.g. 4 for DINT/REAL, 1 for BOOL/BYTE). Required so ADS knows how much data to deliver per sample.")] int dataSize,
        [Description("Notification mode: 'OnChange' (only when the value changes) or 'Cyclic' (every cycle). Default 'OnChange'.")] string mode = "OnChange",
        [Description("How often TwinCAT checks for changes / delivers samples, in milliseconds. Default 100.")] int cycleTimeMs = 100,
        CancellationToken cancellationToken = default)
    {
        _ = await connections.EnsureConnectedAsync(ct: cancellationToken);
        var transMode = ParseTransMode(mode);
        var info = await notifications.SubscribeAsync(symbol, transMode, cycleTimeMs, dataSize, cancellationToken);
        return Serialize(info);
    }

    [McpServerTool, Description("Drains buffered samples for a subscription created by SubscribeToChanges. Each sample is the raw bytes ADS delivered plus a timestamp — " +
        "interpret them according to the symbol's known type (the same shape ReadSymbol would decode). Returns an empty list if nothing new has arrived.")]
    public static string PollSubscription(
        NotificationHub notifications,
        [Description("Subscription ID returned by SubscribeToChanges.")] string subscriptionId,
        [Description("Maximum number of buffered samples to return and consume. Default 50.")] int maxSamples = 50)
    {
        var result = notifications.Poll(subscriptionId, maxSamples);
        return result is null
            ? Serialize(new { Found = false, SubscriptionId = subscriptionId })
            : Serialize(new
            {
                result.SubscriptionId,
                result.StillActive,
                Samples = result.Samples.Select(s => new { s.TimeStamp, DataBase64 = Convert.ToBase64String(s.RawData) }),
            });
    }

    [McpServerTool, Description("Lists all currently-active change-notification subscriptions on this connection.")]
    public static string ListSubscriptions(NotificationHub notifications) => Serialize(notifications.ListSubscriptions());

    [McpServerTool, Description("Cancels a change-notification subscription created by SubscribeToChanges and discards any unread buffered samples.")]
    public static async Task<string> Unsubscribe(
        NotificationHub notifications,
        [Description("Subscription ID returned by SubscribeToChanges.")] string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        var removed = await notifications.UnsubscribeAsync(subscriptionId, cancellationToken);
        return Serialize(new { Removed = removed, SubscriptionId = subscriptionId });
    }

    [McpServerTool, Description("Reads the current ADS state (Run/Stop/Config/...) and device state of the connected target.")]
    public static async Task<string> GetPlcState(AdsConnectionManager connections, CancellationToken cancellationToken = default)
    {
        var client = await connections.EnsureConnectedAsync(ct: cancellationToken);
        var state = await client.ReadStateAsync(cancellationToken);
        return Serialize(ToStateInfo(state.State));
    }

    [McpServerTool, Description("Requests a PLC run-state transition (e.g. Run -> Stop, Stop -> Run, or Reset). High-impact and gated by the safety policy " +
        "(SafeMode, AllowedStateTransitions, confirm — state transitions always require confirm=true by default). Pass dryRun=true to preview the decision.")]
    public static async Task<string> SetPlcState(
        AdsConnectionManager connections,
        SafetyGate safety,
        [Description("Target ADS state to request: Run, Stop, Reset, or Config.")] string targetState,
        [Description("If true, evaluate the safety decision and report it without changing anything. Default false.")] bool dryRun = false,
        [Description("Required once the safety policy says the transition is allowed but needs confirmation (the default for all state transitions).")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var client = await connections.EnsureConnectedAsync(ct: cancellationToken);
        var current = await client.ReadStateAsync(cancellationToken);
        var currentState = ToStateInfo(current.State);

        var target = ParseAdsState(targetState);
        var decision = safety.CheckStateTransition(currentState.AdsState, target.ToString(), confirm);

        if (!decision.IsAllowed || dryRun)
            return Serialize(new StateChangeResult(Applied: false, Succeeded: false, Error: null, decision.Reason, ResultingState: currentState));

        try
        {
            var write = await client.WriteControlAsync(target, (ushort)current.State.DeviceState, cancellationToken);
            var resulting = await client.ReadStateAsync(cancellationToken);
            return Serialize(new StateChangeResult(write.Succeeded, write.Succeeded, write.Succeeded ? null : write.ErrorCode.ToString(), decision.Reason, ToStateInfo(resulting.State)));
        }
        catch (AdsErrorException ex)
        {
            return Serialize(new StateChangeResult(Applied: true, Succeeded: false, ex.Message, decision.Reason, ResultingState: currentState));
        }
    }

    /// <summary>One write request within a <see cref="WriteSymbolsBatch"/> call.</summary>
    public sealed record SymbolWriteRequest(string Symbol, JsonElement Value);

    private static async Task<SymbolReadResult> ReadOneAsync(SymbolBrowser browser, string symbol, CancellationToken ct)
    {
        try
        {
            if (await browser.FindSymbolAsync(symbol, ct) is not IValueSymbol valueSymbol)
                return new SymbolReadResult(symbol, false, null, $"Symbol '{symbol}' not found.");

            var result = await valueSymbol.ReadValueAsync(ct);
            return (AdsErrorCode)result.ErrorCode == AdsErrorCode.NoError
                ? new SymbolReadResult(symbol, true, JsonSerializer.SerializeToElement(result.Value, Json), null)
                : new SymbolReadResult(symbol, false, null, ((AdsErrorCode)result.ErrorCode).ToString());
        }
        catch (Exception ex)
        {
            return new SymbolReadResult(symbol, false, null, ex.Message);
        }
    }

    private static async Task<SymbolWriteResult> WriteOneAsync(SymbolBrowser browser, string symbol, JsonElement value, string safetyReason, CancellationToken ct)
    {
        try
        {
            if (await browser.FindSymbolAsync(symbol, ct) is not IValueSymbol valueSymbol)
                return new SymbolWriteResult(symbol, Applied: false, Succeeded: false, $"Symbol '{symbol}' not found.", safetyReason);

            var clrValue = ToClrValue(value) ?? throw new ArgumentException($"Cannot write a null/undefined value to symbol '{symbol}'.");
            var result = await valueSymbol.WriteValueAsync(clrValue, ct);
            var ok = (AdsErrorCode)result.ErrorCode == AdsErrorCode.NoError;
            return new SymbolWriteResult(symbol, Applied: true, ok, ok ? null : ((AdsErrorCode)result.ErrorCode).ToString(), safetyReason);
        }
        catch (Exception ex)
        {
            return new SymbolWriteResult(symbol, Applied: true, Succeeded: false, ex.Message, safetyReason);
        }
    }

    /// <summary>
    /// Converts a JSON value from an MCP tool argument into a CLR value <see cref="AdsClient.WriteAnyAsync"/> can
    /// marshal — primitives map directly; objects/arrays pass through as dictionaries/lists, which the ADS
    /// "any" marshaler resolves against the target symbol's actual type.
    /// </summary>
    private static object? ToClrValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => element.EnumerateArray().Select(ToClrValue).ToArray(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToClrValue(p.Value)),
        _ => throw new NotSupportedException($"Unsupported JSON value kind '{element.ValueKind}'."),
    };

    private static PlcStateInfo ToStateInfo(StateInfo state) => new(state.AdsState.ToString(), state.DeviceState);

    private static AdsState ParseAdsState(string targetState) =>
        Enum.TryParse<AdsState>(targetState, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException($"'{targetState}' is not a recognized ADS state (try Run, Stop, Reset, or Config).", nameof(targetState));

    private static AdsTransMode ParseTransMode(string mode) => mode switch
    {
        _ when string.Equals(mode, "OnChange", StringComparison.OrdinalIgnoreCase) => AdsTransMode.OnChange,
        _ when string.Equals(mode, "Cyclic", StringComparison.OrdinalIgnoreCase) => AdsTransMode.Cyclic,
        _ => throw new ArgumentException($"'{mode}' is not a recognized notification mode (try 'OnChange' or 'Cyclic').", nameof(mode)),
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
