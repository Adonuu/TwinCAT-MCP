namespace TwinCatMcp.Automation;

internal static class SolutionConfigurationSelection
{
    internal static int Select(string requested, IReadOnlyList<(string Name, string Platform)> available)
    {
        var parts = requested.Split('|');
        if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Use 'Configuration' or 'Configuration|Platform'.", nameof(requested));
        var matches = available.Select((c, i) => (c, i)).Where(x =>
            string.Equals(x.c.Name, parts[0].Trim(), StringComparison.OrdinalIgnoreCase) &&
            (parts.Length == 1 || string.Equals(x.c.Platform, parts[1].Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length != 1)
            throw new ArgumentException($"Configuration '{requested}' is {(matches.Length == 0 ? "unavailable" : "ambiguous; specify its platform")}. Available: " +
                string.Join(", ", available.Select(c => c.Name + "|" + c.Platform)), nameof(requested));
        return matches[0].i + 1; // EnvDTE collections are one-based.
    }
}
