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

## What you can ask it to do

A few examples of the kind of thing an agent can do once connected, one per scope:

- **Source**: "Find the POU that handles recipe validation and show me its
  implementation" — the agent searches and reads source across POUs/GVLs/DUTs
  on disk, and (with writes enabled) can edit declarations/implementations or
  scaffold new objects.
- **Runtime**: "What's the current value of the active recipe struct, and let
  me know if `MAIN.fbConveyor.eState` changes" — the agent reads/browses live
  ADS symbols and subscribes to value-change notifications, polling for samples
  as they arrive.
- **Automation**: "Build the project and show me any errors" — the agent drives
  the XAE Shell to build/clean and reports back the resulting error list.

Anything that changes state — symbol writes, run-state transitions, builds,
restarts — goes through the safety policy described next, so these stay safe to
explore even with writes enabled.

Every operation that changes anything — source edits, symbol writes, state
transitions, builds, restarts — is gated by a central safety policy with
`SafeMode`/allow-lists/`dryRun`/`confirm` and an append-only audit log. See
[`docs/SAFETY.md`](docs/SAFETY.md) before enabling any writes.

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
project-local `.mcp.json` instead.

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
