namespace TwinCatMcp.Source.Models;

/// <summary>The TwinCAT XAE object kinds this server can browse and edit, keyed to their file extensions.</summary>
public enum PlcObjectKind
{
    Pou,
    Gvl,
    Dut,
}

public static class PlcObjectKindExtensions
{
    public static string FileExtension(this PlcObjectKind kind) => kind switch
    {
        PlcObjectKind.Pou => ".TcPOU",
        PlcObjectKind.Gvl => ".TcGVL",
        PlcObjectKind.Dut => ".TcDUT",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The root child element name TwinCAT uses inside &lt;TcPlcObject&gt; for this kind (POU/GVL/DUT).</summary>
    public static string RootElementName(this PlcObjectKind kind) => kind switch
    {
        PlcObjectKind.Pou => "POU",
        PlcObjectKind.Gvl => "GVL",
        PlcObjectKind.Dut => "DUT",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool TryFromExtension(string extension, out PlcObjectKind kind)
    {
        switch (extension.ToUpperInvariant())
        {
            case ".TCPOU": kind = PlcObjectKind.Pou; return true;
            case ".TCGVL": kind = PlcObjectKind.Gvl; return true;
            case ".TCDUT": kind = PlcObjectKind.Dut; return true;
            default: kind = default; return false;
        }
    }
}
