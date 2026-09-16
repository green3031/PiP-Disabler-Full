#requires -Version 7
<#
  verify_defaults_unchanged.ps1 - "the defaults did not move", as a measurement.

  1. Reads every config.Bind(section, key, defaultValue, ...) call out of the BUILT
     PiP-Disabler.dll (Mono.Cecil, read-only) by using the C# left-to-right argument
     order: the instruction right after the key literal IS the default value.
  2. Compares every key against "com.fiodor.pipdisabler.cfg" as the PREVIOUS
     version wrote it (its "# Default value:" lines are the previous compiled defaults),
     so "the defaults did not move" is a measurement, not a promise.
     That cfg is an archived copy kept next to this project's build notes; point -Cfg at
     any other copy written by an earlier build if that one is gone.

  Depends on D:\game\EFT-SPT 4.1.5\BepInEx\core\Mono.Cecil.dll (ConfigurationManager's copy).

  Usage:
      pwsh -File 'D:\Work area\212\PiP-Disabler-Full\Tools\verify_defaults_unchanged.ps1'
      exit 0 = every pre-existing default is unchanged
#>
[CmdletBinding()]
param(
    [string]$ProjectDir = (Split-Path -Parent $PSScriptRoot),
    [string]$Cfg = 'D:\Work area\212\_logs\20260914_224928\com.fiodor.pipdisabler.cfg'
)

$ErrorActionPreference = 'Stop'

$game = 'D:\game\EFT-SPT 4.1.5'
$core = Join-Path $game 'BepInEx\core'
$dll = Join-Path $ProjectDir 'PiP-Disabler.dll'
$cfg = $Cfg
if (-not (Test-Path $cfg)) { throw "baseline cfg not found: $cfg (pass -Cfg <path to a cfg written by the previous build>)" }

Add-Type -Path (Join-Path $core 'Mono.Cecil.dll')
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.ReadingMode = [Mono.Cecil.ReadingMode]::Deferred
$rp.InMemory = $true
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll, $rp)

function All-Types($types) { foreach ($t in $types) { $t; if ($t.HasNestedTypes) { All-Types $t.NestedTypes } } }
function Find-Type([string]$fullName) {
    foreach ($t in (All-Types $asm.MainModule.Types)) { if ($t.FullName -eq $fullName) { return $t } }
    return $null
}

$settingsType = Find-Type 'PiPDisabler.Settings'
$init = $null
foreach ($m in $settingsType.Methods) { if ($m.Name -eq 'Init') { $init = $m } }
if ($init -eq $null) { throw 'Settings::Init not found' }

$sections = @(
    'General', 'Hacks', 'Graphics', 'Global Mesh Surgery settings', 'Debug',
    'Per scope settings', 'Scope Effects'
)

# ---- 1. pull (section, key, default literal) out of the IL -----------------------------
$rows = New-Object System.Collections.Generic.List[object]
$ins = @($init.Body.Instructions)
for ($i = 0; $i -lt $ins.Count - 2; $i++) {
    if ($ins[$i].OpCode.Name -ne 'ldstr') { continue }
    $sec = [string]$ins[$i].Operand
    if ($sections -notcontains $sec) { continue }
    if ($ins[$i + 1].OpCode.Name -ne 'ldstr') { continue }
    $key = [string]$ins[$i + 1].Operand
    $d = $ins[$i + 2]
    $lit = $null
    if ($d.OpCode.Name -match '^ldc\.i4\.(\d+)$') { $lit = $Matches[1] }
    elseif ($d.OpCode.Name -eq 'ldc.i4.s') { $lit = [string][int]$d.Operand }
    elseif ($d.OpCode.Name -eq 'ldc.i4') { $lit = [string][int]$d.Operand }
    elseif ($d.OpCode.Name -eq 'ldc.r4') { $lit = ([single]$d.Operand).ToString('R', [System.Globalization.CultureInfo]::InvariantCulture) }
    elseif ($d.OpCode.Name -eq 'ldc.r8') { $lit = ([double]$d.Operand).ToString('R', [System.Globalization.CultureInfo]::InvariantCulture) }
    elseif ($d.OpCode.Name -eq 'ldstr') { $lit = [string]$d.Operand }
    elseif ($d.OpCode.Name -eq 'ldnull') { $lit = '' }
    else { $lit = '<' + $d.OpCode.Name + '>' }
    $rows.Add([pscustomobject]@{ Section = $sec; Key = $key; Default = $lit; OpCode = $d.OpCode.Name })
}

