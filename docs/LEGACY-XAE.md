# Legacy 32-bit XAE: source access and build configuration

A successful DTE/ROT attachment does not guarantee that all TwinCAT COM
interfaces are available. In a TwinCAT 3.1.4024.78 / 32-bit XAE installation,
`get_project_tree` and source tools exposed two separate problems:

- IEC objects reported their kind in `ItemType` (e.g. GVL = 615), while
  `ItemSubType` was zero. Treating the latter as the PLC kind returned `Unknown`.
- The PLC declaration and implementation IIDs were registered only in the
  32-bit registry view, and their referenced `TCatSysManagerLib` registrations
  were missing. `IDispatch.GetTypeInfo` failed with `TYPE_E_LIBNOTREGISTERED`
  (`0x8002801D`); a declaration-interface cast also failed for valid GVLs/POUs.

## Choose the engineering server architecture

Use the same Windows user and elevation as XAE. For a legacy installation with
only 32-bit PLC interface registration, build the engineering MCP server as x86:

```powershell
dotnet publish src/TwinCatMcp.Server/TwinCatMcp.Server.csproj `
  -c Release -r win-x86 --self-contained true -o artifacts/win-x86
```

Point the MCP client's command at that output's `TwinCatMcp.Server.exe` and
reload the server. The engineering server architecture is separate from the
PLC's target platform: a 32-bit MCP process can build a CE7 ARMV7 PLC project.
Use `win-x64` for installations whose COM interfaces are registered for x64.

## Restore missing type-library references only when diagnosed

First prefer the Beckhoff installation/repair tools. If the installed interface
entries point to a missing `TCatSysManagerLib` type-library registration, the
optional script below restores references to existing, validated vendor `.tlb`
files for the current user. It never runs automatically in the MCP server.

```powershell
# Preview; select only the installed versions needed by your COM registration.
.\scripts\Repair-BeckhoffTypeLib.ps1 -Versions 3.3,3.4 -WhatIf
# Apply after reviewing the preview.
.\scripts\Repair-BeckhoffTypeLib.ps1 -Versions 3.3,3.4
```

The script validates the library GUID/version and declaration IID before any
mutation. It creates only missing HKCU Registry32 `TypeLib` version keys for
`{3C49D6C3-93DC-11D0-B162-00A0248C244B}`. It preserves HKLM, CLSID and Interface
registrations, skips an existing reference to the same file, and refuses to
overwrite a different reference. It does not fix missing Interface registrations.
`-TypeLibDirectory` supports a non-default TwinCAT installation path.

A recovery record is saved under `%LOCALAPPDATA%\TwinCatMcp\TypeLibRepair`.
`-BackupPath` selects a different record location. Preview rollback with:

```powershell
.\scripts\Repair-BeckhoffTypeLib.ps1 -Undo -WhatIf
```

Rollback refuses changed/unexpected registry contents. As a shared engineering
dependency, the repaired references may also be used by other TwinCAT clients;
review rollback before applying it. The script supports Windows PowerShell 5.1
and PowerShell 7; its COM struct name and integer casts are explicit for both.

## Select an exact build configuration

`build_project` and `clean_project` accept `Configuration|Platform`, for example
`Release|TwinCAT CE7 (ARMV7)`. EnvDTE stores these in separate `Name` and
`PlatformName` properties. The server matches both before activating the
configuration. A configuration name alone is accepted only when unambiguous;
an unavailable/ambiguous request fails without switching platforms.

## Verification

The corresponding fixes were exercised in an existing 32-bit XAE Shell with
TwinCAT 3.1.4024.78. MCP read, edited and read back temporary GVL declarations
(BOOL/INT and Unicode comments), POU declarations and ST implementations.
Dry-run edits preserved source, GVL implementation edits were rejected, saved
files contained the changes, and source restoration was checked. Temporary
objects were removed and the original source and 15 I/O links were unchanged.
An explicit `Release|TwinCAT CE7 (ARMV7)` build finished with zero errors and
zero warnings. These were offline engineering/interface tests; no ADS writes,
activation, downloads, runtime state changes or hardware testing took place.
