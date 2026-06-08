namespace TwinCatMcp.Automation;

/// <summary>
/// Configuration for driving the TwinCAT XAE Shell through its COM-based Automation Interface.
///
/// This is the most environment-fragile part of the server: it requires Windows and a local TwinCAT
/// XAE Shell install. The COM ProgID of the shell's DTE object (e.g. "TcXaeShell.DTE.15.0" for
/// VS2017-based shells) varies across TwinCAT/Visual-Studio-shell versions — rather than ask a
/// deployment to find and configure that string, <see cref="XaeShellSession"/> discovers whatever's
/// actually registered on the machine directly from HKEY_CLASSES_ROOT.
/// </summary>
public sealed class AutomationOptions
{
    public const string SectionName = "Automation";

    /// <summary>Whether to make the XAE Shell window visible while the server drives it (useful when developing/debugging; usually false in production).</summary>
    public bool ShowIde { get; set; }

    /// <summary>How long to wait for a build to finish before giving up, in seconds.</summary>
    public int BuildTimeoutSeconds { get; set; } = 600;
}
