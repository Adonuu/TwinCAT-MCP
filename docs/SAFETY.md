# Safety model

This server can read TwinCAT PLC project source, read/write live runtime variables and
state, and drive the XAE Shell's build/automation interface — including operations that
can disrupt a running physical system. Every operation that *changes* anything funnels
through one chokepoint, `TwinCatMcp.Safety.SafetyGate`, before it touches disk, the ADS
runtime, or the XAE Shell.

The model deliberately stays to three independent on/off toggles, changeable live. There
is no master "SafeMode" switch and no allow-lists — those added friction without buying
much, and settings are meant to be flipped mid-session, not edited in a config file.

Read/browse/search tools (`list_plc_objects`, `read_pou_source`, `search_plc_source`,
`get_project_structure`, `read_symbol`, `browse_symbols`, `describe_type`, `get_plc_state`,
`get_project_status`, `get_build_errors`, `list_hardware_configurations`, ...) are **never**
gated — they remain fully available regardless of the toggles.

## Configuration (`Safety` section)

Seed the defaults from `appsettings.json` or environment variables (`Safety__AllowAdsWrites=true`,
...), then change them live with the `set_safety_toggles` tool (see below).

| Option | Default | Effect |
|---|---|---|
| `AllowAdsWrites` | `false` | Allows `write_symbol` / `write_symbols_batch` — writing live PLC variable values via ADS. |
| `AllowDownloads` | `false` | Allows `activate_configuration` — downloading/activating a configuration to the runtime, which stops, reloads, and restarts the PLC. |
| `AllowRuntimeStateChanges` | `false` | Allows `set_plc_state` (Run/Stop/Reset/Config) and `restart_twincat` (cold runtime restart). |

Everything else mutating is **not gated** by this policy: file-based source edits
(`write_pou_declaration` / `write_pou_implementation`), `write_plc_object_*`,
`build_project`, `clean_project`, `save_project`, `create_plc_object`,
`delete_plc_object`, `import_plc_object`, `export_plc_object`. Those are the tools the
server exists to provide (especially source edits) — gating them would defeat the purpose.
They are still **recorded** to the audit log.

> Removal note: the old `SafeMode`, `WritableSymbolPatterns`, `WritableSourcePathPatterns`,
> `AllowedStateTransitions`, `AlwaysConfirmStateTransitions`, and
> `AlwaysConfirmAutomationOperations` options are gone. Existing config keys under
> `Safety:` other than the three toggles are ignored.

## How a check resolves

`SafetyGate` evaluates each mutating call against exactly one toggle:

1. `write_symbol` / `write_symbols_batch` → allowed only if `AllowAdsWrites` is on.
2. `activate_configuration` → allowed only if `AllowDownloads` is on.
3. `set_plc_state` / `restart_twincat` → allowed only if `AllowRuntimeStateChanges` is on.
4. Everything else (source edits, build/clean, PLC object create/delete/import/export,
   save) → always allowed.

Whatever the outcome, every check returns a `SafetyDecision` (`Verdict` + human-readable
`Reason`) that rides along in the tool's JSON result, and the check is appended to the
audit log. Nothing fails silently.

## Changing the toggles at runtime

The defaults are seeded once at startup; after that you can flip them live, with no
restart, from the tool surface:

- **`set_safety_toggles`** — pass one or more of `allowAdsWrites`, `allowDownloads`,
  `allowRuntimeStateChanges` (`true`/`false`) to change them mid-session. Omit a toggle to
  leave it unchanged. In practice you'd tell the agent, e.g. *"turn on ADS writes, write
  the recipe values, then turn them back off"* — that becomes
  `set_safety_toggles` → the writes → `set_safety_toggles` again, all visible in the
  transcript.
- **`get_safety_toggles`** — reports the current state so you (or the agent) can check
  before attempting a write.

Each `set_safety_toggles` call is itself written to the audit log as a `PolicyChanged`
entry, so every permission flip is traceable. Toggles are **session-scoped**: a server
restart resets them to the configured defaults.

## Audit log

Every mutating attempt — allowed or denied — is appended as one JSON object per line to
`twincat-mcp-audit.jsonl`, resolved under the process working directory (the indexed PLC
project root). This is a fixed, non-configurable path: there is no way to run the server
without recording. Each entry has `TimestampUtc`, `Operation`, `Subject`, `Verdict`,
`Reason`, and optional `Details`. Failures to write the log are logged but never thrown —
an audit-trail outage must not be able to take the server down.

## Recommended rollout

1. **Start with all toggles off** (the default). Explore read-only: list/read PLC source,
   read live symbols, read build errors and hardware configs. Confirm `twincat-mcp-audit.jsonl`
   is being written and the tool surface looks right.
2. **Enable only what the session needs.** For a source-editing session you need nothing
   on — source edits aren't gated. Add `AllowAdsWrites` only when the agent should poke live
   variables (e.g. test values on a fixture), `AllowDownloads` only when you want it to
   download/activate, and `AllowRuntimeStateChanges` only when it may stop/restart the PLC.
   Flip them off as soon as the task's done.
3. **Tail the audit log** (`tail -f twincat-mcp-audit.jsonl`) while running an agent
   session against anything other than a sandbox, so every mutation is visible to a human
   in real time.

One thing to keep in mind: because source edits and PLC object create/delete/import/export
are intentionally ungated, an agent with the server connected can modify the project tree
the moment all toggles are left as-is. The audit log is the record of it, not a blocker.