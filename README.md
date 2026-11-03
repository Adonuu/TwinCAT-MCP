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
  on disk, and (with writes enabled) can edit declarations/implementations.
- **Runtime**: "What's the current value of the active recipe struct, and let
  me know if `MAIN.fbConveyor.eState` changes" — the agent reads/browses live
  ADS symbols and subscribes to value-change notifications, polling for samples
  as they arrive.
- **Automation**: "Build the project and show me any errors" — the agent drives
  the XAE Shell to build/clean and reports back the resulting error list.

Anything that touches the live runtime — symbol writes, run-state changes, configuration
downloads — is gated by three safety toggles described next; read/browse/search tools are
always available.

Every operation that changes anything — source edits, symbol writes, state changes, builds,
restarts — is recorded to an always-on append-only audit log (`twincat-mcp-audit.jsonl` in
the working directory), regardless of whether a safety toggle allowed or blocked it. The
three toggles (`AllowAdsWrites`, `AllowDownloads`, `AllowRuntimeStateChanges`) can be
flipped live at runtime via the `set_safety_toggles` tool — no restart needed. See
[`docs/SAFETY.md`](docs/SAFETY.md) before enabling any of them.

Like the toggles, the default ADS target is changeable live: `set_runtime_target` swaps the
session default `amsNetId`/`port` (audit-logged, reconnects immediately) and
`get_runtime_target` reports it. Both reset to the configured `Runtime__AmsNetId` /
`Runtime__AmsPort` values on server restart.

## Connect to an already open XAE Shell

The Automation scope can attach to an interactive **32-bit TcXAEShell**
(`TcXaeShell.DTE.15.0`) or **64-bit TcXAEShell** (`TcXaeShell.DTE.17.0`),
using the Windows Running Object Table, as IOImporter does. The x64 MCP server
can attach to either shell through out-of-process COM; no x86 server is needed.
Run the server as the same Windows user and at the same elevation as the IDE.
Busy, exiting or inaccessible instances may be omitted from discovery; retry
discovery when the IDE is ready.

1. Call `list_xae_shell_instances` to get each accessible shell's `InstanceId`,
   `ProcessId`, `ProgId`, `Bitness` and `SolutionPath` (null for an empty shell).
2. Call `attach_xae_shell` with `instanceId` from that list, or an absolute
   `solutionPath`. Omit both only when exactly one instance is accessible.
   Multiple matches require an explicit instance ID.
3. Use the existing project, build and tree tools against that IDE. Source tools
   follow the attached solution's directory. `get_xae_shell_connection` reports
   the connection and refreshes its current solution path.
4. Call `disconnect_xae_shell` before selecting another IDE.

Attachment keeps the current solution, pending edits and IDE visibility intact.
Disconnecting or shutting down the MCP server leaves an attached IDE open.
`close_project` still explicitly closes its solution **without saving**.
`open_xae_project` keeps its launch behavior when disconnected; disconnecting
or shutting down quits an IDE created by the server. Attaching does not change
the ADS runtime target or safety toggles.

## Setup (Windows engineering workstation)

The Automation scope is COM-based and Windows-only; the Runtime scope needs a
reachable ADS target; the Source scope is portable. Running the whole server on the
engineering workstation (alongside the installed XAE Shell, with a local/loopback
runtime) is the natural fit for all three at once.

> **Windows only** — the package is built self-contained for `win-x64`. The Automation scope requires TwinCAT XAE Shell (COM/Windows), and the Runtime scope requires a reachable ADS target.

### Prerequisites
- **TwinCAT XAE Shell** installed locally, with a local or reachable PLC runtime

### 1. Find your AMS Net ID (for the Runtime scope)
Shown in the TwinCAT system tray icon, or `127.0.0.1.1.1` for a local loopback
target. Port `851` is the standard PLC runtime port.

### 2. Install
Install the server as a .NET tool (see the
[NuGet package](https://www.nuget.org/packages/Adonuu.TwinCatMcp) for the install
command), which puts a `twincat-mcp` binary on your PATH.

### 3. Configure

#### Claude Code
```bash
claude mcp add twincat twincat-mcp --env Runtime__AmsNetId=127.0.0.1.1.1 --env Runtime__AmsPort=851 --env Automation__ShowIde=true
```

#### OpenCode

Add the server under `mcp` in your [`opencode.json`](https://opencode.ai/docs/mcp-servers/)
(either the project-level `opencode.json` or the global config via
`opencode mcp add`):

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "twincat": {
      "type": "local",
      "command": ["twincat-mcp"],
      "enabled": true,
      "environment": {
        "Runtime__AmsNetId": "127.0.0.1.1.1",
        "Runtime__AmsPort": "851",
        "Automation__ShowIde": "false"
      }
    }
  }
}
```

## License

[MIT](LICENSE) — use it, fork it, ship it, just keep the copyright notice. Provided
"as is", with no warranty; see the [`LICENSE`](LICENSE) file for the full text.
