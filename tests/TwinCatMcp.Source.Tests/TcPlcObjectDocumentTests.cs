using System.Text;
using TwinCatMcp.Source;
using TwinCatMcp.Source.Models;
using Xunit;

namespace TwinCatMcp.Source.Tests;

/// <summary>
/// Round-trip safety is the whole point of <see cref="TcPlcObjectDocument"/>: the TwinCAT IDE treats these
/// files as its source of truth, and a write that reflows whitespace, regenerates a GUID, or coerces a
/// CDATA block into escaped text can make the IDE refuse to open the project cleanly. Every test here
/// works against a fresh copy of real-format TwinCAT XML (written verbatim, CRLF and all, to match what
/// the IDE itself produces) and asserts the file is byte-identical except for the one thing that was
/// deliberately changed.
/// </summary>
public sealed class TcPlcObjectDocumentTests : IDisposable
{
    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Real-format TwinCAT XML, reproduced verbatim (CRLF line endings, tabs in declarations) so tests
    // exercise the exact byte patterns the IDE itself writes.
    private const string MainPouXml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<TcPlcObject Version=\"1.1.0.1\" ProductVersion=\"3.1.4024.12\">\r\n" +
        "  <POU Name=\"MAIN\" Id=\"{ac002873-776d-4096-82aa-e6da7e9c1d13}\" SpecialFunc=\"None\">\r\n" +
        "    <Declaration><![CDATA[PROGRAM MAIN\r\n" +
        "VAR\r\n" +
        "\tnCounter : DINT := 0;\r\n" +
        "\tbStart : BOOL := FALSE;\r\n" +
        "\tbRunning : BOOL := FALSE;\r\n" +
        "END_VAR\r\n" +
        "]]></Declaration>\r\n" +
        "    <Implementation>\r\n" +
        "      <ST><![CDATA[IF bStart THEN\r\n" +
        "\tbRunning := TRUE;\r\n" +
        "\tnCounter := nCounter + 1;\r\n" +
        "END_IF\r\n" +
        "]]></ST>\r\n" +
        "    </Implementation>\r\n" +
        "    <LineIds Name=\"MAIN\">\r\n" +
        "      <LineId Id=\"3\" Count=\"1\" />\r\n" +
        "      <LineId Id=\"2\" Count=\"0\" />\r\n" +
        "    </LineIds>\r\n" +
        "  </POU>\r\n" +
        "</TcPlcObject>\r\n";

    private const string GvlGlobalsXml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<TcPlcObject Version=\"1.1.0.1\" ProductVersion=\"3.1.4024.12\">\r\n" +
        "  <GVL Name=\"GVL_Globals\" Id=\"{3f1a9b2c-5d6e-4a7f-8b9c-0d1e2f3a4b5c}\">\r\n" +
        "    <Declaration><![CDATA[{attribute 'qualified_only'}\r\n" +
        "VAR_GLOBAL\r\n" +
        "\tG_MAX_RETRIES : UINT := 3;\r\n" +
        "\tG_DEFAULT_TIMEOUT_MS : UDINT := 5000;\r\n" +
        "END_VAR\r\n" +
        "]]></Declaration>\r\n" +
        "  </GVL>\r\n" +
        "</TcPlcObject>\r\n";

    private const string StRecipeDutXml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<TcPlcObject Version=\"1.1.0.1\" ProductVersion=\"3.1.4024.12\">\r\n" +
        "  <DUT Name=\"ST_Recipe\" Id=\"{7c8d9e0f-1a2b-4c3d-9e8f-7a6b5c4d3e2f}\">\r\n" +
        "    <Declaration><![CDATA[TYPE ST_Recipe :\r\n" +
        "STRUCT\r\n" +
        "\tsName : STRING(80);\r\n" +
        "\tnTargetTemperature : INT;\r\n" +
        "\tnDurationSeconds : UDINT;\r\n" +
        "END_STRUCT\r\n" +
        "END_TYPE\r\n" +
        "]]></Declaration>\r\n" +
        "  </DUT>\r\n" +
        "</TcPlcObject>\r\n";

