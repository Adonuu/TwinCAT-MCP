# Safety model

This server can read TwinCAT PLC project source, read/write live runtime variables and
state, and drive the XAE Shell's build/automation interface — including operations that
can disrupt a running physical system. Every operation that *changes* anything funnels
through one chokepoint, `TwinCatMcp.Safety.SafetyGate`, before it touches disk, the ADS
runtime, or the XAE Shell.

Read/browse/search tools (`list_plc_objects`, `read_pou_source`, `search_plc_source`,
`get_project_structure`, `read_symbol`, `browse_symbols`, `describe_type`, `get_plc_state`,
`get_project_status`, `get_build_errors`, `list_hardware_configurations`, ...) are **never**
gated — they remain fully available regardless of policy.

## Configuration (`Safety` section)

Bind these from `appsettings.json`, environment variables (`Safety__SafeMode=true`, ...),
or your MCP client's `env` block (see `claude-desktop-config.sample.json`).

| Option | Default | Effect |
|---|---|---|
| `SafeMode` | `true` | When `true`, **every** mutating operation is denied outright — source edits, symbol writes, state changes, build/clean/automation. This is the fail-closed default; an operator must deliberately turn it off. |
| `WritableSymbolPatterns` | `[]` | Glob patterns (`*`, `?`, case-insensitive) matched against the dotted ADS symbol name. `write_symbol`/`write_symbols_batch` only succeed for symbols matching at least one entry. |
| `WritableSourcePathPatterns` | `[]` | Glob patterns matched against a PLC object's project-relative path (e.g. `POUs/Generated/*`). `write_pou_declaration`/`write_pou_implementation` only succeed for matching files. |
| `AllowedStateTransitions` | `[]` | Explicit `"From->To"` pairs (e.g. `"Stop->Run"`). `set_plc_state` denies any transition not listed here, regardless of `SafeMode`. |
| `AlwaysConfirmStateTransitions` | `true` | Even an allow-listed transition still requires the caller to re-invoke with `confirm=true`. |
| `AlwaysConfirmAutomationOperations` | `["ActivateConfiguration", "RestartTwinCat"]` | Automation operation names that always require `confirm=true`, independent of `SafeMode`/allow-lists (build/clean/etc. are not allow-list-gated, but `SafeMode` still blocks them). |
| `AuditLogPath` | `twincat-mcp-audit.jsonl` | Append-only JSON-lines audit trail. A relative path is resolved under the configured PLC project root (falling back to the working directory). |

## How a check resolves

`SafetyGate` evaluates every mutating call in this order — see `SafetyGate.Evaluate`:

1. **`SafeMode` on?** → instant deny, with a reason naming `Safety:SafeMode`. Nothing else
   is checked.
2. **Allow-listed?** (symbol/path patterns, or state transition in `AllowedStateTransitions`)
   → if not, deny with a reason naming the specific config key that would need an entry.
3. **Confirmation required?** (`AlwaysConfirmStateTransitions`, or the operation name appears
   in `AlwaysConfirmAutomationOperations`) and the caller didn't pass `confirm=true` → return
   `RequiresConfirmation`, prompting a second, explicit tool call.
4. Otherwise → **Allowed**.

Every mutating tool returns the resulting `SafetyDecision` (`Verdict` + human-readable
`Reason`) directly in its JSON result — both the agent and a human reviewing the transcript
can see *why* something was allowed, denied, or paused for confirmation. Nothing fails silently.

## `dryRun` and `confirm`

Every mutating tool accepts both:

- **`dryRun=true`** — evaluate the safety decision and report it without applying anything.
  Useful for an agent (or operator) to preview "what would happen" before committing.
- **`confirm=true`** — required wherever the policy demands it (state transitions by default,
  named automation operations, and any future high-impact tool). Passing it when it isn't
  required is harmless; omitting it where it's required returns `RequiresConfirmation` rather
  than acting — an unmissable two-step "I intend to do X" → "yes, do X" flow visible in the
  agent's own tool-call transcript.

## Audit log

Every `SafetyGate` check — allowed, denied, *or* pending confirmation — is appended as one
JSON object per line to `AuditLogPath` (`TimestampUtc`, `Operation`, `Subject`, `Verdict`,
`Reason`, optional `Details`). This is an independent forensic record of everything an agent
*attempted*, separate from whatever the LLM conversation log retains, and survives even if
the operation itself was blocked. Failures to write the log are logged but never thrown —
an audit-trail outage must not be able to take the server down or silently bypass the gate
it observes.

## Recommended rollout

1. **Start with `SafeMode=true`** (the default) and explore read-only: list/read PLC source,
   read live symbols, read build errors and hardware configs. Confirm the audit log is being
   written and the tool surface looks right.
2. **Enable writes narrowly**, scoped to a non-production fixture or sandbox project first:
   add a few entries to `WritableSymbolPatterns`/`WritableSourcePathPatterns`/
   `AllowedStateTransitions` that match only what you intend to let the agent touch, then
   flip `SafeMode=false`.
3. **Leave `AlwaysConfirmStateTransitions=true`** and the default
   `AlwaysConfirmAutomationOperations` list alone unless you have a specific, reviewed reason
   to remove an operation from it — these are the operations most likely to interrupt a
   running physical system.
4. **Tail the audit log** (`tail -f <AuditLogPath>`) while running an agent session against
   anything other than a sandbox, so a denied or confirmation-pending attempt is visible to
   a human in real time, not just in retrospect.
