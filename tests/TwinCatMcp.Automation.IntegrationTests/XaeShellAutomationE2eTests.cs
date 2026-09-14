using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwinCatMcp.Automation;
using TwinCatMcp.Automation.Models;
using Xunit;
using Xunit.Abstractions;

namespace TwinCatMcp.Automation.IntegrationTests;

/// <summary>
/// Marks a test that drives a real TwinCAT XAE Shell — slow, Windows-only, and stateful (it opens
/// the IDE and mutates the target project, cleaning up after itself). Skipped unless explicitly
/// opted in, so a normal <c>dotnet test</c> never launches the shell.
/// </summary>
public sealed class XaeShellE2eFactAttribute : FactAttribute
{
    public XaeShellE2eFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TWINCAT_E2E") != "1")
            Skip = "Set TWINCAT_E2E=1 and TWINCAT_E2E_PROJECT=<path to a TwinCAT .sln> to run XAE Shell integration tests.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TWINCAT_E2E_PROJECT")))
            Skip = "TWINCAT_E2E_PROJECT is not set — point it at a TwinCAT .sln to test against.";
    }
}

/// <summary>
/// End-to-end coverage of the whole XAE Shell automation surface against a real project, in one
/// sequential test (the steps share one IDE instance and build on each other). All created objects
/// are contained in a single 'McpVerify' folder that is deleted at the end, so a passing run leaves
/// zero net change in the target project.
///
/// ActivateConfiguration/StartRestartTwinCAT act on the machine's local TwinCAT runtime for real —
/// they additionally require TWINCAT_E2E_RUNTIME_OPS=1 so a routine opt-in run doesn't restart a
/// runtime somebody is using.
/// </summary>
public sealed class XaeShellAutomationE2eTests : IAsyncLifetime
{
    private readonly StaThreadDispatcher _dispatcher = new();
    private readonly ITestOutputHelper _output;
    private XaeShellSession? _session;

    public XaeShellAutomationE2eTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync()
    {
        _session = new XaeShellSession(
            _dispatcher,
            Options.Create(new AutomationOptions { ShowIde = false }),
            NullLogger<XaeShellSession>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_session is not null)
            await _session.DisposeAsync();
        _dispatcher.Dispose();
    }

