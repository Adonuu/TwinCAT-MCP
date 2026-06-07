using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TwinCatMcp.Safety;
using TwinCatMcp.Source.Models;

namespace TwinCatMcp.Source;

/// <summary>
/// MCP tools for browsing, searching, reading, and editing TwinCAT PLC project source — POUs, GVLs, and
/// DUTs stored as XML on disk. Read/browse/search tools are always available; anything that writes a file
/// is routed through <see cref="SafetyGate.CheckSourceEdit"/> first (see that type and <c>SAFETY.md</c> for
/// the policy this enforces).
/// </summary>
[McpServerToolType]
public static class SourceTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [McpServerTool, Description("Lists POUs/GVLs/DUTs in the configured PLC project, optionally filtered by a name substring and/or object kind (Pou, Gvl, Dut).")]
    public static string ListPlcObjects(
        PlcProjectIndex index,
        [Description("Case-insensitive substring to match against object names. Omit to list everything.")] string? namePattern = null,
        [Description("Restrict to one kind: Pou, Gvl, or Dut. Omit to include all kinds.")] string? kind = null)
    {
        var parsedKind = ParseKind(kind);
        IEnumerable<PlcObjectSummary> objects = index.All();

        if (parsedKind is { } k)
            objects = objects.Where(o => o.Kind == k);
        if (!string.IsNullOrWhiteSpace(namePattern))
            objects = objects.Where(o => o.Name.Contains(namePattern, StringComparison.OrdinalIgnoreCase));

        return Serialize(objects.OrderBy(o => o.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [McpServerTool, Description("Reads the full declaration (and, for POUs, implementation) source text of a PLC object, identified by name or GUID.")]
    public static string ReadPouSource(
        PlcProjectIndex index,
        [Description("The object's name (e.g. 'MAIN') or GUID (with or without braces).")] string nameOrGuid,
        [Description("Disambiguation hint when multiple objects share a name: Pou, Gvl, or Dut.")] string? kind = null)
    {
        var summary = ResolveSingle(index, nameOrGuid, ParseKind(kind));
        var doc = TcPlcObjectDocument.Load(index.ResolvePath(summary));

        var source = new PlcObjectSource(
            Name: doc.Name,
            Kind: doc.Kind,
            RelativePath: summary.RelativePath,
            Guid: doc.Guid,
            Declaration: doc.GetDeclarationText(),
            Implementation: doc.GetImplementationText(),
            ImplementationLanguage: doc.GetImplementationLanguage());

        return Serialize(source);
    }

    [McpServerTool, Description("Searches declaration and implementation source text across the project for a substring (or, optionally, a .NET regular expression) and returns matching lines with context.")]
    public static string SearchPlcSource(
        PlcProjectIndex index,
        [Description("Text to search for.")] string query,
        [Description("Treat 'query' as a .NET regular expression instead of a literal substring. Default false.")] bool regex = false,
        [Description("Restrict the search to one kind: Pou, Gvl, or Dut. Omit to search all kinds.")] string? kind = null,
        [Description("Maximum number of hits to return. Default 100.")] int maxResults = 100)
    {
        if (string.IsNullOrEmpty(query))
            throw new ArgumentException("query must not be empty.", nameof(query));

        var matcher = regex
            ? (Func<string, bool>)(line => System.Text.RegularExpressions.Regex.IsMatch(line, query))
            : line => line.Contains(query, StringComparison.OrdinalIgnoreCase);

        var parsedKind = ParseKind(kind);
        var hits = new List<SearchHit>();

        foreach (var summary in index.All().Where(o => parsedKind is null || o.Kind == parsedKind))
        {
            if (hits.Count >= maxResults)
                break;

            TcPlcObjectDocument doc;
            try { doc = TcPlcObjectDocument.Load(index.ResolvePath(summary)); }
            catch { continue; } // skip files that fail to parse rather than aborting the whole search

            CollectHits(hits, summary, "Declaration", doc.GetDeclarationText(), matcher, maxResults);
            if (doc.GetImplementationText() is { } implementation)
                CollectHits(hits, summary, "Implementation", implementation, matcher, maxResults);
        }

        return Serialize(hits);
    }

    [McpServerTool, Description("Replaces the declaration block (the VAR.../END_VAR or TYPE.../END_TYPE text) of a PLC object. Gated by the safety policy — pass dryRun=true to preview the change without writing it.")]
    public static string WritePouDeclaration(
        PlcProjectIndex index,
        SafetyGate safety,
        [Description("The object's name or GUID.")] string nameOrGuid,
        [Description("The complete new declaration text to write, replacing the existing block verbatim.")] string newDeclarationText,
        [Description("Disambiguation hint: Pou, Gvl, or Dut.")] string? kind = null,
        [Description("If true, compute and return the change without writing it. Default false.")] bool dryRun = false)
        => ApplyEdit(index, safety, nameOrGuid, ParseKind(kind), DeclarationEditor.Region.Declaration, newDeclarationText, dryRun);

    [McpServerTool, Description("Replaces the implementation body (the executable ST code) of a POU. GVLs and DUTs have no implementation and will be rejected. Gated by the safety policy — pass dryRun=true to preview the change without writing it.")]
    public static string WritePouImplementation(
        PlcProjectIndex index,
        SafetyGate safety,
        [Description("The POU's name or GUID.")] string nameOrGuid,
        [Description("The complete new implementation source text to write, replacing the existing body verbatim.")] string newImplementationText,
        [Description("If true, compute and return the change without writing it. Default false.")] bool dryRun = false)
        => ApplyEdit(index, safety, nameOrGuid, PlcObjectKind.Pou, DeclarationEditor.Region.Implementation, newImplementationText, dryRun);

    [McpServerTool, Description("Creates a new POU/GVL/DUT skeleton file with a freshly-minted GUID under the given project-relative folder. " +
        "IMPORTANT: this only writes the file to disk — it does NOT register the object in the .plcproj. If a project is open in the XAE Shell " +
        "(see OpenXaeProject), prefer the CreatePlcObject automation tool instead: it creates AND registers the object through the IDE's own " +
        "Automation Interface in one step, with correctly-synced GUIDs, so no manual 'Add Existing Item' step is needed. Use this file-based " +
        "tool only for headless scenarios where no XAE Shell session is open. Gated by the safety policy — pass dryRun=true to preview without writing.")]
    public static string CreatePou(
        PlcProjectIndex index,
        SafetyGate safety,
        [Description("The new object's name (must be a valid IEC 61131-3 identifier).")] string name,
        [Description("Project-relative folder to create the file in, e.g. 'POUs' or 'POUs/Generated'. Forward or back slashes both work.")] string folder,
        [Description("Object kind: Pou, Gvl, or Dut.")] string kind,
        [Description("If true, compute and return what would be created without writing it. Default false.")] bool dryRun = false)
    {
        var parsedKind = Enum.Parse<PlcObjectKind>(kind, ignoreCase: true);
        var relativeFolder = folder.Replace('\\', '/').Trim('/');
        var relativePath = string.IsNullOrEmpty(relativeFolder)
            ? $"{name}{parsedKind.FileExtension()}"
            : $"{relativeFolder}/{name}{parsedKind.FileExtension()}";

        var decision = safety.CheckSourceEdit(relativePath, confirm: true);
        if (!decision.IsAllowed)
            return Serialize(new CreateResult(Created: false, relativePath, Guid: ""));

        var fullPath = Path.Combine(index.ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath))
            throw new InvalidOperationException($"A file already exists at '{relativePath}'.");

        var guid = Guid.NewGuid();
        if (dryRun)
            return Serialize(new CreateResult(Created: false, relativePath, Guid: $"{{{guid}}}"));

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, PlcObjectTemplates.CreateSkeletonXml(parsedKind, name, guid), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        index.Invalidate();

        return Serialize(new CreateResult(Created: true, relativePath, Guid: $"{{{guid}}}"));
    }

    [McpServerTool, Description("Returns the project's POU/GVL/DUT objects grouped by folder, mirroring the on-disk layout of the PLC project tree.")]
    public static string GetProjectStructure(PlcProjectIndex index)
    {
        var tree = index.All()
            .GroupBy(o => Path.GetDirectoryName(o.RelativePath)?.Replace('\\', '/') ?? string.Empty)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Folder = g.Key,
                Objects = g.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                           .Select(o => new { o.Name, Kind = o.Kind.ToString(), o.RelativePath, o.Guid })
                           .ToArray(),
            })
            .ToArray();

        return Serialize(tree);
    }

    private static string ApplyEdit(
        PlcProjectIndex index,
        SafetyGate safety,
        string nameOrGuid,
        PlcObjectKind? kindHint,
        DeclarationEditor.Region region,
        string newText,
        bool dryRun)
    {
        var summary = ResolveSingle(index, nameOrGuid, kindHint);
        var decision = safety.CheckSourceEdit(summary.RelativePath, confirm: true);

        if (!decision.IsAllowed)
        {
            return Serialize(new EditResult(Changed: false, Applied: false, summary.RelativePath, DiffPreview: null,
                Warnings: [decision.Reason]));
        }

        var path = index.ResolvePath(summary);
        var doc = TcPlcObjectDocument.Load(path);

        var result = DeclarationEditor.Plan(doc, region, newText, dryRun, apply: d =>
        {
            if (region == DeclarationEditor.Region.Declaration) d.SetDeclarationText(newText);
            else d.SetImplementationText(newText);
            d.Save();
        });

        if (result.Applied)
            index.Invalidate();

        return Serialize(result with { Warnings = [.. result.Warnings, decision.Reason] });
    }

    private static void CollectHits(List<SearchHit> hits, PlcObjectSummary summary, string region, string text, Func<string, bool> matcher, int maxResults)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length && hits.Count < maxResults; i++)
        {
            if (matcher(lines[i]))
                hits.Add(new SearchHit(summary.Name, summary.Kind, summary.RelativePath, region, i + 1, lines[i].Trim()));
        }
    }

    private static PlcObjectSummary ResolveSingle(PlcProjectIndex index, string nameOrGuid, PlcObjectKind? kind)
    {
        var matches = index.Find(nameOrGuid, kind);
        return matches.Count switch
        {
            0 => throw new InvalidOperationException($"No PLC object found matching '{nameOrGuid}'" + (kind is { } k ? $" of kind {k}" : "") + "."),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"'{nameOrGuid}' matches {matches.Count} objects ({string.Join(", ", matches.Select(m => $"{m.Name} [{m.Kind}] at {m.RelativePath}"))}). " +
                "Narrow the search with an exact name, a GUID, or a 'kind' hint."),
        };
    }

    private static PlcObjectKind? ParseKind(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? null : Enum.Parse<PlcObjectKind>(kind, ignoreCase: true);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
