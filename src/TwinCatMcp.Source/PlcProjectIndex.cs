using System.Collections.Concurrent;
using TwinCatMcp.Source.Models;

namespace TwinCatMcp.Source;

/// <summary>
/// Discovers and caches the POU/GVL/DUT files under a configured TwinCAT project root, so tools can
/// resolve a name or GUID to a file path without re-walking the tree on every call. Refreshes on demand
/// and via a <see cref="FileSystemWatcher"/> so externally-saved IDE changes are picked up.
/// </summary>
public sealed class PlcProjectIndex : IDisposable
{
    private static readonly string[] WatchedExtensions =
        Enum.GetValues<PlcObjectKind>().Select(k => k.FileExtension()).ToArray();

    private volatile string _projectRoot;
    private readonly object _refreshLock = new();
    private FileSystemWatcher? _watcher;
    private ConcurrentDictionary<string, PlcObjectSummary> _byRelativePath = new();
    private volatile bool _dirty = true;

    public PlcProjectIndex(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("Project root must not be empty.", nameof(projectRoot));

        _projectRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(_projectRoot))
            throw new DirectoryNotFoundException($"PLC project root not found: '{_projectRoot}'");

        TryStartWatcher();
    }

    public string ProjectRoot => _projectRoot;

    /// <summary>All indexed objects, refreshing the cache first if files have changed since the last build.</summary>
    public IReadOnlyCollection<PlcObjectSummary> All()
    {
        EnsureFresh();
        return _byRelativePath.Values.ToArray();
    }

    /// <summary>
    /// Finds objects by exact name, GUID (with or without braces), or a case-insensitive substring of the name.
    /// Exact name/GUID matches are returned alone; otherwise all substring matches are returned.
    /// </summary>
    public IReadOnlyList<PlcObjectSummary> Find(string nameOrPattern, PlcObjectKind? kind = null)
    {
        EnsureFresh();

        var candidates = _byRelativePath.Values.AsEnumerable();
        if (kind is { } k)
            candidates = candidates.Where(o => o.Kind == k);

        var normalizedGuid = NormalizeGuid(nameOrPattern);

        var exact = candidates
            .Where(o => string.Equals(o.Name, nameOrPattern, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(NormalizeGuid(o.Guid), normalizedGuid, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exact.Length > 0)
            return exact;

        return candidates
            .Where(o => o.Name.Contains(nameOrPattern, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public string ResolvePath(PlcObjectSummary summary) => Path.Combine(_projectRoot, summary.RelativePath);

    public void Invalidate() => _dirty = true;

    /// <summary>
    /// Re-points the index at a different project root — used when a solution is opened in the XAE
    /// Shell, so the file-based tools follow the project actually being worked on instead of staying
    /// on the process's startup working directory. No-op if the root is unchanged.
    /// </summary>
    public void Reroot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("Project root must not be empty.", nameof(projectRoot));

        var newRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(newRoot))
            throw new DirectoryNotFoundException($"PLC project root not found: '{newRoot}'");

        lock (_refreshLock)
        {
            if (string.Equals(newRoot, _projectRoot, StringComparison.OrdinalIgnoreCase))
                return;

            _watcher?.Dispose();
            _watcher = null;
            _projectRoot = newRoot;
            TryStartWatcher();
            _dirty = true;
        }
    }

    private void EnsureFresh()
    {
        if (!_dirty)
            return;

        lock (_refreshLock)
        {
            if (!_dirty)
                return;

            var fresh = new ConcurrentDictionary<string, PlcObjectSummary>(StringComparer.OrdinalIgnoreCase);
            foreach (var kind in Enum.GetValues<PlcObjectKind>())
            {
                foreach (var path in Directory.EnumerateFiles(_projectRoot, $"*{kind.FileExtension()}", SearchOption.AllDirectories))
                {
                    if (TryIndex(path, kind, out var summary))
                        fresh[summary.RelativePath] = summary;
                }
            }

            _byRelativePath = fresh;
            _dirty = false;
        }
    }

    private bool TryIndex(string fullPath, PlcObjectKind kind, out PlcObjectSummary summary)
    {
        try
        {
            var doc = TcPlcObjectDocument.Load(fullPath);
            var relativePath = Path.GetRelativePath(_projectRoot, fullPath);
            summary = new PlcObjectSummary(
                Name: doc.Name,
                Kind: kind,
                RelativePath: relativePath,
                Guid: doc.Guid,
                LastModifiedUtc: File.GetLastWriteTimeUtc(fullPath));
            return true;
        }
        catch
        {
            // A file that doesn't parse as a well-formed TcPlcObject is skipped rather than failing the
            // whole index — e.g. mid-save by the IDE, or a non-standard object kind we don't model.
            summary = default!;
            return false;
        }
    }

    private void TryStartWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher(_projectRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            _watcher.Changed += (_, e) => InvalidateIfWatched(e.FullPath);
            _watcher.Created += (_, e) => InvalidateIfWatched(e.FullPath);
            _watcher.Deleted += (_, e) => InvalidateIfWatched(e.FullPath);
            _watcher.Renamed += (_, e) => { InvalidateIfWatched(e.FullPath); InvalidateIfWatched(e.OldFullPath); };
            _watcher.EnableRaisingEvents = true;
        }
        catch (IOException)
        {
            // Best-effort: if the watcher can't be created (e.g. unusual filesystem), callers can still
            // force a refresh via Invalidate(); we just lose the automatic external-change detection.
            _watcher = null;
        }
    }

    private void InvalidateIfWatched(string path)
    {
        if (WatchedExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            _dirty = true;
    }

    private static string NormalizeGuid(string value) => value.Trim('{', '}');

    public void Dispose() => _watcher?.Dispose();
}
