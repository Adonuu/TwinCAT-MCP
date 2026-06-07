# TwinCAT 3 MCP Server

An MCP (Model Context Protocol) server that lets an AI agent work with a Beckhoff
TwinCAT 3 PLC project across three independent scopes:

1. **PLC project source** (file-based) — browse/read/search/edit POUs (`.TcPOU`),
   GVLs (`.TcGVL`), DUTs (`.TcDUT`) on disk. Portable — works on any OS.
2. **Live runtime** (ADS) — connect to a running TwinCAT runtime, read/write
   variables, call methods, subscribe to changes, control run state.
3. **XAE Shell automation** (COM) — drive the TwinCAT engineering IDE: open/build/
   clean a project, read build errors, activate hardware configs, restart the
   runtime. Windows-only, requires a local TwinCAT XAE Shell install.

Every operation that changes anything — source edits, symbol writes, state
transitions, builds, restarts — is gated by a central safety policy with
`SafeMode`/allow-lists/`dryRun`/`confirm` and an append-only audit log. See
[`docs/SAFETY.md`](docs/SAFETY.md) before enabling any writes.

## The three scopes hook up to *different* things

This is the part that trips people up: "the project" means three different things
depending on which scope you're using, and they're configured in three different
ways.

| Scope | What it connects to | How it's configured |
|---|---|---|
| **Source** | A folder on disk containing `.TcPOU`/`.TcGVL`/`.TcDUT` files | Defaults to the server's current working directory (an MCP client launches it as a child process inheriting its own cwd) — override with `PROJECT_PATH` env var / `PlcProject:Root` config if needed |
| **Runtime** | A running ADS target (a TwinCAT runtime, identified by AMS Net ID + port) | `Runtime:AmsNetId` / `Runtime:AmsPort` config — or passed per-call to the `connect_ads` tool |
| **Automation** | An open XAE Shell solution (`.sln`/`.tsproj` file) | **Not config** — passed live to the `open_xae_project` tool by the agent during a session |

You can use any subset of these independently — e.g. point `PROJECT_PATH` at a PLC
project's source folder for browsing/editing, without ever touching ADS or the XAE
Shell.

### 1. Source scope — auto-detected from the working directory

By default the server indexes whatever's in **its own current working directory** —
which an MCP client inherits when it launches the server as a child process. So if
your client (Claude Desktop, Claude Code, etc.) launches `twincat-mcp` from (or
pointed at) a folder that contains a PLC project, that project is indexed
automatically. No path configuration needed.

`PlcProjectIndex` recursively scans the resolved root for
`*.TcPOU`/`*.TcGVL`/`*.TcDUT` files (`SearchOption.AllDirectories`), so it doesn't
matter exactly which folder in the tree the cwd is — as long as your PLC source
files live somewhere underneath it (and it's perfectly happy to find none, e.g. if
you launch it from an unrelated directory — `list_plc_objects` just comes back
empty). A typical TwinCAT solution looks like:

```
MySolution/
├── MySolution.sln
├── MySolution.tsproj
└── PLC/
    └── MyPlcProject/
        ├── MyPlcProject.plcproj
        ├── POUs/
        │   └── MAIN.TcPOU
        ├── GVLs/
        │   └── GVL_Globals.TcGVL
        └── DUTs/
```

If cwd-based detection isn't right for your setup — e.g. the client always launches
the server from some other directory — set `PROJECT_PATH` (env var) or
`PlcProject:Root` (config) to override it explicitly. Pointing it at
`MySolution/PLC/MyPlcProject` (the PLC project folder itself) is the most precise —
it indexes exactly the objects that belong to that PLC project. Pointing it at
`MySolution` also works (it'll just walk a larger tree, including any other PLC
projects in the same solution).

The index also runs a `FileSystemWatcher`, so edits made through the IDE while the
server is running are picked up automatically — no restart needed to see them
reflected in `list_plc_objects`/`read_pou_source`.

### 2. Runtime scope — pointing at an ADS target

This doesn't point at "a project" at all — it points at a **running PLC runtime**,
addressed by its AMS Net ID (e.g. `127.0.0.1.1.1` for a local loopback target, or
the Net ID shown in the TwinCAT system tray icon for a remote one) and port
(`851` for the standard PLC runtime).

Set `Runtime:AmsNetId`/`Runtime:AmsPort` in config to have the server connect on
first use, or leave them unset and let the agent call `connect_ads(amsNetId, port)`
explicitly — useful if you want to target different runtimes across a session.
`AdsConnectionManager` reconnects transparently if the target changes.

Note that the live symbols you'll see/read/write are whatever's *currently loaded
and running* on that target — which is the build output of some PLC project, but
the connection itself has no notion of "project," only of the running process.

### 3. Automation scope — opening a solution in the XAE Shell

This is the only scope where "the project" is something the agent opens at runtime,
not something you configure up front. The agent calls:

```
open_xae_project(path: "C:\path\to\MySolution\MySolution.sln")
```

(a `.tsproj` also works) which launches/attaches to a `TcXaeShell.DTE` COM instance
and opens that solution — after which `build_project`, `get_build_errors`,
`list_hardware_configurations`, `activate_configuration`, `restart_twin_cat`, etc.
operate on it. `Automation:ShowIde` (default `false`) controls whether the shell
window is visible while the agent drives it — handy to set `true` the first time so
you can watch what it's doing.

`Automation:DteProgId` must match what's actually registered on the machine — see
step 3 of the setup below.

## Setup (Windows engineering workstation)

