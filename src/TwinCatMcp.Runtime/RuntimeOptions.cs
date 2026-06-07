namespace TwinCatMcp.Runtime;

/// <summary>
/// Connection defaults for the ADS client. All values can be overridden per-call (e.g. <c>ConnectAds</c>
/// accepting an explicit <c>amsNetId</c>/<c>port</c>) — these are just what the server uses when a tool
/// call doesn't specify a target.
/// </summary>
public sealed class RuntimeOptions
{
    public const string SectionName = "Runtime";

    /// <summary>AMS Net ID of the default ADS target. Null/empty means the local runtime (loopback).</summary>
    public string? AmsNetId { get; set; }

    /// <summary>AMS port of the default ADS target — 851 is TwinCAT 3 PLC runtime 1.</summary>
    public int AmsPort { get; set; } = 851;
}