    [XaeShellE2eFact]
    public async Task FullAutomationSurface_EndToEnd()
    {
        var session = _session!;
        var solution = Environment.GetEnvironmentVariable("TWINCAT_E2E_PROJECT")!;
        var runtimeOps = Environment.GetEnvironmentVariable("TWINCAT_E2E_RUNTIME_OPS") == "1";
        // PLC objects export as PLCopen XML (PlcOpenExport under the hood).
        var exportFile = Path.Combine(Path.GetTempPath(), $"FB_McpTest.{Guid.NewGuid():N}.plcopen.xml");

        string? plcRoot = null;
        var createdFolder = false;
        try
        {
            var open = await session.OpenProjectAsync(solution, CancellationToken.None);
            Assert.True(open.Open, "OpenProject should report the project as open.");

            var tree = await session.GetProjectTreeAsync(null, 2, CancellationToken.None);
            Assert.NotNull(tree);
            var plcProjectNode = tree!.Children.FirstOrDefault();
            Assert.NotNull(plcProjectNode);

            // The shell does not enumerate the nested IEC project node ('<name> Project') — where POUs
            // live and where CreateChild parents belong — among the PLC node's children; WalkTree grafts
            // it in by naming convention, and everything below depends on that grafted path being real.
            var nestedProject = plcProjectNode!.Children.FirstOrDefault(c => c.TreePath.EndsWith(" Project", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(nestedProject);
            plcRoot = nestedProject!.TreePath;

            // ListIoDevices must not throw; an empty list is a valid result.
            _ = await session.ListIoDevicesAsync(CancellationToken.None);

            // Self-heal: aborted runs can leave this test's own object names behind at project level
            // (e.g. FB_McpTest), which would make the CreateChild calls below collide. Remove our named
            // artifacts first, then persist the removal so the .plcproj stops referencing them.
            foreach (var stray in new[] { $"{plcRoot}^FB_McpTest", $"{plcRoot}^McpVerify" })
            {
                var existing = await session.ValidateTreePathAsync(stray, CancellationToken.None);
                if (existing.Exists)
                {
                    _output.WriteLine($"Preflight: removing leftover '{stray}'.");
                    var removed = await session.DeletePlcObjectAsync(stray, "e2e", CancellationToken.None);
                    Assert.True(removed.Succeeded, $"Preflight removal of '{stray}' failed: {removed.Error}");
                }
            }

            await session.SaveAllAsync("e2e", CancellationToken.None);

            var folder = await session.CreatePlcObjectAsync(plcRoot!, "McpVerify", "Folder", null, null, "e2e", CancellationToken.None);
            Assert.True(folder.Succeeded, $"Create folder failed: {folder.Error}");
            createdFolder = true;
            var verifyPath = folder.TreePath ?? $"{plcRoot}^McpVerify";

            foreach (var (name, kind, pouType, returnType) in new[]
            {
                ("FB_McpTest", "Pou", (string?)"FunctionBlock", (string?)null),
                ("PRG_McpTest", "Pou", "Program", null),
                ("F_McpTest", "Pou", "Function", "LREAL"),
                ("GVL_McpTest", "Gvl", null, null),
                ("ST_McpTest", "Dut", null, null),
            })
            {
                var created = await session.CreatePlcObjectAsync(verifyPath, name, kind, pouType, returnType, "e2e", CancellationToken.None);
                Assert.True(created.Succeeded, $"Create {name} ({kind}/{pouType}) failed: {created.Error}");
                Assert.False(string.IsNullOrEmpty(created.TreePath), $"{name} should report its tree path.");
            }

            var verifyTree = await session.GetProjectTreeAsync(verifyPath, 1, CancellationToken.None);
            Assert.Equal(5, verifyTree?.Children.Count);

            // Code write/read through the Automation Interface (ITcPlcDeclaration/ITcPlcImplementation).
            // Written BEFORE the export so the PLCopen export/delete/import round-trip below carries
            // real code, and validated by the Build further down (the IDE compiles what we wrote).
            const string fbDeclaration = "FUNCTION_BLOCK FB_McpTest\r\nVAR_INPUT\r\n\tnIn : INT;\r\nEND_VAR\r\nVAR_OUTPUT\r\n\tnOut : INT;\r\nEND_VAR\r\nVAR\r\nEND_VAR\r\n";
            const string fbImplementation = "nOut := nIn + 1;\r\n";
            var fbPath = $"{verifyPath}^FB_McpTest";

            var dryRunWrite = await session.WritePlcObjectCodeAsync(fbPath, implementation: false, fbDeclaration, dryRun: true, "e2e", CancellationToken.None);
            Assert.True(dryRunWrite.Succeeded, $"Dry-run declaration write failed: {dryRunWrite.Error}");
            Assert.False(dryRunWrite.Applied, "A dry-run write must not report Applied.");
            Assert.NotNull(dryRunWrite.PreviousText);

            var writeDecl = await session.WritePlcObjectCodeAsync(fbPath, implementation: false, fbDeclaration, dryRun: false, "e2e", CancellationToken.None);
            Assert.True(writeDecl.Succeeded, $"Write declaration failed: {writeDecl.Error}");
            var writeImpl = await session.WritePlcObjectCodeAsync(fbPath, implementation: true, fbImplementation, dryRun: false, "e2e", CancellationToken.None);
            Assert.True(writeImpl.Succeeded, $"Write implementation failed: {writeImpl.Error}");

            // Assert on content, not byte equality — the IDE may normalize line endings/whitespace.
            var readBack = await session.ReadPlcObjectCodeAsync(fbPath, CancellationToken.None);
            Assert.True(readBack.Succeeded, $"Read code failed: {readBack.Error}");
            Assert.Contains("nIn : INT", readBack.Declaration);
            Assert.Contains("nOut := nIn + 1;", readBack.Implementation);

            // GVLs are declaration-only: the implementation QI must fail gracefully, not error out.
            var gvlCode = await session.ReadPlcObjectCodeAsync($"{verifyPath}^GVL_McpTest", CancellationToken.None);
            Assert.True(gvlCode.Succeeded, $"Read GVL code failed: {gvlCode.Error}");
            Assert.NotNull(gvlCode.Declaration);
            Assert.Null(gvlCode.Implementation);

            var export = await session.ExportPlcObjectAsync($"{verifyPath}^FB_McpTest", exportFile, "e2e", CancellationToken.None);
            Assert.True(export.Succeeded, $"Export failed: {export.Error}");

            var delete = await session.DeletePlcObjectAsync($"{verifyPath}^FB_McpTest", "e2e", CancellationToken.None);
            Assert.True(delete.Succeeded, $"Delete failed: {delete.Error}");

            var import = await session.ImportPlcObjectAsync(verifyPath, exportFile, "e2e", CancellationToken.None);
            Assert.True(import.Succeeded, $"Import failed: {import.Error}");

            var build = await session.BuildAsync(null, CancellationToken.None);
            Assert.True(build.Succeeded, $"Build reported {build.ErrorCount} error(s): " +
                string.Join(" | ", build.Errors.Where(e => e.Severity == "Error").Take(5).Select(e => e.Description)));

            if (runtimeOps)
            {
                var activate = await session.ActivateConfigurationAsync("e2e", CancellationToken.None);
                Assert.True(activate.Succeeded, $"ActivateConfiguration failed: {activate.Error}");
            }

            _ = await session.CleanAsync(null, CancellationToken.None);

            // Clean up while the shell is guaranteed responsive, and save so the removal persists
            // to the .plcproj — tree changes are otherwise discarded by the unsaved close below.
            var cleanup = await session.DeletePlcObjectAsync($"{plcRoot}^McpVerify", "e2e", CancellationToken.None);
            Assert.True(cleanup.Succeeded, $"Cleanup of McpVerify failed — the target project may need manual cleanup: {cleanup.Error}");
            createdFolder = false;

            var save = await session.SaveAllAsync("e2e", CancellationToken.None);
            Assert.True(save.Succeeded, $"SaveAll after cleanup failed: {save.Error}");

            if (runtimeOps)
            {
                // Restart LAST: afterwards the shell can reject COM calls for minutes, so nothing
                // project-state-critical may follow it. Poll until it answers again before closing.
                var restart = await session.RestartTwinCatAsync("e2e", CancellationToken.None);
                Assert.True(restart.Succeeded, $"RestartTwinCat failed: {restart.Error}");

                var deadline = DateTime.UtcNow.AddMinutes(5);
                var responsive = false;
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        if (await session.GetProjectTreeAsync(null, 0, CancellationToken.None) is not null) { responsive = true; break; }
                    }
                    catch { /* still busy */ }
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
                Assert.True(responsive, "Shell did not become responsive within 5 minutes after RestartTwinCat.");
            }
        }
        finally
        {
            // Only reached with work left behind if the test failed before its own cleanup ran.
            if (createdFolder && plcRoot is not null)
            {
                try
                {
                    await session.DeletePlcObjectAsync($"{plcRoot}^McpVerify", "e2e", CancellationToken.None);
                    await session.SaveAllAsync("e2e", CancellationToken.None);
                }
                catch { /* best effort — close regardless */ }
            }

            await session.CloseProjectAsync(CancellationToken.None);

            if (File.Exists(exportFile))
                File.Delete(exportFile);
        }
    }

    /// <summary>
    /// Regression coverage for ActivateConfiguration's verification (the fix for "reports success even
    /// when a build fails"): inject a deliberate compile error into a POU that a program actually
    /// instantiates (so the PLC compiler compiles it), then assert that activating reports NOT-success
    /// instead of blindly trusting <c>ITcSysManager::ActivateConfiguration()</c>'s S_OK. Because the
    /// build is broken the activation never downloads, so this step is safe to run without
    /// TWINCAT_E2E_RUNTIME_OPS.
    /// </summary>
    [XaeShellE2eFact]
    public async Task ActivateConfiguration_ReportsFailure_WhenImplicitBuildFails()
    {
        var session = _session!;
        var solution = Environment.GetEnvironmentVariable("TWINCAT_E2E_PROJECT")!;

        string? plcRoot = null;
        string? testProgramPath = null;
        string? originalTpDeclaration = null;
        var createdFolder = false;
        try
        {
            var open = await session.OpenProjectAsync(solution, CancellationToken.None);
            Assert.True(open.Open, "OpenProject should report the project as open.");

            var tree = await session.GetProjectTreeAsync(null, 2, CancellationToken.None);
            Assert.NotNull(tree);
            var plcProjectNode = tree!.Children.First();
            var nestedProject = plcProjectNode.Children.First(c => c.TreePath.EndsWith(" Project", StringComparison.OrdinalIgnoreCase));
            plcRoot = nestedProject.TreePath;

            var folder = await session.CreatePlcObjectAsync(plcRoot!, "McpVerify", "Folder", null, null, "e2e", CancellationToken.None);
            Assert.True(folder.Succeeded, $"Create folder failed: {folder.Error}");
            createdFolder = true;
            var verifyPath = folder.TreePath ?? $"{plcRoot}^McpVerify";

            var created = await session.CreatePlcObjectAsync(verifyPath, "FB_McpBroken", "Pou", "FunctionBlock", null, "e2e", CancellationToken.None);
            Assert.True(created.Succeeded, $"Create FB_McpBroken failed: {created.Error}");

            // Deliberate, unambiguous compile error: assigning an INT straight into a STRING is a type
            // mismatch the TwinCAT PLC compiler cannot miss (unlike an undeclared call, which this
            // compiler version evidently accepts).
            const string brokenDeclaration = "FUNCTION_BLOCK FB_McpBroken\r\nVAR_INPUT\r\n\tnIn : INT;\r\nEND_VAR\r\nVAR_OUTPUT\r\n\tsOut : STRING;\r\nEND_VAR\r\nVAR\r\nEND_VAR\r\n";
            const string brokenImplementation = "sOut := nIn;\r\n";
            var writeDecl = await session.WritePlcObjectCodeAsync($"{verifyPath}^FB_McpBroken", false, brokenDeclaration, dryRun: false, "e2e", CancellationToken.None);
            Assert.True(writeDecl.Succeeded, $"Write declaration failed: {writeDecl.Error}");
            var writeImpl = await session.WritePlcObjectCodeAsync($"{verifyPath}^FB_McpBroken", true, brokenImplementation, dryRun: false, "e2e", CancellationToken.None);
            Assert.True(writeImpl.Succeeded, $"Write implementation failed: {writeImpl.Error}");

            // A POU that no program instantiates is pruned from TwinCAT's compile, so the compiler never
            // sees its errors. Instantiate FB_McpBroken in the project's first PROGRAM POU (found by its
            // tree kind, not by name — TEST_PROGRAM/MAIN aren't guaranteed) to force the compiler to
            // actually compile it.
            // A POU that no program instantiates is pruned from TwinCAT's compile, so the compiler never
            // sees its errors. Instantiate FB_McpBroken in the project's first PROGRAM POU to force the
            // compiler to actually compile it. Note: ItemSubType reads 0 for every node in this
            // automation shell (Kind is always "Unknown"), so a program is identified structurally — a
            // leaf that isn't a Reference/.tmc/PlcTask-task-entry whose declaration opens with
            // "PROGRAM" — instead of by name, since TEST_PROGRAM/MAIN aren't guaranteed.
            var tpTree = await session.GetProjectTreeAsync(plcRoot!, 5, CancellationToken.None);
            async Task<PlcTreeNode?> FindProgramAsync(PlcTreeNode? n)
            {
                if (n is null) return null;
                if (n.Children.Count == 0
                    && !n.TreePath.Contains("^References", StringComparison.OrdinalIgnoreCase)
                    && !n.TreePath.EndsWith(".tmc", StringComparison.OrdinalIgnoreCase)
                    && !n.TreePath.Contains("^PlcTask", StringComparison.OrdinalIgnoreCase))
                {
                    var code = await session.ReadPlcObjectCodeAsync(n.TreePath, CancellationToken.None);
                    if (code.Succeeded && code.Declaration?.TrimStart().StartsWith("PROGRAM ", StringComparison.OrdinalIgnoreCase) == true)
                        return n;
                }

                foreach (var c in n.Children)
                {
                    var hit = await FindProgramAsync(c);
                    if (hit is not null) return hit;
                }

                return null;
            }

            var testProgram = await FindProgramAsync(tpTree);
            Assert.NotNull(testProgram);
            _output.WriteLine($"Using program for the broken-FB instance: {testProgram!.TreePath}");
            testProgramPath = testProgram!.TreePath;

            var tpCode = await session.ReadPlcObjectCodeAsync(testProgramPath, CancellationToken.None);
            Assert.True(tpCode.Succeeded, $"Read TEST_PROGRAM failed: {tpCode.Error}");
            originalTpDeclaration = tpCode.Declaration ?? "";
            Assert.True(originalTpDeclaration.Contains("END_VAR", StringComparison.OrdinalIgnoreCase),
                "TEST_PROGRAM declaration should contain a VAR block to put the instance in.");
            var lastVarEnd = originalTpDeclaration.LastIndexOf("END_VAR", StringComparison.OrdinalIgnoreCase);
            var tpWithInstance = originalTpDeclaration.Insert(lastVarEnd, "\r\n\tfbMcpBroken : FB_McpBroken;\r\n");
            var instWrite = await session.WritePlcObjectCodeAsync(testProgramPath, false, tpWithInstance, dryRun: false, "e2e", CancellationToken.None);
            Assert.True(instWrite.Succeeded, $"Instantiate FB_McpBroken in TEST_PROGRAM failed: {instWrite.Error}");

            // Diagnostics: confirm the write persisted (in-memory), then dump the error list state.
            var readBack = await session.ReadPlcObjectCodeAsync($"{verifyPath}^FB_McpBroken", CancellationToken.None);
            _output.WriteLine($"ReadBack succeeded={readBack.Succeeded} declaration='{readBack.Declaration}' implementation='{readBack.Implementation}'");

            // AI text writes only update the in-memory project; a TwinCAT build/activate compiles what is
            // persisted. Persist (as a user would before activating over a real failing edit).
            var save = await session.SaveAllAsync("e2e", CancellationToken.None);
            _output.WriteLine($"SaveAll: succeeded={save.Succeeded} error='{save.Error}'");

            var persisted = await session.ReadPlcObjectCodeAsync($"{verifyPath}^FB_McpBroken", CancellationToken.None);
            _output.WriteLine($"ReadBack after save: implementation='{persisted.Implementation}'");

            // Did the save actually reach disk? The build/activate compiles what is persisted, so dump
            // the on-disk .TcPOU contents under McpVerify.
            var rootDir = Path.GetDirectoryName(solution)!;
            var diskPous = Directory.GetFiles(rootDir, "*.TcPOU", SearchOption.AllDirectories)
                .Where(f => f.Contains("McpVerify", StringComparison.OrdinalIgnoreCase));
            foreach (var f in diskPous)
                _output.WriteLine($"OnDisk {f}: {System.IO.File.ReadAllText(f)}");

            // Ground truth: the compiler must flag the type error now that FB_McpBroken is instantiated, and
            // ReadErrorList must see it. Clean forces a full compile rather than an incremental one.
            var clean = await session.CleanAsync(null, CancellationToken.None);
            _output.WriteLine($"Explicit Clean: success={clean.Succeeded} errors={clean.ErrorCount} warnings={clean.WarningCount}");
            var build = await session.BuildAsync(null, CancellationToken.None);
            _output.WriteLine($"Explicit Build (after clean): success={build.Succeeded} errors={build.ErrorCount} warnings={build.WarningCount} detail={build.Errors.Count}");
            foreach (var f in Directory.GetFiles(rootDir, "*.buildmsg", SearchOption.AllDirectories))
                _output.WriteLine($"BuildMsg {f}:{Environment.NewLine}{System.IO.File.ReadAllText(f)}");

            // The regression under test: activating a configuration whose implicit build fails used to
            // report success. It must now report NOT-success with a reason.
            var activate = await session.ActivateConfigurationAsync("e2e", CancellationToken.None);
            _output.WriteLine($"ActivateConfiguration result: succeeded={activate.Succeeded} applied={activate.Applied} error='{activate.Error}' detail='{activate.Detail}'");
            Assert.False(activate.Succeeded, "ActivateConfiguration must report failure when its implicit build fails.");
            Assert.False(string.IsNullOrWhiteSpace(activate.Error), "The failure must carry a reason.");
            // In a readable-error-list shell the message names the actual compiler error (nIn/sOut/INT);
            // in the headless automation shell it falls back to the LastBuildInfo count. Both prove the
            // verification reported the failed build rather than S_OK.
            Assert.Contains("failed project", activate.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // Drop the instance we injected into TEST_PROGRAM so the project isn't left referencing a
            // POU that no longer exists after the McpVerify folder is deleted below.
            if (testProgramPath is not null && originalTpDeclaration is not null)
            {
                try
                {
                    await session.WritePlcObjectCodeAsync(testProgramPath, false, originalTpDeclaration, dryRun: false, "e2e", CancellationToken.None);
                    await session.SaveAllAsync("e2e", CancellationToken.None);
                }
                catch { /* best effort — close regardless */ }
            }

            if (createdFolder && plcRoot is not null)
            {
                try
                {
                    await session.DeletePlcObjectAsync($"{plcRoot}^McpVerify", "e2e", CancellationToken.None);
                    await session.SaveAllAsync("e2e", CancellationToken.None);
                }
                catch { /* best effort — close regardless */ }
            }

            await session.CloseProjectAsync(CancellationToken.None);
        }
    }
}