The Automation scope is COM-based and Windows-only; the Runtime scope needs a
reachable ADS target; the Source scope is portable. Running the whole server on the
engineering workstation (alongside the installed XAE Shell, with a local/loopback
runtime) is the natural fit for all three at once.

The server is packaged as a standard **MCP server NuGet package** — the
established way to distribute a local (stdio) .NET MCP server (see
[NuGet's MCP server docs](https://learn.microsoft.com/en-us/nuget/concepts/nuget-mcp)
and the manifest at [`.mcp/server.json`](.mcp/server.json)). Your MCP client
launches it with **`dnx`** — the .NET-ecosystem equivalent of `npx`/`uvx` — which
downloads and runs it in one shot, no SDK or source checkout required on the
engineering workstation. `dnx` ships with the **.NET 10 SDK**.

### Prerequisites
- **`dnx`** (ships with the .NET 10 SDK) to launch the published package — or the
  **.NET 8 SDK** if you'd rather [build from source](#building-from-source-contributors)
- **TwinCAT XAE Shell** installed locally, with a local or reachable PLC runtime

### 1. Find your COM ProgID (for the Automation scope)
```powershell
reg query HKEY_CLASSES_ROOT /f "TcXaeShell.DTE" /k /s
```
Use the exact version string this returns (e.g. `TcXaeShell.DTE.15.0`) for
`Automation:DteProgId` — it varies by TwinCAT/Visual Studio version.

### 2. Find your AMS Net ID (for the Runtime scope)
Shown in the TwinCAT system tray icon, or `127.0.0.1.1.1` for a local loopback
target. Port `851` is the standard PLC runtime port.

### 3. Configure
Copy [`docs/claude-desktop-config.sample.json`](docs/claude-desktop-config.sample.json)
into your MCP client's config and edit the `env` block:

```json
{
  "mcpServers": {
    "twincat": {
      "command": "dnx",
      "args": ["Adonuu.TwinCatMcp@0.1.0", "--yes"],
      "env": {
        "Runtime__AmsNetId": "127.0.0.1.1.1",
        "Runtime__AmsPort": "851",
        "Automation__DteProgId": "TcXaeShell.DTE.15.0",
        "Automation__ShowIde": "false",
        "Safety__SafeMode": "true"
      }
    }
  }
}
```

Note there's no `PROJECT_PATH` here — the Source scope defaults to wherever your
client launches the server from (see [Source scope](#1-source-scope--auto-detected-from-the-working-directory)
above). Add `PROJECT_PATH`/`PlcProject:Root` to `env` only if that default isn't
right for your setup.

(Double underscores `__` are .NET configuration's standard way of expressing nested
section keys — e.g. `Runtime__AmsNetId` binds to `RuntimeOptions.AmsNetId` under the
`Runtime` config section.)

Leave `Safety__SafeMode` as `true` to start — every read/browse/search tool works
fully in this mode; only mutating operations are blocked. See
[`docs/SAFETY.md`](docs/SAFETY.md) for the full policy model and a recommended
staged rollout to enabling writes.

### 4. Smoke-test with MCP Inspector before wiring up a real client
```powershell
npx @modelcontextprotocol/inspector dnx Adonuu.TwinCatMcp@0.1.0 -- --yes
```
Click through the tools interactively — particularly `connect_ads`, `get_plc_state`,
`browse_symbols` (Runtime) and `open_xae_project`, `get_project_status`,
`list_hardware_configurations` (Automation), since these need a real ADS target /
XAE Shell to function and can't be exercised on a non-Windows dev machine.

### 5. Wire it into your MCP client
Merge the edited `mcpServers.twincat` entry into your client's config (e.g.
`claude_desktop_config.json` for Claude Desktop) and restart the client.

## Building from source (contributors)

```powershell
dotnet build -c Release
dotnet test -c Release      # 22 portable tests (Safety + Source) should pass anywhere
```

To run the server straight from a checkout (e.g. while developing), point your MCP
client at it directly instead of via `dnx`:

```json
{
  "command": "dotnet",
  "args": ["run", "--project", "C:\\path\\to\\twincat-mcp\\src\\TwinCatMcp.Server", "-c", "Release"]
}
```

and likewise swap `dnx Adonuu.TwinCatMcp@0.1.0 -- --yes` for
`dotnet run --project src/TwinCatMcp.Server -c Release` when smoke-testing with the
MCP Inspector.

## Publishing your own build

The shipped `PackageId` (`Adonuu.TwinCatMcp`) is this repo's own; if you fork or
rebrand, change `<PackageId>`/`<ToolCommandName>` in
[`TwinCatMcp.Server.csproj`](src/TwinCatMcp.Server/TwinCatMcp.Server.csproj) and
`name`/`packages[].identifier` in [`.mcp/server.json`](.mcp/server.json) to your own
unique id first. Then, from a Windows machine (the package is built
self-contained for `win-x64`):

```powershell
dotnet pack src/TwinCatMcp.Server -c Release
# inspect src/TwinCatMcp.Server/bin/Release/*.nupkg, then:
dotnet nuget push src/TwinCatMcp.Server/bin/Release/<YourPackageId>.<version>.nupkg `
  --source https://api.nuget.org/v3/index.json --api-key <your-nuget-api-key>
```

This requires your own NuGet.org account and API key — publishing makes the package
publicly resolvable via `dnx`/`dotnet tool install`, so it's worth confirming the
package contents (`dotnet pack` output, or unzip the `.nupkg`) before pushing.

## License

[MIT](LICENSE) — use it, fork it, ship it, just keep the copyright notice. Provided
"as is", with no warranty; see the [`LICENSE`](LICENSE) file for the full text.
