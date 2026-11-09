using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Extensions.Logging;
using TwinCatMcp.Automation.Models;

namespace TwinCatMcp.Automation;

/// <summary>Enumerates the shared Windows ROT; out-of-process DTE proxies work across bitness.</summary>
internal static class RunningXaeShells
{
    internal static bool TryParseMoniker(string name, out string progId, out int processId)
    {
        progId = "";
        processId = 0;
        var separator = name.LastIndexOf(':');
        if (separator < 1 || !int.TryParse(name[(separator + 1)..], out processId) || processId <= 0)
            return false;
        progId = name[1..separator];
        return name[0] == '!' &&
            (progId.Equals("TcXaeShell.DTE.15.0", StringComparison.OrdinalIgnoreCase) ||
             progId.Equals("TcXaeShell.DTE.17.0", StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<RunningXaeShell> Enumerate(ILogger logger)
    {
        var instances = new List<RunningXaeShell>();
        IRunningObjectTable? rot = null;
        IEnumMoniker? enumerator = null;
        IBindCtx? context = null;
        try
        {
            Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out rot));
            Marshal.ThrowExceptionForHR(CreateBindCtx(0, out context));
            rot.EnumRunning(out enumerator);
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                object? dte = null;
                try
                {
                    monikers[0].GetDisplayName(context, null, out var name);
                    if (!TryParseMoniker(name, out var progId, out var processId))
                        continue;
                    Marshal.ThrowExceptionForHR(rot.GetObject(monikers[0], out dte));
                    // A busy or exiting shell must not hide other accessible instances.
                    dynamic shell = dte!;
                    string path = shell.Solution.FullName;
                    instances.Add(new RunningXaeShell(
                        new XaeShellInstance(name, progId, processId,
                            progId.EndsWith(".15.0", StringComparison.OrdinalIgnoreCase) ? 32 : 64,
                            string.IsNullOrWhiteSpace(path) ? null : path), dte!));
                    dte = null; // ownership transferred to the candidate
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Skipping an inaccessible ROT entry during XAE Shell discovery.");
                }
                finally
                {
                    Release(dte);
                    Release(monikers[0]);
                }
            }
            return instances;
        }
        catch
        {
            foreach (var instance in instances)
                instance.Dispose();
            throw;
        }
        finally
        {
            Release(context);
            Release(enumerator);
            Release(rot);
        }
    }

    internal static void Release(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx context);
}

/// <summary>Owns one ROT reference until it is either released or transferred to the session.</summary>
internal sealed class RunningXaeShell(XaeShellInstance info, object dte) : IDisposable
{
    private object? _dte = dte;
    internal XaeShellInstance Info { get; } = info;
    internal object TakeDte() => Interlocked.Exchange(ref _dte, null)
        ?? throw new InvalidOperationException("DTE reference already transferred.");
    public void Dispose() => RunningXaeShells.Release(Interlocked.Exchange(ref _dte, null));
}
