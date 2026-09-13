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
