namespace TwinCatMcp.Automation;

/// <summary>
/// Configuration for driving the TwinCAT XAE Shell through its COM-based Automation Interface.
///
/// This is the most environment-fragile part of the server: it requires Windows, a local TwinCAT XAE
/// Shell install, and the exact DTE ProgID that ships with that install (which has changed across
/// TwinCAT/Visual-Studio-shell versions — Beckhoff's samples use values like "TcXaeShell.DTE.15.0" for
/// VS2017-based shells). Rather than guess and hard-code one, it's surfaced as config so a deployment can
/// match its actual install.
/// </summary>
public sealed class AutomationOptions
{
    public const string SectionName = "Automation";

    /// <summary>
    /// COM ProgID of the XAE Shell's DTE object, e.g. "TcXaeShell.DTE.15.0". Check
    /// HKEY_CLASSES_ROOT on the engineering workstation for the exact installed version if unsure.
    /// </summary>
    public string DteProgId { get; set; } = "TcXaeShell.DTE.15.0";

    /// <summary>Whether to make the XAE Shell window visible while the server drives it (useful when developing/debugging; usually false in production).</summary>
    public bool ShowIde { get; set; }

    /// <summary>How long to wait for a build to finish before giving up, in seconds.</summary>
    public int BuildTimeoutSeconds { get; set; } = 600;
}
