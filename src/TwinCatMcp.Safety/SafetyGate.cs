using Microsoft.Extensions.Options;

namespace TwinCatMcp.Safety;

/// <summary>
/// The single chokepoint every mutating operation — PLC source edits, live symbol writes, runtime state
/// changes, and XAE build/automation operations — must pass through before doing anything irreversible.
///
/// Centralizing this (rather than scattering checks across <c>RuntimeTools</c>/<c>AutomationTools</c>/
/// <c>SourceTools</c>) means there is exactly one place to read, test, and reason about "what can an
/// agent actually do to this system" — and exactly one place that writes to the audit trail, so nothing
/// mutating can slip through unrecorded.
///
/// Every check both returns a <see cref="SafetyDecision"/> *and* records it to the
/// <see cref="OperationAuditLog"/> — the caller never has to remember to audit separately, and a denied
/// or confirmation-pending attempt is just as visible in the trail as an allowed one.
/// </summary>
public sealed class SafetyGate
{
    private readonly SafetyOptions _options;
    private readonly OperationAuditLog _audit;

    public SafetyGate(IOptions<SafetyOptions> options, OperationAuditLog audit)
    {
        _options = options.Value;
        _audit = audit;
    }

    /// <summary>Whether the gate is in read-only mode — exposed so tools can short-circuit and report it directly.</summary>
    public bool SafeMode => _options.SafeMode;

    /// <summary>Gate for writing a live PLC symbol value via ADS.</summary>
    public SafetyDecision CheckSymbolWrite(string symbolName, bool confirm)
    {
        var decision = Evaluate(
            allowListed: GlobPattern.AnyMatch(_options.WritableSymbolPatterns, symbolName),
            notAllowListedReason: $"Symbol '{symbolName}' does not match any entry in Safety:WritableSymbolPatterns.",
            requiresConfirmation: false,
            confirm: confirm);

        _audit.Record("WriteSymbol", symbolName, decision);
        return decision;
    }

    /// <summary>Gate for editing a PLC source file's declaration or implementation text.</summary>
    public SafetyDecision CheckSourceEdit(string relativePath, bool confirm)
    {
        var decision = Evaluate(
            allowListed: GlobPattern.AnyMatch(_options.WritableSourcePathPatterns, relativePath),
            notAllowListedReason: $"'{relativePath}' does not match any entry in Safety:WritableSourcePathPatterns.",
            requiresConfirmation: false,
            confirm: confirm);

        _audit.Record("EditSource", relativePath, decision);
        return decision;
    }

    /// <summary>Gate for changing PLC runtime state (Run/Stop/Reset/Restart).</summary>
    public SafetyDecision CheckStateTransition(string fromState, string toState, bool confirm)
    {
        var transition = $"{fromState}->{toState}";
        var decision = Evaluate(
            allowListed: _options.AllowedStateTransitions.Contains(transition, StringComparer.OrdinalIgnoreCase),
            notAllowListedReason: $"Transition '{transition}' is not listed in Safety:AllowedStateTransitions.",
            requiresConfirmation: _options.AlwaysConfirmStateTransitions,
            confirm: confirm);

        _audit.Record("SetPlcState", transition, decision);
        return decision;
    }

    /// <summary>
    /// Gate for an XAE Shell automation operation (build, clean, activate configuration, restart, ...).
    /// Build/clean/read-error operations are generally not destructive, so they're not allow-list-gated —
    /// but anything named in Safety:AlwaysConfirmAutomationOperations still requires explicit confirmation,
    /// and SafeMode still blocks all of them, since "build" can trigger code generation that touches the
    /// project tree and "restart" affects a running physical system.
    /// </summary>
    public SafetyDecision CheckAutomationOperation(string operationName, bool confirm)
    {
        var requiresConfirmation = _options.AlwaysConfirmAutomationOperations
            .Contains(operationName, StringComparer.OrdinalIgnoreCase);

        var decision = Evaluate(
            allowListed: true,
            notAllowListedReason: "(unreachable — automation operations are not allow-list-gated)",
            requiresConfirmation: requiresConfirmation,
            confirm: confirm);

        _audit.Record("Automation", operationName, decision);
        return decision;
    }

    private SafetyDecision Evaluate(bool allowListed, string notAllowListedReason, bool requiresConfirmation, bool confirm)
    {
        if (_options.SafeMode)
            return SafetyDecision.Deny("Server is running in SafeMode (Safety:SafeMode = true) — all mutating operations are denied. Read/browse/search tools remain available.");

        if (!allowListed)
            return SafetyDecision.Deny(notAllowListedReason);

        if (requiresConfirmation && !confirm)
            return SafetyDecision.NeedsConfirmation("This operation requires explicit confirmation — re-invoke the same tool call with confirm=true to proceed.");

        return SafetyDecision.Allow("Permitted by current safety policy.");
    }
}
