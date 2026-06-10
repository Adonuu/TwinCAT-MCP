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
claude mcp add-json twincat '{"type":"stdio","command":"twincat-mcp","env":{"Runtime__AmsNetId":"127.0.0.1.1.1","Runtime__AmsPort":"851","Safety__SafeMode":"true"}}'
```

Leave `Safety__SafeMode` as `true` to start — every read/browse/search tool works
fully in this mode; only mutating operations are blocked. See
[`docs/SAFETY.md`](docs/SAFETY.md) for the full policy model and a recommended
staged rollout to enabling writes.

## License

[MIT](LICENSE) — use it, fork it, ship it, just keep the copyright notice. Provided
"as is", with no warranty; see the [`LICENSE`](LICENSE) file for the full text.
