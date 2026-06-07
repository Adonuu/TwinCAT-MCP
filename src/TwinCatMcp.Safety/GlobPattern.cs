using System.Text.RegularExpressions;

namespace TwinCatMcp.Safety;

/// <summary>
/// Minimal case-insensitive glob matching for allow-list patterns (<c>*</c> = any run of characters,
/// <c>?</c> = any single character). Deliberately tiny — full glob/regex semantics would be overkill for
/// "MAIN.cmd_*"-style symbol and path patterns in a config file, and a small hand-rolled matcher is one
/// less dependency to audit.
/// </summary>
public static class GlobPattern
{
    public static bool IsMatch(string pattern, string value)
        => ToRegex(pattern).IsMatch(value);

    public static bool AnyMatch(IReadOnlyList<string> patterns, string value)
    {
        foreach (var pattern in patterns)
        {
            if (IsMatch(pattern, value))
                return true;
        }
        return false;
    }

    private static Regex ToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