    private readonly string _workDir;

    public TcPlcObjectDocumentTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"twincat-mcp-source-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir))
            Directory.Delete(_workDir, recursive: true);
    }

    private string WriteFixture(string fileName, string xml)
    {
        var path = Path.Combine(_workDir, fileName);
        File.WriteAllText(path, xml, NoBom);
        return path;
    }

    [Fact]
    public void Load_reads_name_guid_and_kind_from_a_POU()
    {
        var path = WriteFixture("MAIN.TcPOU", MainPouXml);

        var doc = TcPlcObjectDocument.Load(path);

        Assert.Equal("MAIN", doc.Name);
        Assert.Equal("{ac002873-776d-4096-82aa-e6da7e9c1d13}", doc.Guid);
        Assert.Equal(PlcObjectKind.Pou, doc.Kind);
    }

    [Fact]
    public void Save_without_changes_produces_a_byte_identical_file()
    {
        var path = WriteFixture("MAIN.TcPOU", MainPouXml);
        var originalBytes = File.ReadAllBytes(path);

        var doc = TcPlcObjectDocument.Load(path);
        doc.Save();

        var savedBytes = File.ReadAllBytes(path);
        Assert.Equal(originalBytes, savedBytes);
    }

    [Fact]
    public void GetDeclarationText_returns_the_raw_CDATA_content()
    {
        var path = WriteFixture("MAIN.TcPOU", MainPouXml);
        var doc = TcPlcObjectDocument.Load(path);

        var declaration = doc.GetDeclarationText();

        Assert.Contains("PROGRAM MAIN", declaration);
        Assert.Contains("nCounter : DINT := 0;", declaration);
    }

    [Fact]
    public void GetImplementationText_returns_the_ST_body_and_language()
    {
        var path = WriteFixture("MAIN.TcPOU", MainPouXml);
        var doc = TcPlcObjectDocument.Load(path);

        Assert.Equal("ST", doc.GetImplementationLanguage());
        Assert.Contains("bRunning := TRUE;", doc.GetImplementationText());
    }

    [Fact]
    public void GVL_and_DUT_have_no_implementation()
    {
        var gvlPath = WriteFixture("GVL_Globals.TcGVL", GvlGlobalsXml);
        var dutPath = WriteFixture("ST_Recipe.TcDUT", StRecipeDutXml);

        var gvl = TcPlcObjectDocument.Load(gvlPath);
        var dut = TcPlcObjectDocument.Load(dutPath);

        Assert.Null(gvl.GetImplementationText());
        Assert.Null(dut.GetImplementationText());
        Assert.Contains("VAR_GLOBAL", gvl.GetDeclarationText());
        Assert.Contains("STRUCT", dut.GetDeclarationText());
    }

    [Fact]
    public void SetDeclarationText_changes_only_the_CDATA_value_preserving_everything_else()
    {
        var path = WriteFixture("MAIN.TcPOU", MainPouXml);
        var originalText = File.ReadAllText(path);
        const string newDeclaration = "PROGRAM MAIN\r\nVAR\r\n\tnCounter : DINT := 0;\r\n\tbStart : BOOL := FALSE;\r\n\tbRunning : BOOL := FALSE;\r\n\tnNewVariable : INT;\r\nEND_VAR\r\n";

        var doc = TcPlcObjectDocument.Load(path);
        doc.SetDeclarationText(newDeclaration);
        doc.Save();

        var rewritten = File.ReadAllText(path);

        // The new variable shows up...
        Assert.Contains("nNewVariable", rewritten);
        // ...the GUID is untouched...
        Assert.Contains("{ac002873-776d-4096-82aa-e6da7e9c1d13}", rewritten);
        // ...the CDATA wrapper survived (not escaped as &lt;![CDATA...)...
        Assert.Contains("<![CDATA[PROGRAM MAIN", rewritten);
        Assert.DoesNotContain("&lt;![CDATA[", rewritten);
        // ...LineIds untouched...
        Assert.Contains("<LineId Id=\"3\" Count=\"1\" />", rewritten);
        // ...the implementation block is completely unchanged...
        Assert.Contains("<ST><![CDATA[IF bStart THEN", rewritten);
        // ...and everything outside the edited CDATA is byte-for-byte identical.
        var (originalOutside, rewrittenOutside) = StripDeclarationCData(originalText, rewritten);
        Assert.Equal(originalOutside, rewrittenOutside);
    }

    [Fact]
    public void SetImplementationText_changes_only_the_ST_CDATA_value()
    {
        var path = WriteFixture("MAIN.TcPOU", MainPouXml);

        var doc = TcPlcObjectDocument.Load(path);
        doc.SetImplementationText("(* replaced body *)\r\nbRunning := FALSE;\r\n");
        doc.Save();

        var rewritten = File.ReadAllText(path);
        Assert.Contains("(* replaced body *)", rewritten);
        Assert.Contains("<![CDATA[(* replaced body *)", rewritten);
        Assert.Contains("PROGRAM MAIN", rewritten); // declaration untouched
        Assert.Contains("{ac002873-776d-4096-82aa-e6da7e9c1d13}", rewritten); // GUID untouched
    }

    [Fact]
    public void SetDeclarationText_on_a_GVL_round_trips_through_a_reload()
    {
        var path = WriteFixture("GVL_Globals.TcGVL", GvlGlobalsXml);
        const string updated = "{attribute 'qualified_only'}\r\nVAR_GLOBAL\r\n\tG_MAX_RETRIES : UINT := 5;\r\nEND_VAR\r\n";

        var doc = TcPlcObjectDocument.Load(path);
        doc.SetDeclarationText(updated);
        doc.Save();

        var reloaded = TcPlcObjectDocument.Load(path);
        Assert.Equal("GVL_Globals", reloaded.Name);
        Assert.Equal("{3f1a9b2c-5d6e-4a7f-8b9c-0d1e2f3a4b5c}", reloaded.Guid);
        Assert.Contains("G_MAX_RETRIES : UINT := 5;", reloaded.GetDeclarationText());
    }

    [Fact]
    public void SetImplementationText_on_a_GVL_throws_because_it_has_no_body()
    {
        var path = WriteFixture("GVL_Globals.TcGVL", GvlGlobalsXml);
        var doc = TcPlcObjectDocument.Load(path);

        Assert.Throws<InvalidDataException>(() => doc.SetImplementationText("won't work"));
    }

    [Fact]
    public void Save_rejects_text_containing_characters_that_are_illegal_in_XML_and_leaves_the_file_untouched()
    {
        // U+0001 is a valid .NET string character but illegal in XML 1.0 — XmlWriter refuses to
        // serialize it. Save() must propagate that failure (rather than writing a half-baked file)
        // so a bad in-place text edit can never reach the file the IDE has open.
        var path = WriteFixture("MAIN.TcPOU", MainPouXml);
        var originalBytes = File.ReadAllBytes(path);
        var doc = TcPlcObjectDocument.Load(path);

        doc.SetDeclarationText("PROGRAM MAIN\r\nVAR\r\n\tbBad : BOOL; (* contains  which is illegal in XML 1.0 *)\r\nEND_VAR\r\n");

        Assert.ThrowsAny<Exception>(() => doc.Save());
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
    }

    /// <summary>Returns both documents with their &lt;Declaration&gt;...&lt;/Declaration&gt; spans removed, for an "everything else is identical" comparison.</summary>
    private static (string Left, string Right) StripDeclarationCData(string left, string right)
    {
        return (Strip(left), Strip(right));

        static string Strip(string xml)
        {
            var start = xml.IndexOf("<Declaration>", StringComparison.Ordinal);
            var end = xml.IndexOf("</Declaration>", StringComparison.Ordinal) + "</Declaration>".Length;
            return xml.Remove(start, end - start);
        }
    }
}
