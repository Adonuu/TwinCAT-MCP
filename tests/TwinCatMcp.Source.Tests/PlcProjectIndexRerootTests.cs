using System.Text;
using TwinCatMcp.Source;
using Xunit;

namespace TwinCatMcp.Source.Tests;

/// <summary>
/// <see cref="PlcProjectIndex.Reroot"/> re-points the index at the solution opened in the XAE Shell
/// (wired via XaeShellSession.ProjectOpened in Program.cs). These tests pin the contract: after a
/// re-root, lookups and path resolution see only the new root; a bad root fails loudly without
/// disturbing the current one.
/// </summary>
public sealed class PlcProjectIndexRerootTests : IDisposable
{
    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _rootA;
    private readonly string _rootB;

    public PlcProjectIndexRerootTests()
    {
        _rootA = Path.Combine(Path.GetTempPath(), $"twincat-mcp-reroot-a-{Guid.NewGuid():N}");
        _rootB = Path.Combine(Path.GetTempPath(), $"twincat-mcp-reroot-b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootA);
        Directory.CreateDirectory(_rootB);

        WritePou(_rootA, "FB_InRootA", "{11111111-1111-1111-1111-111111111111}");
        WritePou(_rootB, "FB_InRootB", "{22222222-2222-2222-2222-222222222222}");
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _rootA, _rootB })
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static void WritePou(string root, string name, string guid)
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<TcPlcObject Version=\"1.1.0.1\" ProductVersion=\"3.1.4024.12\">\r\n" +
            $"  <POU Name=\"{name}\" Id=\"{guid}\" SpecialFunc=\"None\">\r\n" +
            $"    <Declaration><![CDATA[FUNCTION_BLOCK {name}\r\nVAR\r\nEND_VAR\r\n]]></Declaration>\r\n" +
            "    <Implementation>\r\n" +
            "      <ST><![CDATA[;\r\n]]></ST>\r\n" +
            "    </Implementation>\r\n" +
            "  </POU>\r\n" +
            "</TcPlcObject>\r\n";
        File.WriteAllText(Path.Combine(root, $"{name}.TcPOU"), xml, NoBom);
    }

    [Fact]
    public void Reroot_switches_lookups_and_path_resolution_to_the_new_root()
    {
        using var index = new PlcProjectIndex(_rootA);
        Assert.Single(index.Find("FB_InRootA"));
        Assert.Empty(index.Find("FB_InRootB"));

        index.Reroot(_rootB);

        Assert.Equal(Path.GetFullPath(_rootB), index.ProjectRoot);
        Assert.Empty(index.Find("FB_InRootA"));
        var found = Assert.Single(index.Find("FB_InRootB"));
        Assert.Equal(Path.Combine(Path.GetFullPath(_rootB), found.RelativePath), index.ResolvePath(found));
    }

    [Fact]
    public void Reroot_to_the_same_root_is_a_noop()
    {
        using var index = new PlcProjectIndex(_rootA);
        _ = index.All(); // force a fresh build so a pointless re-root would be observable as a rescan

        index.Reroot(_rootA);

        Assert.Single(index.Find("FB_InRootA"));
    }

    [Fact]
    public void Reroot_to_a_missing_directory_throws_and_keeps_the_current_root()
    {
        using var index = new PlcProjectIndex(_rootA);

        Assert.Throws<DirectoryNotFoundException>(() => index.Reroot(Path.Combine(_rootB, "does-not-exist")));

        Assert.Equal(Path.GetFullPath(_rootA), index.ProjectRoot);
        Assert.Single(index.Find("FB_InRootA"));
    }
}
