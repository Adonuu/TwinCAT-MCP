namespace TwinCatMcp.Safety;

public enum SafetyVerdict
{
    /// <summary>The operation may proceed.</summary>
    Allowed,

    /// <summary>The operation is blocked by policy (a safety toggle is off).</summary>
    Denied,
}

/// <summary>
/// The outcome of a <see cref="SafetyGate"/> check, returned verbatim to the LLM in every mutating tool's
/// result so both the agent and a human reviewing the transcript can see *why* something was allowed or
/// blocked — never a silent no-op.
/// </summary>
public sealed record SafetyDecision(SafetyVerdict Verdict, string Reason)
{
    public bool IsAllowed => Verdict == SafetyVerdict.Allowed;

    public static SafetyDecision Allow(string reason) => new(SafetyVerdict.Allowed, reason);
    public static SafetyDecision Deny(string reason) => new(SafetyVerdict.Denied, reason);
}

/// <summary>Immutable snapshot of the three safety toggles, returned by <see cref="SafetyGate.Snapshot"/>.</summary>
public sealed record SafetyToggleState(bool AllowAdsWrites, bool AllowDownloads, bool AllowRuntimeStateChanges);