# Engineering automation tools

These tools extend the existing XAE connection/session and run COM work on its STA
thread. They work with either a server-created IDE or an attached interactive IDE.
They use late binding plus minimal raw COM interface declarations, so building does
not require TwinCAT interop assemblies. The APIs were cross-checked against
[TcAutomation](https://github.com/Beckhoff-USA-Community/TcAutomation) and Beckhoff's
[Automation Interface documentation](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/index.html).
TcAutomation is not added as a dependency or vendored into this repository.

## Tool reference

| Area | Tools |
|---|---|
| Solutions and TwinCAT projects | `create_xae_solution`, `add_twincat_project`, `list_twincat_projects`, `select_twincat_project` |
| Project configuration | `get_xae_tree_xml`, `get_xae_target_net_id`, `set_xae_target_net_id`, `set_xae_target_platform` |
| EtherCAT | `create_ethercat_master` (also TE1111 simulation devices), `create_ethercat_box`, `get_device_adapter`, `set_device_adapter`, `export_ethercat_eni`, `import_ethercat_eni` |
| Variable mappings | `get_mapping_info`, `apply_mapping_info`, `link_xae_variables`, `unlink_xae_variables`, `generate_xae_mappings`, `check_xae_variable_links` |
| Real-time tasks and CPU settings | `create_realtime_task`, `add_task_image_variable`, `get_xae_task_settings`, `configure_xae_task`, `configure_xae_realtime_cores` |
| PLC projects and libraries | `add_plc_project`, `add_plc_library`, `add_plc_placeholder`, `remove_plc_reference`, `set_plc_library_parameter` |
| PLC sources and tasks | `import_plc_source_files`, `add_plc_task`, `check_plc_objects`, `set_plc_boot_project` |

The existing source editing, PLC object CRUD/PLCopen import/export, build/clean,
save, configuration activation and restart tools remain available.

## Results, dry runs and persistence

Every mutation is audited with its operation and target. New mutations return:

- `Applied`, `Succeeded`: true after successful execution.
- `DryRun`: true for policy-only previews, which never invoke COM or touch files.
- `SafetyReason`: the audit/policy decision.
- `Value`: the resulting path, project/item information, or method result.
- `Error`: the error on failure.
- `MayHaveApplied`: true if execution failed after entering the operation; COM
  operations are not transactional, so inspect the IDE before retrying.

An allowed dry run returns `Succeeded=true`, `Applied=false`. It checks policy only;
it does not validate arguments, paths, installed components or IDE availability.
A blocked call returns `Succeeded=false`, `Applied=false` without invoking COM.
Cancellation propagates as cancellation; a COM call already executing cannot be
interrupted safely and may have changed the project.

`check_plc_objects` returns the compiler's boolean verdict in `Value`;
`Succeeded` reports whether the call completed. `check_xae_variable_links` reports
command completion, not a validation verdict. Use `get_build_errors` for diagnostics.

Configuration edits normally affect the IDE's in-memory project. Call `save_project`
to persist and `build_project` to compile. Solution creation saves the new .sln;
ENI exports write their output file immediately. Existing solution/export files and
nonempty new-project directories are refused rather than overwritten.

`set_plc_boot_project` requires `AllowDownloads` because it invokes
`GenerateBootProject(true)` on the selected target. Build first. Other new tools
are audit-only and issue no configuration activation, restart, live core-isolation
command or ADS write. No general-purpose COM/IDE-command or ConsumeXml execution
endpoint is exposed.

## Typical workflows

### Work on an existing solution

1. Use `list_xae_shell_instances` and `attach_xae_shell`, or `open_xae_project`.
2. Call `list_twincat_projects`, then `select_twincat_project` if necessary.
3. Use the configuration tools, then `save_project` and `build_project`.

Selection applies to all system-manager operations, including existing tree and
activation tools. Source tools continue to index the whole solution folder.
The selected project resets when the solution changes or the session disconnects.
The XAE target Net ID and the MCP ADS target are independent.

### Create a project

1. Start disconnected; call `create_xae_solution` with a new absolute .sln path.
2. Call `add_twincat_project` with a project name and the full path to an installed
   `TwinCAT Project.tsproj` template. The new project becomes selected.
3. Call `add_plc_project` with an installed template name, normally
   `Standard PLC Template` or `Empty PLC Template`.
4. Import `.TcPOU`, `.TcGVL`, `.TcDUT` files using `import_plc_source_files`,
   or use the existing PLC object creation/editing tools.
5. Save and build.

### Configure EtherCAT and mappings

1. Create an EtherCAT master, then couplers/terminals using installed ESI product
   identifiers, e.g. EK1100 and EL1008. No physical scan is performed.
2. Configure the adapter GUID and 12-digit hexadecimal MAC using `set_device_adapter`.
3. Configure tasks; save and build the PLC so process-image symbols exist.
4. Link exact variable tree paths, or apply a `VarLinks` XML document.
5. Generate/check mappings if supported by the installed shell, then save/build.

ENI import targets TE1111 simulation devices and requires that integration to be
installed. Mapping-generation commands can be unavailable in some XAE versions;
those errors are returned. Commands select the chosen project in Solution Explorer
before executing, and fail if selection is unavailable instead of acting on an
unrelated focused project.

### Configure tasks

Create a real-time task with or without process image. `configure_xae_task` accepts
positive `cycleTimeUs`, priority 1..255 and zero-based `coreId` 0..63. Omitted
settings are preserved. Task XML uses 100 ns units; module-context cycle times use
nanoseconds. `configure_xae_realtime_cores` updates project CPU counts and per-core
base times; it never applies isolation directly to the running target.

For a referenced PLC task, create the real-time task first with `withImage=false`,
then call `add_plc_task` with the PLC project, task and program names.

## Verification

Run automated tests without the existing hardware-affecting E2E tests:

```powershell
dotnet test TwinCatMcp.slnx --filter 'FullyQualifiedName!~XaeShellAutomationE2eTests'
```

After building Debug, verify actual stdio tool discovery and dry-run/policy paths:

```powershell
python tests/configuration_mcp_smoke.py
```

Add `--live` for read-only checks against the first accessible running shell.
Add `--template 'C:/path/to/TwinCAT Project.tsproj'` to create a separate scratch
solution and test configuration mutations. Scratch outputs/logs remain under
`obj/mcp-smoke/`. The script performs no activation, boot-project generation,
runtime state change or ADS write. Run with the same Windows user/elevation as XAE.
