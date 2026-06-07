using TwinCatMcp.Source.Models;

namespace TwinCatMcp.Source;

/// <summary>
/// Generates minimal, well-formed &lt;TcPlcObject&gt; skeleton XML for new POUs/GVLs/DUTs. This is the
/// *only* place in the server that mints a new object GUID — every other code path treats GUIDs as
/// immutable identity keys the IDE owns. The skeleton intentionally omits &lt;LineIds&gt;; TwinCAT
/// generates them itself the first time it opens a newly-added file.
/// </summary>
public static class PlcObjectTemplates
{
    public static string CreateSkeletonXml(PlcObjectKind kind, string name, Guid guid)
    {
        var idAttribute = $"{{{guid}}}";
        return kind switch
        {
            PlcObjectKind.Pou => Pou(name, idAttribute),
            PlcObjectKind.Gvl => Gvl(name, idAttribute),
            PlcObjectKind.Dut => Dut(name, idAttribute),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static string Pou(string name, string id) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<TcPlcObject Version=\"1.1.0.1\" ProductVersion=\"3.1.4024.12\">\r\n" +
        $"  <POU Name=\"{name}\" Id=\"{id}\" SpecialFunc=\"None\">\r\n" +
        "    <Declaration><![CDATA[" + $"PROGRAM {name}\r\nVAR\r\nEND_VAR\r\n" + "]]></Declaration>\r\n" +
        "    <Implementation>\r\n" +
        "      <ST><![CDATA[]]></ST>\r\n" +
        "    </Implementation>\r\n" +
        "  </POU>\r\n" +
        "</TcPlcObject>\r\n";

    private static string Gvl(string name, string id) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<TcPlcObject Version=\"1.1.0.1\" ProductVersion=\"3.1.4024.12\">\r\n" +
        $"  <GVL Name=\"{name}\" Id=\"{id}\">\r\n" +
        "    <Declaration><![CDATA[" + $"{{attribute 'qualified_only'}}\r\nVAR_GLOBAL\r\nEND_VAR\r\n" + "]]></Declaration>\r\n" +
        "  </GVL>\r\n" +
        "</TcPlcObject>\r\n";

    private static string Dut(string name, string id) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<TcPlcObject Version=\"1.1.0.1\" ProductVersion=\"3.1.4024.12\">\r\n" +
        $"  <DUT Name=\"{name}\" Id=\"{id}\">\r\n" +
        "    <Declaration><![CDATA[" + $"TYPE {name} :\r\nSTRUCT\r\nEND_STRUCT\r\nEND_TYPE\r\n" + "]]></Declaration>\r\n" +
        "  </DUT>\r\n" +
        "</TcPlcObject>\r\n";
}
