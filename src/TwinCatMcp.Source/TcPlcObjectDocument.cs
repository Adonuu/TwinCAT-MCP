using System.Text;
using System.Xml;
using System.Xml.Linq;
using TwinCatMcp.Source.Models;

namespace TwinCatMcp.Source;

/// <summary>
/// Loads and saves a single TwinCAT &lt;TcPlcObject&gt; XML file (.TcPOU/.TcGVL/.TcDUT) without disturbing
/// anything the TwinCAT IDE cares about: GUIDs, &lt;LineIds&gt;, element order, whitespace, or CDATA wrappers.
///
/// The IDE treats these files as its source of truth and is picky about round-trips — a naive
/// load-mutate-save with default XDocument/XElement settings will reflow whitespace, coerce CDATA into
/// escaped text nodes, or reorder elements, any of which the IDE may refuse to open cleanly. Every method
/// here is deliberately narrow: it mutates exactly one CDATA text node's value and nothing else.
/// </summary>
public sealed class TcPlcObjectDocument
{
    private static readonly XmlWriterSettings SaveSettings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        NewLineChars = "\r\n",
        NewLineHandling = NewLineHandling.None,
        OmitXmlDeclaration = false,
    };

    private readonly XDocument _document;
    private readonly XElement _objectElement;

    public string FilePath { get; }
    public PlcObjectKind Kind { get; }
    public string Name { get; }
    public string Guid { get; }

    private TcPlcObjectDocument(string filePath, PlcObjectKind kind, XDocument document, XElement objectElement)
    {
        FilePath = filePath;
        Kind = kind;
        _document = document;
        _objectElement = objectElement;
        Name = (string?)objectElement.Attribute("Name") ?? throw new InvalidDataException($"'{filePath}': missing Name attribute");
        Guid = (string?)objectElement.Attribute("Id") ?? throw new InvalidDataException($"'{filePath}': missing Id attribute");
    }

    public static TcPlcObjectDocument Load(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (!PlcObjectKindExtensions.TryFromExtension(extension, out var kind))
            throw new NotSupportedException($"'{filePath}': unsupported TwinCAT object file extension '{extension}'");

        // PreserveWhitespace is the single most important load flag here — without it, XDocument
        // collapses/normalizes insignificant whitespace between elements, and re-saving would reflow
        // the whole file even if we never touch a node.
        var document = XDocument.Load(filePath, LoadOptions.PreserveWhitespace);

        var root = document.Root;
        if (root is null || root.Name.LocalName != "TcPlcObject")
            throw new InvalidDataException($"'{filePath}': expected root element <TcPlcObject>, found <{root?.Name.LocalName ?? "null"}>");

        var objectElement = root.Element(kind.RootElementName())
            ?? throw new InvalidDataException($"'{filePath}': expected <{kind.RootElementName()}> child of <TcPlcObject>");

        return new TcPlcObjectDocument(filePath, kind, document, objectElement);
    }

    /// <summary>The raw declaration text (the VAR…END_VAR / TYPE…END_TYPE block), exactly as stored.</summary>
    public string GetDeclarationText() => GetCData("Declaration", parent: _objectElement)?.Value
        ?? throw new InvalidDataException($"'{FilePath}': <{Kind.RootElementName()} Name=\"{Name}\"> has no <Declaration><![CDATA[...]]></Declaration>");

    /// <summary>The implementation (body) text — present for POUs, absent for GVLs/DUTs which have no executable body.</summary>
    public string? GetImplementationText() => FindImplementationCData()?.Value;

    /// <summary>The implementation language element name (e.g. "ST"), or null if this object has no implementation.</summary>
    public string? GetImplementationLanguage() => FindImplementationCData()?.Parent?.Name.LocalName;

    /// <summary>
    /// Replaces the declaration text in place by mutating the existing &lt;![CDATA[...]]&gt; node's
    /// <see cref="XCData.Value"/>. Never replace the node itself or assign to <see cref="XElement.Value"/> —
    /// both coerce the content into an escaped XML text node, destroying the CDATA wrapper TwinCAT expects.
    /// </summary>
    public void SetDeclarationText(string text)
    {
        var cdata = GetCData("Declaration", parent: _objectElement)
            ?? throw new InvalidDataException($"'{FilePath}': <{Kind.RootElementName()} Name=\"{Name}\"> has no <Declaration><![CDATA[...]]></Declaration> to edit");
        cdata.Value = text;
    }

    /// <summary>Same contract as <see cref="SetDeclarationText"/>, for the &lt;Implementation&gt;&lt;ST&gt;...&lt;/ST&gt;&lt;/Implementation&gt; body.</summary>
    public void SetImplementationText(string text)
    {
        var cdata = FindImplementationCData()
            ?? throw new InvalidDataException($"'{FilePath}': <{Kind.RootElementName()} Name=\"{Name}\"> has no editable <Implementation> body (GVLs/DUTs have none)");
        cdata.Value = text;
    }

    /// <summary>
    /// Serializes the document back to disk. Re-parses the bytes before committing as a cheap sanity check —
    /// a bad in-place text edit producing invalid XML must never reach the file the IDE has open.
    /// </summary>
    public void Save()
    {
        var bytes = RenderBytes();

        // Re-parse before committing — a bad in-place text edit producing invalid XML must never reach
        // the file the IDE has open. Anything that gets this far has already survived XmlWriter's own
        // character-validity checks, so this mainly guards against writer/encoding surprises.
        using var verifyStream = new MemoryStream(bytes);
        _ = XDocument.Load(verifyStream, LoadOptions.None);

        File.WriteAllBytes(FilePath, bytes);
    }

    /// <summary>Renders the document to a UTF-8 byte array using the same settings <see cref="Save"/> would write — used for dry-run diff previews.</summary>
    public byte[] RenderBytes()
    {
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, SaveSettings))
        {
            _document.Save(writer);
        }

        // XmlWriter always terminates the <?xml ... ?> declaration with a bare '\n', ignoring
        // NewLineChars — TwinCAT's own files are CRLF throughout, so normalize to match exactly
        // (and to keep round-trips of an unmodified file byte-identical).
        var text = SaveSettings.Encoding.GetString(buffer.ToArray());
        var normalized = NormalizeToCrLf(text);
        return SaveSettings.Encoding.GetBytes(normalized);
    }

    private static string NormalizeToCrLf(string text)
    {
        // Collapse any existing CRLF to LF first, then expand every LF to CRLF — avoids turning an
        // existing "\r\n" into "\r\r\n".
        return text.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private XCData? FindImplementationCData()
    {
        var implementation = _objectElement.Element("Implementation");
        // <Implementation> wraps a language element (<ST>, <SFC>, <FBD>, ...) which itself wraps the CDATA body.
        return implementation?.Elements().Select(language => GetCData(language)).FirstOrDefault(c => c is not null);
    }

    private static XCData? GetCData(string elementName, XElement parent) => GetCData(parent.Element(elementName));

    private static XCData? GetCData(XElement? element) => element?.Nodes().OfType<XCData>().FirstOrDefault();
}
