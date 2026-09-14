#requires -Version 7
# =============================================================================================
# !! OBSOLETE / DO NOT RUN - kept only as a record of the previous approach !!
#
# This script belongs to the earlier "localize the cfg keys" pass, which rewrote the
# section names, key names and descriptions INSIDE Settings.cs into Chinese. That approach
# was reverted on purpose: a localized key name is not a stable identifier, so every
# language switch orphaned BepInEx\config\com.fiodor.pipdisabler.cfg and threw away the
# player's tuning.
#
# The display layer is now external and language-agnostic:
#     i18n\en.json / i18n\zh-CN.json   <- the text
#     I18n.cs                          <- the loader
#     i18n\README.md                   <- how to add a language
#     Settings-汉化对照表.md            <- the design write-up
#
# Running this script again would re-break that design (Settings.cs gets Chinese section
# and key names back) and invalidate the cfg of every existing user.
# =============================================================================================
<#
  PiP-Disabler-Full : migrate an existing BepInEx config file to the Chinese section/key names.

  BepInEx identifies a setting by "<section>|<key>". After the F12-menu localization those names are
  Chinese, so an already-tuned English .cfg would be orphaned and every value would fall back to its
  default on the next launch. This script renames sections/keys (and refreshes the "## description"
  comment block) IN PLACE while keeping every value byte-for-byte.

  Order of operations when deploying:
      1. replace PiP-Disabler.dll
      2. run this script on BepInEx\config\com.fiodor.pipdisabler.cfg
      3. only then launch the game

  Usage:
      pwsh -File Tools\migrate_cfg_names.ps1 -Cfg 'D:\game\EFT-SPT 4.1.5\BepInEx\config\com.fiodor.pipdisabler.cfg'
      pwsh -File Tools\migrate_cfg_names.ps1 -Cfg <file> -WhatIf     # dry run, nothing is written
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Cfg,
    [string]$MapFile,
    [string]$BackupSuffix = '.bak_zh',
    [switch]$WhatIf,
    [switch]$KeepEnglishDescription
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $MapFile) { $MapFile = Join-Path $PSScriptRoot 'settings_zh_map.json' }
if (-not (Test-Path $Cfg)) { throw "config file not found: $Cfg" }

$map = Get-Content $MapFile -Raw -Encoding utf8 | ConvertFrom-Json
$sections = @{}
foreach ($p in $map.sections.PSObject.Properties) { $sections[$p.Name] = $p.Value }
$entries = @{}
foreach ($e in $map.entries) { $entries["$($e.sec)|$($e.key)"] = $e }

# --- encoding: preserve an existing BOM, otherwise write UTF-8 without BOM ----
$bytes = [System.IO.File]::ReadAllBytes($Cfg)
$hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
$enc = New-Object System.Text.UTF8Encoding($hasBom)
$lines = [System.IO.File]::ReadAllLines($Cfg, [System.Text.Encoding]::UTF8)

if (-not $WhatIf) {
    $backup = $Cfg + $BackupSuffix
    if (Test-Path $backup) {
        $backup = $Cfg + $BackupSuffix + '_' + (Get-Date -Format 'yyyyMMdd_HHmmss')
    }
    Copy-Item $Cfg $backup
    Write-Host "backup: $backup"
}

$out = New-Object System.Collections.Generic.List[string]
$secEn = ''
$renamedKeys = 0
$renamedSections = @{}
$descStart = -1          # index in $out where the current '## ' description block starts
$pendingDesc = $false    # a description block was seen and belongs to the next key line
$unmapped = New-Object System.Collections.Generic.List[string]
$newEntries = [ordered]@{}

for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]

    if ($line -match '^\[(.+)\]$') {
        $raw = $Matches[1]
        $secEn = $raw
        $descStart = -1; $pendingDesc = $false
        if ($sections.ContainsKey($raw)) {
            $renamedSections[$raw] = $sections[$raw]
            $out.Add('[' + $sections[$raw] + ']')
        } else {
            $out.Add($line)
        }
        continue
    }

    if ($line.StartsWith('## ') -or $line -eq '##') {
        if ($descStart -lt 0) { $descStart = $out.Count }
        $out.Add($line)
        continue
    }

    if ($line.StartsWith('# Setting type:')) {
        $pendingDesc = $true
        $out.Add($line)
        continue
    }

    if ($line -match '^([^#\s][^=]*?) = (.*)$') {
        $key = $Matches[1].TrimEnd()
        $value = $Matches[2]
        $id = "$secEn|$key"
        if ($entries.ContainsKey($id)) {
            $e = $entries[$id]
            if ($pendingDesc -and $descStart -ge 0 -and -not $KeepEnglishDescription) {
                $blockEnd = $out.Count
                while ($blockEnd -gt $descStart -and -not $out[$blockEnd - 1].StartsWith('##')) { $blockEnd-- }
                $out.RemoveRange($descStart, $blockEnd - $descStart)
                $ins = New-Object System.Collections.Generic.List[string]
                foreach ($p in @($e.desc)) { $ins.Add('## ' + [string]$p) }
                $out.InsertRange($descStart, $ins)
            }
            $out.Add($e.newKey + ' = ' + $value)
            $newEntries["$($sections[$secEn])|$($e.newKey)"] = $value
            $renamedKeys++
        } else {
            $unmapped.Add("[$secEn] $key")
            $out.Add($line)
            $newEntries["$secEn|$key"] = $value
        }
        $descStart = -1; $pendingDesc = $false
        continue
    }

    if ($line -eq '') { $descStart = -1; $pendingDesc = $false }
    $out.Add($line)
}

# --- verify: same entry count, values preserved, keys all localized -----------
$oldPairs = [ordered]@{}
$curSec = ''
foreach ($l in $lines) {
    if ($l -match '^\[(.+)\]$') { $curSec = $Matches[1]; continue }
    if ($l -match '^([^#\s][^=]*?) = (.*)$') {
        $oldPairs["$curSec|$($Matches[1].TrimEnd())"] = $Matches[2]
    }
}
if ($oldPairs.Count -ne $newEntries.Count) {
    throw "entry count changed: $($oldPairs.Count) -> $($newEntries.Count)"
}
foreach ($k in $oldPairs.Keys) {
    if (-not $entries.ContainsKey($k)) { continue }   # unmapped entry, copied verbatim
    $e = $entries[$k]
    $nk = "$($sections[$e.sec])|$($e.newKey)"
    if (-not $newEntries.Contains($nk)) { throw "migrated entry missing: $nk" }
    if ($newEntries[$nk] -ne $oldPairs[$k]) { throw "value changed for $nk : '$($oldPairs[$k])' -> '$($newEntries[$nk])'" }
}

Write-Host ("sections renamed : {0}" -f $renamedSections.Count)
Write-Host ("entries  renamed : {0} / {1}" -f $renamedKeys, $oldPairs.Count)
if ($unmapped.Count -gt 0) {
    Write-Host ("NOT mapped (left as-is): {0}" -f $unmapped.Count)
    $unmapped | ForEach-Object { Write-Host "   $_" }
}

if ($WhatIf) {
    Write-Host 'WhatIf: nothing written. First 25 lines of the result:'
    $out | Select-Object -First 25 | ForEach-Object { Write-Host $_ }
    return
}

[System.IO.File]::WriteAllLines($Cfg, $out, $enc)
Write-Host "migrated in place: $Cfg"
