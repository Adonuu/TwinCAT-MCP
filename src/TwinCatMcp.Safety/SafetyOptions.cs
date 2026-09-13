namespace TwinCatMcp.Safety;

/// <summary>
/// The runtime safety policy, expressed as three independent toggles. Each starts <c>false</c> — a fresh
/// server blocks live ADS symbol writes, configuration downloads, and runtime state changes until an
/// operator (or the agent, via <c>set_safety_toggles</c>) deliberately flips one on for the session.
///
/// These are seeded from configuration (appsettings.json / environment variables such as
/// <c>Safety__AllowAdsWrites</c>) at startup and can be changed live at runtime — they are not the whole
/// model: every mutating operation is still recorded to the append-only audit log regardless of verdict.
/// </summary>
public sealed class SafetyOptions
{
    public const string SectionName = "Safety";

    /// <summary>
    /// When true, the agent may write live PLC variable values via ADS
    /// (<c>write_symbol</c> / <c>write_symbols_batch</c>).
    /// </summary>
    public bool AllowAdsWrites { get; set; } = false;

    /// <summary>
    /// When true, the agent may download/activate a configuration to the runtime
    /// (<c>activate_configuration</c>) — the operation that stops, reloads, and restarts the PLC.
    /// </summary>
    public bool AllowDownloads { get; set; } = false;

    /// <summary>
    /// When true, the agent may change runtime state — <c>set_plc_state</c> (Run/Stop/Reset/Config)
    /// and <c>restart_twincat</c> (cold runtime restart).
    /// </summary>
    public bool AllowRuntimeStateChanges { get; set; } = false;
}