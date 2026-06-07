namespace TwinCatMcp.Safety;

public enum SafetyVerdict
{
    /// <summary>The operation may proceed.</summary>
    Allowed,

    /// <summary>The operation is blocked by policy (SafeMode, an allow-list miss, or a disallowed transition).</summary>
    Denied,

    /// <summary>The operation would be allowed, but policy requires the caller to re-invoke with <c>confirm=true</c>.</summary>
    RequiresConfirmation,
}

/// <summary>
/// The outcome of a <see cref="SafetyGate"/> check, returned verbatim to the LLM in every mutating tool's
/// result so both the agent and a human reviewing the transcript can see *why* something was allowed,
/// blocked, or needs explicit confirmation — never a silent no-op.
/// </summary>
public sealed record SafetyDecision(SafetyVerdict Verdict, string Reason)
{
    public bool IsAllowed => Verdict == SafetyVerdict.Allowed;

    public static SafetyDecision Allow(string reason) => new(SafetyVerdict.Allowed, reason);
    public static SafetyDecision Deny(string reason) => new(SafetyVerdict.Denied, reason);
    public static SafetyDecision NeedsConfirmation(string reason) => new(SafetyVerdict.RequiresConfirmation, reason);
}
