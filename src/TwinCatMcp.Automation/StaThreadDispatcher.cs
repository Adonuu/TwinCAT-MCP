using System.Collections.Concurrent;

namespace TwinCatMcp.Automation;

/// <summary>
/// Marshals work onto one dedicated, single-threaded-apartment (STA) background thread.
///
/// COM objects created by Visual Studio's automation interfaces (<c>DTE</c>, <c>ITcSysManager</c>, ...)
/// are thread-affine: every call into them must happen on the thread that created the underlying RCW, and
/// that thread must be STA, or calls fail with <c>RPC_E_WRONG_THREAD</c> / silently corrupt state. MCP tool
/// invocations arrive on arbitrary thread-pool threads, so every COM call in this project — creation,
/// property access, method invocation, release — funnels through <see cref="RunAsync{T}"/> rather than
/// touching a COM reference directly.
/// </summary>
public sealed class StaThreadDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _workItems = new();
    private readonly Thread _thread;
    private int _disposed;

    public StaThreadDispatcher()
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "twincat-mcp-sta",
        };

        // SetApartmentState throws on non-Windows runtimes that have no COM apartment concept —
        // the thread still runs (and still serializes access), it just isn't STA there.
        if (OperatingSystem.IsWindows())
            _thread.SetApartmentState(ApartmentState.STA);

        _thread.Start();
    }

    /// <summary>Runs <paramref name="work"/> on the dispatcher thread and returns its result, propagating any exception it throws.</summary>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        _workItems.Add(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        });

        return completion.Task;
    }

    /// <summary>Runs <paramref name="work"/> on the dispatcher thread with no result, propagating any exception it throws.</summary>
    public Task RunAsync(Action work) => RunAsync<object?>(() => { work(); return null; });

    private void RunLoop()
    {
        foreach (var item in _workItems.GetConsumingEnumerable())
            item();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _workItems.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _workItems.Dispose();
    }
}
