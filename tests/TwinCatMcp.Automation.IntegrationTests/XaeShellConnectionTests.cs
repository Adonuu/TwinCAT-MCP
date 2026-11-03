using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwinCatMcp.Automation.Models;
using Xunit;

namespace TwinCatMcp.Automation.IntegrationTests;

public sealed class XaeShellConnectionTests
{
    [Theory]
    [InlineData("!TcXaeShell.DTE.15.0:123", true, 123)]
    [InlineData("!TcXaeShell.DTE.17.0:456", true, 456)]
    [InlineData("!VisualStudio.DTE.17.0:456", false, 456)]
    [InlineData("!TcXaeShell.DTE.17.0Extra:456", false, 456)]
    [InlineData("TcXaeShell.DTE.17.0:456", false, 456)]
    [InlineData("!TcXaeShell.DTE.17.0:0", false, 0)]
    [InlineData("!TcXaeShell.DTE.17.0:abc", false, 0)]
    [InlineData("", false, 0)]
    public void RecognizesOnlySupportedShellMonikers(string moniker, bool expected, int pid)
    {
        Assert.Equal(expected, RunningXaeShells.TryParseMoniker(moniker, out _, out var actualPid));
        if (expected) Assert.Equal(pid, actualPid);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public async Task AttachUsesExistingSolutionAndDoesNotQuitOrCloseOnDisconnect(int bitness)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dispatcher = new StaThreadDispatcher();
        var dte = new FakeDte(@"C:\Projects\Existing.sln");
        var info = Info(bitness, 123, dte.Solution.FullName);
        await using var session = CreateSession(dispatcher, () => [new(info, dte)]);
        string? indexedDirectory = null;
        session.ProjectOpened += directory => indexedDirectory = directory;

        var listed = await session.ListXaeShellInstancesAsync(default);
        Assert.Equal(info, Assert.Single(listed));
        Assert.False((await session.GetXaeShellConnectionAsync(default)).Connected);

        var attached = await session.AttachXaeShellAsync(info.InstanceId, null, default);
        Assert.True(attached.Connected);
        Assert.False(attached.OwnsInstance);
        Assert.Equal(info, attached.Instance);
        Assert.Equal(@"C:\Projects", indexedDirectory);
        Assert.True(session.IsProjectOpen);
        Assert.Equal(dte.Solution.FullName, (await session.GetProjectStatusAsync(default)).Path);
        // Idempotent open must not call Solution.Open on the interactive project.
        Assert.True((await session.OpenProjectAsync(dte.Solution.FullName, default)).Open);
        Assert.Equal(attached, await session.AttachXaeShellAsync(info.InstanceId, null, default));

        Assert.False((await session.DisconnectXaeShellAsync(default)).Connected);
        Assert.False(session.IsProjectOpen);
        Assert.False(dte.QuitCalled);
        Assert.Equal(dte.Solution.FullName, Assert.Single(await session.ListXaeShellInstancesAsync(default)).SolutionPath);
    }

    [Fact]
    public async Task SelectionRejectsAmbiguityAndSwitchingWithoutDisconnect()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dispatcher = new StaThreadDispatcher();
        var first = new FakeDte(@"C:\Projects\First.sln");
        var second = new FakeDte(@"C:\Projects\Second.sln");
        var firstInfo = Info(32, 123, first.Solution.FullName);
        var secondInfo = Info(64, 456, second.Solution.FullName);
        await using var session = CreateSession(dispatcher, () => [new(firstInfo, first), new(secondInfo, second)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AttachXaeShellAsync(null, null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AttachXaeShellAsync("missing", null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => session.AttachXaeShellAsync(firstInfo.InstanceId, firstInfo.SolutionPath, default));
        Assert.False((await session.GetXaeShellConnectionAsync(default)).Connected);

        var selected = await session.AttachXaeShellAsync(null, @"c:\projects\FIRST.sln", default);
        Assert.Equal(firstInfo, selected.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AttachXaeShellAsync(secondInfo.InstanceId, null, default));
        Assert.Equal(firstInfo, (await session.GetXaeShellConnectionAsync(default)).Instance);
        await session.DisconnectXaeShellAsync(default);
        Assert.Equal(secondInfo, (await session.AttachXaeShellAsync(secondInfo.InstanceId, null, default)).Instance);
    }

    [Fact]
    public async Task SolutionChangesRefreshProjectStatusAndSourceRootAndDisposeLeavesIdeOpen()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dispatcher = new StaThreadDispatcher();
        var dte = new FakeDte("");
        var info = Info(64, 456, null);
        var session = CreateSession(dispatcher, () => [new(info, dte)]);
        try
        {
            var roots = new List<string>();
            session.ProjectOpened += roots.Add;
            await session.AttachXaeShellAsync(null, null, default);
            Assert.False(session.IsProjectOpen);
            dte.Solution.FullName = @"C:\Other\Opened.sln";
            var connection = await session.GetXaeShellConnectionAsync(default);
            Assert.Equal(dte.Solution.FullName, connection.SolutionPath);
            Assert.Equal(dte.Solution.FullName, connection.Instance!.SolutionPath);
            Assert.Equal(@"C:\Other", Assert.Single(roots));
            Assert.True((await session.GetProjectStatusAsync(default)).Open);
            dte.Solution.FullName = "";
            Assert.False((await session.GetProjectStatusAsync(default)).Open);
        }
        finally { await session.DisposeAsync(); }
        Assert.False(dte.QuitCalled);
        Assert.False((await session.GetXaeShellConnectionAsync(default)).Connected);
    }

    [Fact]
    public async Task MissingInstancesAndCancellationDoNotConnect()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dispatcher = new StaThreadDispatcher();
        await using var session = CreateSession(dispatcher, () => []);
        Assert.Empty(await session.ListXaeShellInstancesAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AttachXaeShellAsync(null, null, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AttachXaeShellAsync(null, null, new CancellationToken(true)));
        Assert.False((await session.GetXaeShellConnectionAsync(default)).Connected);
    }

    private static XaeShellInstance Info(int bitness, int pid, string? path)
    {
        var progId = $"TcXaeShell.DTE.{(bitness == 32 ? 15 : 17)}.0";
        return new($"!{progId}:{pid}", progId, pid, bitness, path);
    }

    private static XaeShellSession CreateSession(StaThreadDispatcher dispatcher, Func<IReadOnlyList<RunningXaeShell>> discover) =>
        new(dispatcher, Options.Create(new AutomationOptions()), NullLogger<XaeShellSession>.Instance, discover);

    // Deliberately expose only reads and Quit: attachment fails if it tries to open/close the
    // solution or change visibility, UserControl or SuppressUI on an existing interactive IDE.
    public sealed class FakeDte(string path)
    {
        public FakeSolution Solution { get; } = new(path);
        public bool QuitCalled { get; private set; }
        public void Quit() => QuitCalled = true;
    }

    public sealed class FakeSolution(string path)
    {
        public string FullName { get; set; } = path;
    }
}
