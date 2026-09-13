using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TwinCatMcp.Safety;

namespace TwinCatMcp.Server;

/// <summary>
/// MCP tools for viewing and changing the three runtime safety toggles (allow ADS symbol writes, allow
/// configuration downloads, allow runtime state changes) live, mid-session — no restart needed. Viewing
/// and changing are themselves recorded to the audit log, so every policy flip is traceable.
/// </summary>
[McpServerToolType]
public static class SafetyTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [McpServerTool, Description("Reports the current safety toggle state: AllowAdsWrites (live symbol writes via write_symbol/write_symbols_batch), " +
        "AllowDownloads (configuration download via activate_configuration), and AllowRuntimeStateChanges (set_plc_state, restart_twincat). " +
        "Toggles default to false and can be changed live with set_safety_toggles.")]
    public static string GetSafetyToggles(SafetyGate safety)
        => Serialize(new
        {
            Msg = "Current safety toggles. Change these live with set_safety_toggles.",
            safety.Snapshot().AllowAdsWrites,
            safety.Snapshot().AllowDownloads,
            safety.Snapshot().AllowRuntimeStateChanges,
        });

    [McpServerTool, Description("Changes the safety toggles at runtime for the current session (reset to the configured defaults on server restart). " +
        "Omit a toggle to leave it unchanged — pass one or more of allowAdsWrites, allowDownloads, allowRuntimeStateChanges to turn capability on/off immediately. " +
        "Enabling AllowAdsWrites permits write_symbol/write_symbols_batch; AllowDownloads permits activate_configuration (the download that stops/reloads the PLC); " +
        "AllowRuntimeStateChanges permits set_plc_state and restart_twincat. Source file edits, build/clean, and PLC object create/delete/import/export are never gated " +
        "by these toggles. Every change is recorded to the audit log.")]
    public static string SetSafetyToggles(
        SafetyGate safety,
        [Description("Allow the agent to write live ADS symbol values (write_symbol / write_symbols_batch). Omit to leave unchanged.")] bool? allowAdsWrites = null,
        [Description("Allow the agent to download/activate a configuration to the runtime (activate_configuration) — stops, reloads, and restarts the PLC. Omit to leave unchanged.")] bool? allowDownloads = null,
        [Description("Allow the agent to change runtime state: set_plc_state (Run/Stop/Reset/Config) and restart_twincat. Omit to leave unchanged.")] bool? allowRuntimeStateChanges = null)
    {
        var state = safety.SetToggles(allowAdsWrites, allowDownloads, allowRuntimeStateChanges);
        return Serialize(new
        {
            state.AllowAdsWrites,
            state.AllowDownloads,
            state.AllowRuntimeStateChanges,
            Msg = "Safety toggles updated for this session. Confirmed via get_safety_toggles.",
        });
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}