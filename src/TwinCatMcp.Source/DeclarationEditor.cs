using TwinCatMcp.Source.Models;

namespace TwinCatMcp.Source;

/// <summary>
/// Applies whole-region replacements to a PLC object's declaration or implementation text and produces a
/// dry-run-friendly preview. Deliberately stays at the text level — it never parses IEC 61131-3 source into
/// a structured model (that's the much deeper job the companion `twincat-validator-mcp` project already
/// does); doing the minimal text substitution is what keeps round-trips safe and predictable for an LLM.
/// </summary>
public static class DeclarationEditor
{
    public enum Region { Declaration, Implementation }

    /// <summary>
    /// Computes the result of replacing a whole region's text, without writing anything. <paramref name="apply"/>
    /// performs the mutation on <paramref name="document"/> (and the caller decides whether to <c>Save()</c>) —
    /// kept as a callback so dry-run and real runs share the exact same diff/no-op detection logic.
    /// </summary>
    public static EditResult Plan(
        TcPlcObjectDocument document,
        Region region,
        string newText,
        bool dryRun,
        Action<TcPlcObjectDocument> apply)
    {
        var current = region switch
        {
            Region.Declaration => document.GetDeclarationText(),
            Region.Implementation => document.GetImplementationText()
                ?? throw new InvalidOperationException(
                    $"'{document.Name}' ({document.Kind}) has no <Implementation> body to edit — GVLs and DUTs are declaration-only."),
            _ => throw new ArgumentOutOfRangeException(nameof(region)),
        };

        var warnings = new List<string>();
        if (string.Equals(current, newText, StringComparison.Ordinal))
        {
            return new EditResult(Changed: false, Applied: false, document.Name, DiffPreview: null, warnings);
        }

        if (string.IsNullOrWhiteSpace(newText))
            warnings.Add("New text is empty or whitespace-only — double-check this is intentional.");

        var diff = BuildLineDiffPreview(current, newText);

        if (dryRun)
        {
            return new EditResult(Changed: true, Applied: false, document.Name, diff, warnings);
        }

        apply(document);
        return new EditResult(Changed: true, Applied: true, document.Name, diff, warnings);
    }

    /// <summary>
    /// A compact unified-diff-style preview (added/removed lines only, with a couple lines of context) —
    /// enough for an LLM or human reviewer to sanity-check an edit without reproducing the whole file.
    /// </summary>
    private static string BuildLineDiffPreview(string before, string after, int contextLines = 2)
    {
        var beforeLines = SplitLines(before);
        var afterLines = SplitLines(after);

        var commonPrefix = 0;
        while (commonPrefix < beforeLines.Count && commonPrefix < afterLines.Count
               && beforeLines[commonPrefix] == afterLines[commonPrefix])
            commonPrefix++;

        var commonSuffix = 0;
        while (commonSuffix < beforeLines.Count - commonPrefix && commonSuffix < afterLines.Count - commonPrefix
               && beforeLines[^(commonSuffix + 1)] == afterLines[^(commonSuffix + 1)])
            commonSuffix++;

        var removedStart = commonPrefix;
        var removedEnd = beforeLines.Count - commonSuffix;
        var addedStart = commonPrefix;
        var addedEnd = afterLines.Count - commonSuffix;

        var contextStart = Math.Max(0, commonPrefix - contextLines);
        var lines = new List<string>();

        for (var i = contextStart; i < commonPrefix; i++)
            lines.Add($"  {beforeLines[i]}");
        for (var i = removedStart; i < removedEnd; i++)
            lines.Add($"- {beforeLines[i]}");
        for (var i = addedStart; i < addedEnd; i++)
            lines.Add($"+ {afterLines[i]}");

        var contextEnd = Math.Min(beforeLines.Count, beforeLines.Count - commonSuffix + contextLines);
        for (var i = beforeLines.Count - commonSuffix; i < contextEnd; i++)
            lines.Add($"  {beforeLines[i]}");

        return string.Join('\n', lines);
    }

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').ToList();
}
