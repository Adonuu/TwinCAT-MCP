using System.Linq;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;
using TwinCatMcp.Runtime.Models;

namespace TwinCatMcp.Runtime;

/// <summary>
/// Wraps the ADS symbol loader so <c>RuntimeTools</c> can browse the live symbol tree without dealing
/// directly with <see cref="ISymbolLoader"/> lifecycle. A loader is bound to one <see cref="AdsClient"/>
/// instance, so this type creates a fresh one whenever <see cref="AdsConnectionManager"/> hands back a
/// (re)connected client — comparing by reference is enough to detect that.
/// </summary>
public sealed class SymbolBrowser
{
    private readonly AdsConnectionManager _connections;
    private IConnection? _loaderBoundTo;
    private IDynamicSymbolLoader? _loader;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SymbolBrowser(AdsConnectionManager connections)
    {
        _connections = connections;
    }

    /// <summary>
    /// Returns every top-level symbol (and, recursively, their members up to <paramref name="maxDepth"/>)
    /// whose instance path contains <paramref name="prefix"/> (case-insensitive substring match — TwinCAT
    /// instance paths are dotted, e.g. "MAIN.fbRecipe.sName", and a substring match lets the agent search
    /// for "Recipe" without knowing the full path).
    /// </summary>
    public async Task<IReadOnlyList<SymbolInfo>> BrowseAsync(string? prefix, int maxDepth, int maxResults, CancellationToken ct)
    {
        var loader = await GetLoaderAsync(ct);
        var symbols = (await loader.GetDynamicSymbolsAsync(ct)).Symbols ?? Enumerable.Empty<ISymbol>();

        var results = new List<SymbolInfo>();
        foreach (var symbol in symbols)
        {
            Walk(symbol, prefix, maxDepth, results, maxResults);
            if (results.Count >= maxResults)
                break;
        }

        return results;
    }

    private static void Walk(ISymbol symbol, string? prefix, int depthRemaining, List<SymbolInfo> results, int maxResults)
    {
        if (results.Count >= maxResults)
            return;

        if (string.IsNullOrEmpty(prefix) || symbol.InstancePath.Contains(prefix, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new SymbolInfo(
                InstancePath: symbol.InstancePath,
                TypeName: symbol.TypeName,
                Comment: string.IsNullOrEmpty(symbol.Comment) ? null : symbol.Comment,
                IsReadOnly: symbol.IsReadOnly,
                ChildCount: symbol.SubSymbols.Count));
        }

        if (depthRemaining <= 0)
            return;

        foreach (var child in symbol.SubSymbols)
            Walk(child, prefix, depthRemaining - 1, results, maxResults);
    }

    /// <summary>Finds a single symbol by its dotted instance path, or null if no such symbol exists.</summary>
    public async Task<ISymbol?> FindSymbolAsync(string instancePath, CancellationToken ct)
    {
        var loader = await GetLoaderAsync(ct);
        var symbols = (await loader.GetDynamicSymbolsAsync(ct)).Symbols ?? Enumerable.Empty<ISymbol>();
        return FindByPath(symbols, instancePath);
    }

    /// <summary>Describes one symbol's data type, including its members for struct/FB types.</summary>
    public async Task<SymbolInfo?> DescribeAsync(string instancePath, CancellationToken ct)
    {
        var loader = await GetLoaderAsync(ct);
        var symbols = (await loader.GetDynamicSymbolsAsync(ct)).Symbols ?? Enumerable.Empty<ISymbol>();

        var found = FindByPath(symbols, instancePath);
        if (found is null)
            return null;

        return new SymbolInfo(
            InstancePath: found.InstancePath,
            TypeName: found.TypeName,
            Comment: string.IsNullOrEmpty(found.Comment) ? null : found.Comment,
            IsReadOnly: found.IsReadOnly,
            ChildCount: found.SubSymbols.Count);
    }

    private static ISymbol? FindByPath(IEnumerable<ISymbol> symbols, string instancePath)
    {
        foreach (var symbol in symbols)
        {
            if (string.Equals(symbol.InstancePath, instancePath, StringComparison.OrdinalIgnoreCase))
                return symbol;

            if (instancePath.StartsWith(symbol.InstancePath + ".", StringComparison.OrdinalIgnoreCase))
            {
                var nested = FindByPath(symbol.SubSymbols, instancePath);
                if (nested is not null)
                    return nested;
            }
        }

        return null;
    }

    private async Task<IDynamicSymbolLoader> GetLoaderAsync(CancellationToken ct)
    {
        var client = await _connections.EnsureConnectedAsync(ct: ct);

        await _gate.WaitAsync(ct);
        try
        {
            if (_loader is null || !ReferenceEquals(_loaderBoundTo, client))
            {
                _loader = (IDynamicSymbolLoader)SymbolLoaderFactory.Create(client, SymbolLoaderSettings.DefaultDynamic);
                _loaderBoundTo = client;
            }

            return _loader;
        }
        finally
        {
            _gate.Release();
        }
    }
}
