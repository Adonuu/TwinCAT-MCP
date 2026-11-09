[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TypeLibDirectory = ([IO.Path]::Combine([Environment]::GetFolderPath('ProgramFilesX86'), 'Beckhoff', 'TwinCAT', '3.1', 'Components', 'Base', 'TypeLib')),
    [ValidateSet('2.1','3.1','3.2','3.3','3.4')][string[]]$Versions = @('3.3','3.4'),
    [string]$BackupPath = (Join-Path $env:LOCALAPPDATA 'TwinCatMcp\TypeLibRepair\backup.json'),
    [switch]$Undo
)
$ErrorActionPreference = 'Stop'
$libraryId = '{3C49D6C3-93DC-11D0-B162-00A0248C244B}'
$registryRoot = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry32)
try {
    if ($Undo) {
        $backup = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
        foreach ($entry in $backup.Entries) {
            if (-not $entry.Created) { continue }
            if ($entry.Key -notmatch ('^Software\\Classes\\TypeLib\\' + [regex]::Escape($libraryId) + '\\(2\.1|3\.[1-4])$')) { throw 'Unexpected rollback key' }
            $key = $registryRoot.OpenSubKey($entry.Key)
            if (-not $key) { continue }
            try {
                $platform = $key.OpenSubKey('0\win32')
                try { if (-not $platform -or $platform.GetValue('') -ne $entry.File) { throw 'Registration changed since repair; no rollback performed' } }
                finally { if ($platform) { $platform.Dispose() } }
                $expected = @{
                    '' = 'TCatSysManagerLib'; '0' = $null; '0\win32' = $entry.File;
                    'FLAGS' = '0'; 'HELPDIR' = (Split-Path -Parent $entry.File)
                }
                $queue = New-Object 'System.Collections.Generic.Queue[string]'
                $queue.Enqueue('')
                while ($queue.Count) {
                    $relative = $queue.Dequeue()
                    if (-not $expected.ContainsKey($relative)) { throw 'Unexpected subkey; no rollback performed' }
                    $check = if ($relative) { $key.OpenSubKey($relative) } else { $key }
                    try {
                        $names = @($check.GetValueNames())
                        if ($null -eq $expected[$relative]) {
                            if ($names.Count) { throw 'Unexpected registry values; no rollback performed' }
                        } elseif ($names.Count -ne 1 -or $names[0] -ne '' -or $check.GetValue('') -ne $expected[$relative]) {
                            throw 'Registration changed since repair; no rollback performed'
                        }
                        foreach ($child in $check.GetSubKeyNames()) {
                            $queue.Enqueue($(if ($relative) { $relative+'\'+$child } else { $child }))
                        }
                    } finally { if ($relative) { $check.Dispose() } }
                }
            } finally { $key.Dispose() }
            if ($PSCmdlet.ShouldProcess('HKCU\'+$entry.Key,'Remove only the new per-user type library registration')) { $registryRoot.DeleteSubKeyTree($entry.Key) }
        }
        Write-Output 'Per-user repair rollback completed.'
        return
    }
    if (-not ('BeckhoffTypeLibValidation' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using TYPELIBATTR = System.Runtime.InteropServices.ComTypes.TYPELIBATTR;
public static class BeckhoffTypeLibValidation {
 [DllImport("oleaut32.dll",CharSet=CharSet.Unicode,PreserveSig=false)]
 static extern void LoadTypeLibEx(string file, int registrationKind, out ITypeLib library);
 public static void Validate(string file, short major, short minor) {
  ITypeLib library; LoadTypeLibEx(file,2,out library); // REGKIND_NONE: read-only
  try { IntPtr pointer; library.GetLibAttr(out pointer);
   TYPELIBATTR attr;
   try { attr=(TYPELIBATTR)Marshal.PtrToStructure(pointer,typeof(TYPELIBATTR)); }
   finally { library.ReleaseTLibAttr(pointer); }
   if(attr.guid!=new Guid("3c49d6c3-93dc-11d0-b162-00a0248c244b") || attr.wMajorVerNum!=major || attr.wMinorVerNum!=minor)
    throw new InvalidOperationException("Unexpected Beckhoff typelib identity or version");
   Guid declaration=new Guid("e937e819-2185-420d-91c0-3d8136fcc517"); ITypeInfo info;
   library.GetTypeInfoOfGuid(ref declaration,out info); Marshal.ReleaseComObject(info);
  } finally { Marshal.ReleaseComObject(library); }
 }
}
'@
    }
    $entries = @()
    foreach ($version in $Versions) {
        $file = [IO.Path]::GetFullPath((Join-Path $TypeLibDirectory "$version\TCatSysManager.tlb"))
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Installed vendor typelib missing: $file" }
        [BeckhoffTypeLibValidation]::Validate($file,[System.Int16]$version.Split('.')[0],[System.Int16]$version.Split('.')[1])
        $keyPath = "Software\Classes\TypeLib\$libraryId\$version"
        $existing = $registryRoot.OpenSubKey($keyPath)
        if ($existing) {
            try {
                $registered = $existing.OpenSubKey('0\win32')
                try { $registeredPath = if ($registered) { [string]$registered.GetValue('') } else { '' } }
                finally { if ($registered) { $registered.Dispose() } }
                if ([string]::Equals($registeredPath,$file,[StringComparison]::OrdinalIgnoreCase)) {
                    Write-Output "Library $version is already registered for this user at the validated path; leaving it unchanged."
                    continue
                }
                throw "A different per-user registration already exists at $keyPath. Refusing to overwrite it."
            } finally { $existing.Dispose() }
        }
        $entries += [pscustomobject]@{ Key=$keyPath; File=$file; Created=$false }
        Write-Output "Validated installed Beckhoff library $version; planned HKCU-only registration: $keyPath"
    }
    if (-not $WhatIfPreference -and (Test-Path -LiteralPath $backupPath)) { throw "Backup already exists; inspect $backupPath before repeating this repair." }
    $backup = [pscustomobject]@{ Date=(Get-Date -Format o); RegistryView='Registry32'; Entries=$entries }
    foreach ($entry in $entries) {
        if ($PSCmdlet.ShouldProcess('HKCU\'+$entry.Key,'Register existing Beckhoff type information for the current user only')) {
            # Write a recovery record before the first mutation. The version keys did not exist.
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($backupPath))) | Out-Null
            $entry.Created = $true
            $backup | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $backupPath -Encoding utf8
            $key = $registryRoot.CreateSubKey($entry.Key)
            try {
                $key.SetValue('','TCatSysManagerLib',[Microsoft.Win32.RegistryValueKind]::String)
                foreach ($sub in @(@('0\win32',$entry.File),@('FLAGS','0'),@('HELPDIR',(Split-Path -Parent $entry.File)))) {
                    $subKey = $key.CreateSubKey($sub[0])
                    try { $subKey.SetValue('',$sub[1],[Microsoft.Win32.RegistryValueKind]::String) }
                    finally { $subKey.Dispose() }
                }
            } finally { $key.Dispose() }
        }
    }
    Write-Output 'Done. No HKLM, CLSID, Interface, TwinCAT runtime or PLC project settings were changed.'
} finally { $registryRoot.Dispose() }