Write-Host ("=== config.Bind rows recovered from the built DLL: {0} ===" -f $rows.Count)

# ---- 2. previous version's defaults, from the cfg it wrote -----------------------------
$cfgDefaults = @{}
$cfgSection = ''
$pending = $null
$pendingType = $null
foreach ($line in [System.IO.File]::ReadAllLines($cfg, [System.Text.Encoding]::UTF8)) {
    $t = $line.Trim()
    if ($t -match '^\[(.+)\]$') { $cfgSection = $Matches[1]; continue }
    if ($t -match '^#\s*Default value:\s*(.*)$') { $pending = $Matches[1].Trim(); continue }
    if ($t -match '^#\s*Setting type:\s*(.+)$') { $pendingType = $Matches[1].Trim(); continue }
    if ($t.StartsWith('#') -or $t.Length -eq 0) { continue }
    $eq = $line.IndexOf(' = ')
    if ($eq -lt 0) { continue }
    $k = $line.Substring(0, $eq).Trim()
    $v = $line.Substring($eq + 3).Trim()
    if ($pending -ne $null) {
        $cfgDefaults["$cfgSection|$k"] = [pscustomobject]@{ Value = $pending; Type = $pendingType }
        $pending = $null; $pendingType = $null
    }
}
Write-Host ("=== defaults recorded by the PREVIOUS build (cfg): {0} ===" -f $cfgDefaults.Count)

# The comparison is done on a canonical form, because the cfg stores "true"/"false"/"None"
# while the IL stores 1/0 for the same values.
function Get-Canonical([string]$value, [string]$type, [string]$fromIl) {
    $v = $value.Trim()
    if ($type -eq 'KeyCode' -or $v -eq 'None') {
        if ($v -eq 'None') { return 'enum:0' }
        $ki = 0
        if ([int]::TryParse($v, [ref]$ki)) { return 'enum:' + $ki }
        return 'enum:' + $v
    }
    $isBool = (($type -eq 'Boolean') -or ($v -match '^(true|false)$'))
    if ($isBool) {
        if ($v -match '^(1|true|True)$') { return 'bool:true' }
        if ($v -match '^(0|false|False)$') { return 'bool:false' }
    }
    $d = 0.0
    if ([double]::TryParse($v, [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) {
        return 'num:' + $d.ToString('R', [System.Globalization.CultureInfo]::InvariantCulture)
    }
    return 'str:' + $v
}

$checked = 0; $mismatch = 0; $missing = 0
foreach ($r in $rows) {
    $id = "$($r.Section)|$($r.Key)"
    if (-not $cfgDefaults.ContainsKey($id)) {
        # The i18n pass added [General] Language AFTER the baseline cfg was written, so there is
        # no recorded default for it - assert it against the source constant instead (I18n.AutoValue).
        if ($id -eq 'General|Language') {
            $checked++
            if ($r.Default -ne 'Auto') { $mismatch++; Write-Host ("  [FAIL] {0}: artifact={1} expected=Auto" -f $id, $r.Default) }
            continue
        }
        $missing++; Write-Host "  [--] no baseline in the previous cfg: $id"; continue
    }
    $checked++
    $prev = $cfgDefaults[$id]
    $a = Get-Canonical $r.Default $prev.Type $true
    $b = Get-Canonical $prev.Value $prev.Type $false
    if ($a -ne $b) {
        $mismatch++
        Write-Host ("  [FAIL] {0}: artifact={1} ({2})  previous={3} ({4})" -f $id, $r.Default, $a, $prev.Value, $b)
    }
}
Write-Host ''
Write-Host ("=== pre-existing defaults: {0} compared, {1} mismatched, {2} without a baseline ===" -f $checked, $mismatch, $missing)

Write-Host ''
if ($mismatch -gt 0) { Write-Host "=== RESULT: $mismatch mismatch(es) ==="; exit 1 }
Write-Host ("=== RESULT: every pre-existing default is unchanged ({0} compared, {1} without any baseline) ===" -f $checked, $missing)
exit 0
