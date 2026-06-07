namespace TwinCatMcp.Runtime.Models;

/// <summary>Result of (re)establishing the ADS connection.</summary>
public sealed record ConnectResult(bool Connected, string AmsNetId, int Port, bool IsLocal);

/// <summary>A single symbol read, success or failure.</summary>
public sealed record SymbolReadResult(string Symbol, bool Succeeded, object? Value, string? Error);

/// <summary>Outcome of writing a single symbol — including the <see cref="SafetyDecision"/> that gated it.</summary>
public sealed record SymbolWriteResult(string Symbol, bool Applied, bool Succeeded, string? Error, string SafetyReason);

/// <summary>One symbol's shape, as reported by <c>BrowseSymbols</c>.</summary>
public sealed record SymbolInfo(string InstancePath, string TypeName, string? Comment, bool IsReadOnly, int ChildCount);

/// <summary>Current ADS state of the connected target.</summary>
public sealed record PlcStateInfo(string AdsState, int DeviceState);

/// <summary>Outcome of a state-transition request — including the <see cref="SafetyDecision"/> that gated it.</summary>
public sealed record StateChangeResult(bool Applied, bool Succeeded, string? Error, string SafetyReason, PlcStateInfo? ResultingState);

/// <summary>Outcome of an RPC method invocation.</summary>
public sealed record RpcCallResult(bool Succeeded, object? ReturnValue, object?[]? OutValues, string? Error);

/// <summary>A handle to a buffered change-notification subscription.</summary>
public sealed record SubscriptionInfo(string SubscriptionId, string Symbol, string Mode, int CycleTimeMs);

/// <summary>One buffered notification sample, in arrival order.</summary>
public sealed record NotificationSample(System.DateTimeOffset TimeStamp, byte[] RawData);

/// <summary>The buffered samples returned (and consumed) by a poll.</summary>
public sealed record PollResult(string SubscriptionId, IReadOnlyList<NotificationSample> Samples, bool StillActive);
