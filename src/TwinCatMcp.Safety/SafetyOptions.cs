namespace TwinCatMcp.Safety;

/// <summary>
/// Operator-controlled policy for everything that can change PLC source, live variable values, runtime
/// state, or the XAE project/build. Bound from configuration (appsettings.json / environment variables)
/// so an operator can tune the "blast radius" of an autonomously-acting agent without recompiling.
///
/// Ship with conservative defaults: <see cref="SafeMode"/> on and every allow-list empty. An operator must
/// deliberately opt in to write access — fail-closed, not fail-open.
/// </summary>
public sealed class SafetyOptions
{
    public const string SectionName = "Safety";

    /// <summary>
    /// Path to the append-only JSON-lines audit log file. Relative paths are resolved against the
    /// process's working directory. Defaults to a file alongside wherever the server is run from.
    /// </summary>
    public string AuditLogPath { get; set; } = "twincat-mcp-audit.jsonl";

    /// <summary>
    /// When true (the default), every mutating operation — source edits, symbol writes, state changes,
    /// build/automation ops — is denied outright. Read/browse/search tools remain fully available.
    /// </summary>
    public bool SafeMode { get; set; } = true;

    /// <summary>
    /// Glob patterns (e.g. "MAIN.cmd_*", "HMI.*") matched case-insensitively against the dotted ADS symbol
    /// name. A symbol write is only permitted if it matches at least one pattern here.
    /// </summary>
    public List<string> WritableSymbolPatterns { get; set; } = [];

    /// <summary>
    /// Glob patterns matched against a PLC object's project-relative path (e.g. "POUs/Generated/*"). A
    /// source edit is only permitted if its file matches at least one pattern here.
    /// </summary>
    public List<string> WritableSourcePathPatterns { get; set; } = [];

    /// <summary>
    /// Allowed runtime state transitions, written as "From-&gt;To" (e.g. "Run-&gt;Stop", "Stop-&gt;Run").
    /// A transition not listed here is denied outright, regardless of <see cref="SafeMode"/>; one that
    /// *is* listed still requires the caller to pass <c>confirm=true</c> (see <see cref="AlwaysConfirmStateTransitions"/>).
    /// </summary>
    public List<string> AllowedStateTransitions { get; set; } = [];

    /// <summary>
    /// When true (the default), every allowed state transition still requires explicit <c>confirm=true</c>
    /// from the caller — creating an unmissable two-step "I intend to change runtime state" → "yes, do it"
    /// flow that shows up in the agent's tool-call transcript for human review.
    /// </summary>
    public bool AlwaysConfirmStateTransitions { get; set; } = true;

    /// <summary>
    /// Automation-interface operation names (e.g. "ActivateConfiguration", "RestartTwinCat") that always
    /// require <c>confirm=true</c> even when automation operations are otherwise enabled.
    /// </summary>
    public List<string> AlwaysConfirmAutomationOperations { get; set; } =
        ["ActivateConfiguration", "RestartTwinCat", "DeletePlcObject"];
}
