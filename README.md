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
| **Source** | A folder on disk containing `.TcPOU`/`.TcGVL`/`.TcDUT` files | `PROJECT_PATH` env var / `PlcProject:Root` config — set **once**, at server startup |
| **Runtime** | A running ADS target (a TwinCAT runtime, identified by AMS Net ID + port) | `Runtime:AmsNetId` / `Runtime:AmsPort` config — or passed per-call to the `connect_ads` tool |
| **Automation** | An open XAE Shell solution (`.sln`/`.tsproj` file) | **Not config** — passed live to the `open_xae_project` tool by the agent during a session |

You can use any subset of these independently — e.g. point `PROJECT_PATH` at a PLC
project's source folder for browsing/editing, without ever touching ADS or the XAE
Shell.

### 1. Source scope — pointing `PROJECT_PATH` at your project

`PlcProjectIndex` recursively scans everything under `PROJECT_PATH` for
`*.TcPOU`/`*.TcGVL`/`*.TcDUT` files (`SearchOption.AllDirectories`), so it doesn't
matter exactly which folder in the tree you point at — as long as your PLC source
files live somewhere underneath it. A typical TwinCAT solution looks like:

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

Pointing `PROJECT_PATH` at `MySolution/PLC/MyPlcProject` (the PLC project folder
itself) is the most precise — it indexes exactly the objects that belong to that
PLC project. Pointing it at `MySolution` also works (it'll just walk a larger tree,
including any other PLC projects in the same solution).

`PROJECT_PATH` (or `PlcProject:Root`) is required — the server fails fast at
startup if neither is set, so a missing/misconfigured path is never silently
papered over.

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

### Prerequisites
- **.NET 8 SDK**
- **TwinCAT XAE Shell** installed locally, with a local or reachable PLC runtime

### 1. Build
```powershell
dotnet build -c Release
dotnet test -c Release      # 22 portable tests (Safety + Source) should pass anywhere
```

### 2. Find your COM ProgID (for the Automation scope)
```powershell
reg query HKEY_CLASSES_ROOT /f "TcXaeShell.DTE" /k /s
```
Use the exact version string this returns (e.g. `TcXaeShell.DTE.15.0`) for
`Automation:DteProgId` — it varies by TwinCAT/Visual Studio version.

### 3. Find your AMS Net ID (for the Runtime scope)
Shown in the TwinCAT system tray icon, or `127.0.0.1.1.1` for a local loopback
target. Port `851` is the standard PLC runtime port.

### 4. Configure
Copy [`docs/claude-desktop-config.sample.json`](docs/claude-desktop-config.sample.json)
into your MCP client's config and edit the `env` block:

```json
{
  "mcpServers": {
    "twincat": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\twincat-mcp\\src\\TwinCatMcp.Server", "-c", "Release"],
      "env": {
        "PROJECT_PATH": "C:\\path\\to\\MySolution\\PLC\\MyPlcProject",
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

(Double underscores `__` are .NET configuration's standard way of expressing nested
section keys — e.g. `Runtime__AmsNetId` binds to `RuntimeOptions.AmsNetId` under the
`Runtime` config section.)

Leave `Safety__SafeMode` as `true` to start — every read/browse/search tool works
fully in this mode; only mutating operations are blocked. See
[`docs/SAFETY.md`](docs/SAFETY.md) for the full policy model and a recommended
staged rollout to enabling writes.

### 5. Smoke-test with MCP Inspector before wiring up a real client
```powershell
npx @modelcontextprotocol/inspector dotnet run --project src/TwinCatMcp.Server -c Release
```
Click through the tools interactively — particularly `connect_ads`, `get_plc_state`,
`browse_symbols` (Runtime) and `open_xae_project`, `get_project_status`,
`list_hardware_configurations` (Automation), since these need a real ADS target /
XAE Shell to function and can't be exercised on a non-Windows dev machine.

### 6. Wire it into your MCP client
Merge the edited `mcpServers.twincat` entry into your client's config (e.g.
`claude_desktop_config.json` for Claude Desktop) and restart the client.
