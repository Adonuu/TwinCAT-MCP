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
| **Source** | A folder on disk containing `.TcPOU`/`.TcGVL`/`.TcDUT` files | Always the server's current working directory — an MCP client launches it as a child process inheriting its own cwd, so there's nothing to configure |
| **Runtime** | A running ADS target (a TwinCAT runtime, identified by AMS Net ID + port) | `Runtime:AmsNetId` / `Runtime:AmsPort` config — or passed per-call to the `connect_ads` tool |
| **Automation** | An open XAE Shell solution (`.sln`/`.tsproj` file) | **Not config** — passed live to the `open_xae_project` tool by the agent during a session |

You can use any subset of these independently — e.g. launch the server from inside a
PLC project's source folder for browsing/editing, without ever touching ADS or the
XAE Shell.

### 1. Source scope — always the working directory

The server indexes whatever's in **its own current working directory** — which an
MCP client inherits when it launches the server as a child process. So if your
client (Claude Desktop, Claude Code, etc.) launches `twincat-mcp` from (or pointed
at, e.g. via a `cwd` setting) a folder that contains a PLC project, that project is
indexed automatically. There is no path to configure — `PROJECT_PATH`/
`PlcProject:Root` aren't options; wherever the server runs from *is* the project.

`PlcProjectIndex` recursively scans the working directory for
`*.TcPOU`/`*.TcGVL`/`*.TcDUT` files (`SearchOption.AllDirectories`), so it doesn't
matter exactly which folder in the tree you're in — as long as your PLC source files
live somewhere underneath it (and it's perfectly happy to find none, e.g. if you
launch it from an unrelated directory — `list_plc_objects` just comes back empty). A
typical TwinCAT solution looks like:

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

Running the server from `MySolution/PLC/MyPlcProject` (the PLC project folder
itself) is the most precise — it indexes exactly the objects that belong to that PLC
project. Running it from `MySolution` also works (it'll just walk a larger tree,
including any other PLC projects in the same solution).

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

### 1. Find your AMS Net ID (for the Runtime scope)
Shown in the TwinCAT system tray icon, or `127.0.0.1.1.1` for a local loopback
target. Port `851` is the standard PLC runtime port.

### 2. Configure
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
        "Automation__ShowIde": "false",
        "Safety__SafeMode": "true"
      }
    }
  }
}
```

Note there's no `PROJECT_PATH` here — that's not a thing the server reads. The
Source scope is always whatever's in the working directory the client launches it
from (see [Source scope](#1-source-scope--always-the-working-directory) above);
point your client's `cwd`/working-directory setting at your PLC project if it isn't
already there.

(Double underscores `__` are .NET configuration's standard way of expressing nested
section keys — e.g. `Runtime__AmsNetId` binds to `RuntimeOptions.AmsNetId` under the
`Runtime` config section.)

Leave `Safety__SafeMode` as `true` to start — every read/browse/search tool works
fully in this mode; only mutating operations are blocked. See
[`docs/SAFETY.md`](docs/SAFETY.md) for the full policy model and a recommended
staged rollout to enabling writes.

#### Claude Code
Register it with the `claude mcp add` CLI (or hand-edit `.mcp.json` — same
`mcpServers` JSON shape shown above):

```bash
claude mcp add --transport stdio \
  --env Runtime__AmsNetId=127.0.0.1.1.1 \
  --env Runtime__AmsPort=851 \
  --env Safety__SafeMode=true \
  twincat -- dnx Adonuu.TwinCatMcp@0.1.0 --yes
```

By default this writes to `~/.claude.json`; add `--scope project` to write a
project-local `.mcp.json` instead. Claude Code has no `cwd` config field — the
server simply inherits whatever directory you run `claude` from, so `cd` into your
PLC project first (see [Source scope](#1-source-scope--always-the-working-directory)
above).

#### OpenCode
Add an entry under `mcp` in `opencode.json` (project root, or
`~/.config/opencode/opencode.json` for a global server):

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "twincat": {
      "type": "local",
      "command": ["dnx", "Adonuu.TwinCatMcp@0.1.0", "--yes"],
      "environment": {
        "Runtime__AmsNetId": "127.0.0.1.1.1",
        "Runtime__AmsPort": "851",
        "Safety__SafeMode": "true"
      }
    }
  }
}
```

Like Claude Code, OpenCode has no `cwd` field either — it launches the server from
its own working directory, so run `opencode` from inside your PLC project.

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
