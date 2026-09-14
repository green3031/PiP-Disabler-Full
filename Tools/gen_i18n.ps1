<#
    gen_i18n.ps1 - generates i18n\en.json and i18n\zh-CN.json for PiP-Disabler.

    Sources (read-only, never modified):
      Settings.cs.en.bak                  -> English section/key/description text (the pre-zh source)
      Tools\settings_zh_map.json          -> Chinese translations produced in the earlier
                                             "localize the cfg keys" pass (sec/key/newKey/desc)

    Output:
      i18n\en.json
      i18n\zh-CN.json

    Run from anywhere:
      pwsh -File 'D:\Work area\212\PiP-Disabler-Full\Tools\gen_i18n.ps1'

    The script fails loudly if the two sources do not line up 1:1, so a re-run after
    adding a config entry either regenerates cleanly or tells you exactly what is missing.
#>
[CmdletBinding()]
param(
    [string]$ProjectDir = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$enSrc   = Join-Path $ProjectDir 'Settings.cs.en.bak'
$zhMap   = Join-Path $ProjectDir 'Tools\settings_zh_map.json'
$i18nDir = Join-Path $ProjectDir 'i18n'

foreach ($p in @($enSrc, $zhMap)) {
    if (-not (Test-Path $p)) { throw "missing source file: $p" }
}
New-Item -ItemType Directory -Force -Path $i18nDir | Out-Null

# ---------------------------------------------------------------------------
# 1. Pull (section, key, english description) out of the C# source, in Bind order.
# ---------------------------------------------------------------------------
$src = Get-Content $enSrc -Raw -Encoding UTF8

$bindRx = [regex]'config\.Bind\(\s*"([^"]*)"\s*,\s*"([^"]*)"'
$descRx = [regex]'new ConfigDescription\(\s*((?:"(?:[^"\\]|\\.)*"\s*(?:\+\s*)?)+?)\s*,'
$litRx  = [regex]'"(?:[^"\\]|\\.)*"'

function ConvertFrom-CsLiteral([string]$literal) {
    # $literal includes the surrounding quotes; handles \n \r \t \" \\
    $body = $literal.Substring(1, $literal.Length - 2)
    $sb = New-Object System.Text.StringBuilder
    $i = 0
    while ($i -lt $body.Length) {
        $ch = $body[$i]
        if ($ch -eq '\' -and ($i + 1) -lt $body.Length) {
            $n = $body[$i + 1]
            switch ($n) {
                'n'  { [void]$sb.Append("`n"); $i += 2; continue }
                'r'  { [void]$sb.Append("`r"); $i += 2; continue }
                't'  { [void]$sb.Append("`t"); $i += 2; continue }
                '"'  { [void]$sb.Append('"');  $i += 2; continue }
                '\'  { [void]$sb.Append('\');  $i += 2; continue }
                '0'  { [void]$sb.Append([char]0); $i += 2; continue }
                default { [void]$sb.Append($ch); $i += 1; continue }
            }
        }
        [void]$sb.Append($ch)
        $i += 1
    }
    return $sb.ToString()
}

$binds = $bindRx.Matches($src)
if ($binds.Count -eq 0) { throw "no config.Bind( calls found in $enSrc" }

$enRows = New-Object System.Collections.Generic.List[object]
for ($i = 0; $i -lt $binds.Count; $i++) {
    $sec = $binds[$i].Groups[1].Value
    $key = $binds[$i].Groups[2].Value
    $tail = $src.Substring($binds[$i].Index)
    $dm = $descRx.Match($tail)
    if (-not $dm.Success) { throw "could not find ConfigDescription for [$sec] $key" }
    $sb = New-Object System.Text.StringBuilder
    foreach ($lit in $litRx.Matches($dm.Groups[1].Value)) {
        [void]$sb.Append((ConvertFrom-CsLiteral $lit.Value))
    }
    $enRows.Add([pscustomobject]@{ Section = $sec; Key = $key; Description = $sb.ToString() })
}

# ---------------------------------------------------------------------------
# 2. Read the Chinese map and check it lines up with the C# source row by row.
# ---------------------------------------------------------------------------
$map = Get-Content $zhMap -Raw -Encoding UTF8 | ConvertFrom-Json
if ($map.entries.Count -ne $enRows.Count) {
    throw "row count mismatch: Settings.cs.en.bak has $($enRows.Count) config.Bind calls, settings_zh_map.json has $($map.entries.Count) entries"
}
for ($i = 0; $i -lt $enRows.Count; $i++) {
    $e = $map.entries[$i]
    if ($e.sec -ne $enRows[$i].Section -or $e.key -ne $enRows[$i].Key) {
        throw "row $i mismatch: source=[$($enRows[$i].Section)][$($enRows[$i].Key)] map=[$($e.sec)][$($e.key)]"
    }
}

$sectionOrder = New-Object System.Collections.Generic.List[string]
foreach ($r in $enRows) { if (-not $sectionOrder.Contains($r.Section)) { $sectionOrder.Add($r.Section) } }

# ---------------------------------------------------------------------------
# 3. The Language entry added by the i18n pass (it is not in the .en.bak source).
# ---------------------------------------------------------------------------
$langEntryEn = [pscustomobject]@{
    Section  = 'General'
    Key      = 'Language'
    Name     = 'Language'
    Description = "Display language of this mod's settings.`n" +
                  "Auto = follow the game's in-game language setting (recommended).`n" +
                  "You can also type a language code directly, e.g. en, zh-CN, ru.`n" +
                  "English = en, " + [char]0x4E2D + [char]0x6587 + " = zh-CN.`n" +
                  "Language files live in BepInEx\plugins\PiP-Disabler\i18n\ - see i18n\README.md to add your own."
}
$langEntryZh = [pscustomobject]@{
    Section  = 'General'
    Key      = 'Language'
    Name     = '语言'
    Description = "本模组设置界面的显示语言。`n" +
                  "Auto = 跟随游戏内的语言设置（推荐）。`n" +
                  "也可以直接填语言代码，例如 en、zh-CN、ru。`n" +
                  "English = en，中文 = zh-CN。`n" +
                  "语言文件位于 BepInEx\plugins\PiP-Disabler\i18n\，新增语言的方法见 i18n\README.md。"
}

# ---------------------------------------------------------------------------
# 4. Build the two documents.
# ---------------------------------------------------------------------------
function New-LanguageDocument {
    param(
        [string]$LanguageName,
        [string]$LanguageCode,
        [hashtable]$SectionMap,       # english section -> localized section
        [object[]]$Rows               # @{Section;Key;Name;Description} in Bind order
    )

    $sections = [ordered]@{}
    foreach ($s in $sectionOrder) {
        $localized = $s
        if ($SectionMap.ContainsKey($s)) { $localized = $SectionMap[$s] }
        $sections[$s] = $localized
    }

    $settings = [ordered]@{}
    foreach ($s in $sectionOrder) { $settings[$s] = [ordered]@{} }
    foreach ($r in $Rows) {
        $settings[$r.Section][$r.Key] = [ordered]@{
            name        = $r.Name
            description = $r.Description
        }
    }

    $doc = [ordered]@{
        '_readme'       = "PiP-Disabler display-language file. Only the text on the right-hand side of ':' needs translating - never change the keys on the left, and never rename this file's 'sections'/'settings' keys. Step-by-step guide: i18n/README.md"
        '_language'     = $LanguageName
        '_languageCode' = $LanguageCode
        'sections'      = $sections
        'settings'      = $settings
    }
    return $doc
}

$enMap = @{}   # english -> english (identity)
$zhSectionMap = @{}
foreach ($p in $map.sections.PSObject.Properties) { $zhSectionMap[$p.Name] = $p.Value }

$enRowsFull = New-Object System.Collections.Generic.List[object]
$enRowsFull.Add([pscustomobject]@{ Section='General'; Key='Language'; Name=$langEntryEn.Name; Description=$langEntryEn.Description })
foreach ($r in $enRows) { $enRowsFull.Add([pscustomobject]@{ Section=$r.Section; Key=$r.Key; Name=$r.Key; Description=$r.Description }) }
# keep section order = source order, but Language must be first inside General
$enOrdered = New-Object System.Collections.Generic.List[object]
$enOrdered.Add($enRowsFull[0])
for ($i = 1; $i -lt $enRowsFull.Count; $i++) { $enOrdered.Add($enRowsFull[$i]) }

$zhRowsFull = New-Object System.Collections.Generic.List[object]
$zhRowsFull.Add([pscustomobject]@{ Section='General'; Key='Language'; Name=$langEntryZh.Name; Description=$langEntryZh.Description })
for ($i = 0; $i -lt $map.entries.Count; $i++) {
    $e = $map.entries[$i]
    $zhRowsFull.Add([pscustomobject]@{
        Section     = $e.sec
        Key         = $e.key
        Name        = $e.newKey
        Description = ($e.desc -join "`n")
    })
}

$enDoc = New-LanguageDocument -LanguageName 'English' -LanguageCode 'en' -SectionMap $enMap -Rows $enOrdered
$zhDoc = New-LanguageDocument -LanguageName '中文（简体）' -LanguageCode 'zh-CN' -SectionMap $zhSectionMap -Rows $zhRowsFull

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$enPath = Join-Path $i18nDir 'en.json'
$zhPath = Join-Path $i18nDir 'zh-CN.json'
[System.IO.File]::WriteAllText($enPath, ($enDoc | ConvertTo-Json -Depth 10), $utf8NoBom)
[System.IO.File]::WriteAllText($zhPath, ($zhDoc | ConvertTo-Json -Depth 10), $utf8NoBom)

# ---------------------------------------------------------------------------
# 5. Verify by reading the files back and comparing against the sources.
# ---------------------------------------------------------------------------
$backEn = Get-Content $enPath -Raw -Encoding UTF8 | ConvertFrom-Json
$backZh = Get-Content $zhPath -Raw -Encoding UTF8 | ConvertFrom-Json

$errors = New-Object System.Collections.Generic.List[string]
$enCount = 0; $zhCount = 0
foreach ($r in $enOrdered) {
    $node = $backEn.settings.($r.Section).($r.Key)
    if ($null -eq $node) { $errors.Add("en.json missing [$($r.Section)] $($r.Key)"); continue }
    if ($node.name -ne $r.Name) { $errors.Add("en.json name mismatch [$($r.Section)] $($r.Key)") }
    if ($node.description -ne $r.Description) { $errors.Add("en.json description mismatch [$($r.Section)] $($r.Key)") }
    $enCount++
}
foreach ($r in $zhRowsFull) {
    $node = $backZh.settings.($r.Section).($r.Key)
    if ($null -eq $node) { $errors.Add("zh-CN.json missing [$($r.Section)] $($r.Key)"); continue }
    if ($node.name -ne $r.Name) { $errors.Add("zh-CN.json name mismatch [$($r.Section)] $($r.Key)") }
    if ($node.description -ne $r.Description) { $errors.Add("zh-CN.json description mismatch [$($r.Section)] $($r.Key)") }
    $zhCount++
}

foreach ($s in $sectionOrder) {
    if ($null -eq $backEn.sections.$s) { $errors.Add("en.json sections missing '$s'") }
    if ($null -eq $backZh.sections.$s) { $errors.Add("zh-CN.json sections missing '$s'") }
}

Write-Host "sections           : $($sectionOrder.Count)  ($($sectionOrder -join ', '))"
Write-Host "en.json entries    : $enCount"
Write-Host "zh-CN.json entries : $zhCount"
Write-Host "en.json  bytes     : $((Get-Item $enPath).Length)  -> $enPath"
Write-Host "zh-CN.json bytes   : $((Get-Item $zhPath).Length)  -> $zhPath"

if ($errors.Count -gt 0) {
    Write-Host ""
    Write-Host "VERIFY FAILED:" -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "verify: OK (every section and every entry round-trips byte for byte)" -ForegroundColor Green
