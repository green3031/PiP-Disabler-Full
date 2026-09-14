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
  Standalone checker (lives OUTSIDE the project on purpose):
  proves that the zh-CN rewrite of Settings.cs changed ONLY the three strings of every config.Bind
  call - section name, key name, and the ConfigDescription text - and nothing else.

  Skeleton comparison:
    * description literal lines are dropped from both revisions
    * on a Bind line, section+key literals are dropped, everything from the default value onward
      is compared byte-for-byte
    * every other line must be byte-identical
#>
$ErrorActionPreference = 'Stop'
$dir = 'D:\Work area\212\PiP-Disabler-Full'
$old = [System.IO.File]::ReadAllLines((Join-Path $dir 'Settings.cs.en.bak'), [System.Text.Encoding]::UTF8)
$new = [System.IO.File]::ReadAllLines((Join-Path $dir 'Settings.cs'), [System.Text.Encoding]::UTF8)

function Get-Skeleton([string[]]$lines, [ref]$defaults) {
    $sk = New-Object System.Collections.Generic.List[string]
    $i = 0
    while ($i -lt $lines.Count) {
        $line = $lines[$i]
        $m = [regex]::Match($line, '^(?<indent>\s*)ConfigEntries\.Add\((?<field>[A-Za-z_][A-Za-z0-9_]*) = config\.Bind\("[^"]+", "[^"]+",\s*(?<rest>.*)$')
        if ($m.Success) {
            $sk.Add($m.Groups['indent'].Value + 'BIND ' + $m.Groups['field'].Value + ' | ' + $m.Groups['rest'].Value.TrimEnd())
            $defaults.Value.Add($m.Groups['field'].Value + ' = ' + $m.Groups['rest'].Value.TrimEnd())
            $i++
            # skip the 'new ConfigDescription(' line + the description literal chain
            if ($i -lt $lines.Count -and $lines[$i] -match 'new ConfigDescription\($') { $i++ } else { throw "unexpected at $i : $($lines[$i])" }
            while ($i -lt $lines.Count -and $lines[$i].TrimStart().StartsWith('"')) { $i++ }
            continue
        }
        $sk.Add($line)
        $i++
    }
    return $sk
}

$dOld = New-Object System.Collections.Generic.List[string]
$dNew = New-Object System.Collections.Generic.List[string]
$sOld = Get-Skeleton $old ([ref]$dOld)
$sNew = Get-Skeleton $new ([ref]$dNew)

if ($sOld.Count -ne $sNew.Count) { throw "skeleton length differs: $($sOld.Count) vs $($sNew.Count)" }
$diff = 0
for ($i = 0; $i -lt $sOld.Count; $i++) {
    if ($sOld[$i] -cne $sNew[$i]) {
        $diff++
        if ($diff -le 20) { Write-Host "DIFF line $($i+1):"; Write-Host "  old: $($sOld[$i])"; Write-Host "  new: $($sNew[$i])" }
    }
}
Write-Host "skeleton lines compared: $($sOld.Count); differences: $diff"

if ($dOld.Count -ne $dNew.Count) { throw "bind count differs: $($dOld.Count) vs $($dNew.Count)" }
$dd = 0
for ($i = 0; $i -lt $dOld.Count; $i++) {
    if ($dOld[$i] -cne $dNew[$i]) { $dd++; Write-Host "DEFAULT DIFF: $($dOld[$i])  ->  $($dNew[$i])" }
}
Write-Host "config.Bind entries: $($dOld.Count); default-value differences: $dd"

# every default value byte-identical => compare as an ordered set of raw literals too
$rawOld = ($dOld -join "`n"); $rawNew = ($dNew -join "`n")
Write-Host ("defaults byte-identical: {0}" -f ($rawOld -ceq $rawNew))

# section / key survey of the new file
$secs = [ordered]@{}
foreach ($l in $new) {
    $m = [regex]::Match($l, 'config\.Bind\("(?<sec>[^"]+)", "(?<key>[^"]+)"')
    if ($m.Success) {
        $s = $m.Groups['sec'].Value
        if (-not $secs.Contains($s)) { $secs[$s] = New-Object System.Collections.Generic.List[string] }
        $secs[$s].Add($m.Groups['key'].Value)
    }
}
Write-Host "`n--- new sections / keys ---"
foreach ($k in $secs.Keys) {
    $dupes = ($secs[$k] | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    Write-Host ("[{0}]  {1} keys{2}" -f $k, $secs[$k].Count, $(if ($dupes) { "  !! DUPLICATE KEYS: " + ($dupes -join ', ') } else { '' }))
    $secs[$k] | ForEach-Object { Write-Host "    $_" }
}
