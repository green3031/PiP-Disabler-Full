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
  PiP-Disabler-Full : apply the Chinese (zh-CN) localization to Settings.cs.

  Reads  Tools/settings_zh_map.json  (sec/key = upstream English literals -> newKey/desc = Chinese)
  and rewrites ONLY the three string literals of every config.Bind(...) call:
      config.Bind("<section>", "<key>", <default>, new ConfigDescription("<desc>", ...))

  Guarantees:
    * field name / type / default value / AcceptableValueRange / ConfigurationManagerAttributes
      are copied through byte-for-byte (only the description block and the two name literals change)
    * Settings.cs is backed up to Settings.cs.en.bak once (never overwritten afterwards)
    * output is UTF-8 without BOM (same as the rest of the project)
    * fails loudly if any Bind entry has no mapping, or any mapping is unused

  Also regenerates Settings-汉化对照表.md (sections / keys / descriptions).

  Usage:  pwsh -File Tools\apply_settings_zh.ps1
#>
$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$src    = Join-Path $root 'Settings.cs'
$bak    = Join-Path $root 'Settings.cs.en.bak'
$mapFile= Join-Path $PSScriptRoot 'settings_zh_map.json'
$md     = Join-Path $root 'Settings-汉化对照表.md'

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# --- backup (once) -----------------------------------------------------------
if (-not (Test-Path $bak)) {
    Copy-Item $src $bak
    Write-Host "backup written: $bak"
} else {
    Write-Host "backup already present, kept: $bak"
}

# --- load map ----------------------------------------------------------------
$map = Get-Content $mapFile -Raw -Encoding utf8 | ConvertFrom-Json
$sections = @{}
foreach ($p in $map.sections.PSObject.Properties) { $sections[$p.Name] = $p.Value }
$entries = [ordered]@{}
foreach ($e in $map.entries) {
    $id = "$($e.sec)|$($e.key)"
    if ($entries.Contains($id)) { throw "duplicate mapping for $id" }
    $entries[$id] = $e
}
Write-Host "map: $($sections.Count) sections, $($entries.Count) entries"

function ConvertFrom-CsLiteralChain([string[]]$lines) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($l in $lines) {
        $t = $l.Trim()
        $t = $t -replace ',$', ''
        $t = $t -replace '\+$', ''
        $t = $t.Trim()
        $t = $t -replace '^"', ''
        $t = $t -replace '"$', ''
        [void]$sb.Append($t)
    }
    return $sb.ToString().Replace('\n', '<br>').Replace('|', '\|')
}

# --- rewrite -----------------------------------------------------------------
$lines = [System.IO.File]::ReadAllLines($src, [System.Text.Encoding]::UTF8)
$out = New-Object System.Collections.Generic.List[string]
$used = @{}
$applied = 0
$rows = New-Object System.Collections.Generic.List[object]
$i = 0
while ($i -lt $lines.Count) {
    $line = $lines[$i]
    $m = [regex]::Match($line, '^(?<indent>\s*)ConfigEntries\.Add\((?<field>[A-Za-z_][A-Za-z0-9_]*) = config\.Bind\("(?<sec>[^"]+)", "(?<key>[^"]+)"')
    if (-not $m.Success) { $out.Add($line); $i++; continue }

    $sec   = $m.Groups['sec'].Value
    $key   = $m.Groups['key'].Value
    $field = $m.Groups['field'].Value
    $id    = "$sec|$key"
    if (-not $sections.ContainsKey($sec)) { throw "no section translation for '$sec' ($field)" }
    if (-not $entries.Contains($id))      { throw "no entry translation for '$id' ($field) - upstream drift?" }
    $e = $entries[$id]
    if ($used.ContainsKey($id))           { throw "mapping '$id' used twice" }
    $used[$id] = $true

    $out.Add($line.Replace(
        'config.Bind("' + $sec + '", "' + $key + '"',
        'config.Bind("' + $sections[$sec] + '", "' + $e.newKey + '"'))
    $i++

    if ($i -ge $lines.Count -or $lines[$i] -notmatch 'new ConfigDescription\($') {
        throw "expected 'new ConfigDescription(' after the Bind call of $field"
    }
    $descIndentLine = $lines[$i]
    $out.Add($descIndentLine)
    $i++

    $old = New-Object System.Collections.Generic.List[string]
    while ($i -lt $lines.Count -and $lines[$i].TrimStart().StartsWith('"')) { $old.Add($lines[$i]); $i++ }
    if ($old.Count -eq 0) { throw "no description literal found for $field" }
    # keep the original description indentation (one step deeper than 'new ConfigDescription(')
    $descIndent = ($old[0] -replace '^(\s*).*$', '$1')

    $oldText = ConvertFrom-CsLiteralChain $old
    $paras = @($e.desc)
    for ($j = 0; $j -lt $paras.Count; $j++) {
        $text = ([string]$paras[$j]).Replace('\', '\\').Replace('"', '\"')
        if ($j -lt $paras.Count - 1) { $out.Add($descIndent + '"' + $text + '\n" +') }
        else                         { $out.Add($descIndent + '"' + $text + '",') }
    }

    $rows.Add([pscustomobject]@{
        Sec      = $sections[$sec]
        SecEn    = $sec
        KeyEn    = $key
        KeyZh    = $e.newKey
        Field    = $field
        DescEn   = $oldText
        DescZh   = (ConvertFrom-CsLiteralChain @($paras | ForEach-Object { '"' + $_ + '"' }))
    })
    $applied++
}

if ($applied -ne $entries.Count) { throw "applied $applied but the map has $($entries.Count) entries" }
foreach ($k in $entries.Keys) { if (-not $used.ContainsKey($k)) { throw "unused mapping: $k" } }

[System.IO.File]::WriteAllLines($src, $out, $utf8NoBom)
Write-Host "rewrote $src : $applied Bind entries localized, $($out.Count) lines"

# --- comparison table --------------------------------------------------------
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('# PiP-Disabler-Full — BepInEx F12 配置菜单汉化对照表')
[void]$sb.AppendLine()
[void]$sb.AppendLine('> 源文件：`Settings.cs`（插件 `com.fiodor.pipdisabler`，`[BepInPlugin]` 版本 1.5.0）')
[void]$sb.AppendLine('> 机器可读映射：`Tools/settings_zh_map.json`（同一份数据驱动 `Tools/apply_settings_zh.ps1`，上游更新后可复用）')
[void]$sb.AppendLine('> 生成方式：只替换每处 `config.Bind()` 的段名 / 键名 / `ConfigDescription` 说明三个字符串字面量；')
[void]$sb.AppendLine('> 字段名、类型、默认值、`AcceptableValueRange`、`ConfigurationManagerAttributes` 参数一律未改动。')
[void]$sb.AppendLine()
[void]$sb.AppendLine("条目总数：**$applied**（与源文件 `config.Bind` 调用数一致）")
[void]$sb.AppendLine()

[void]$sb.AppendLine('## 1. 段名对照')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| 原文段名 | 中文段名 | 条目数 |')
[void]$sb.AppendLine('|---|---|---|')
foreach ($p in $map.sections.PSObject.Properties) {
    $n = ($rows | Where-Object { $_.SecEn -eq $p.Name }).Count
    [void]$sb.AppendLine("| ``$($p.Name)`` | **$($p.Value)** | $n |")
}
[void]$sb.AppendLine()

[void]$sb.AppendLine('## 2. 键名对照')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| 中文段名 | 原文键名 | 中文键名 | C# 字段（未改动） |')
[void]$sb.AppendLine('|---|---|---|---|')
foreach ($r in $rows) {
    [void]$sb.AppendLine("| $($r.Sec) | ``$($r.KeyEn)`` | **$($r.KeyZh)** | ``$($r.Field)`` |")
}
[void]$sb.AppendLine()

[void]$sb.AppendLine('## 3. 说明文字对照')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| 中文段名 | C# 字段 | 原文说明 | 中文说明 |')
[void]$sb.AppendLine('|---|---|---|---|')
foreach ($r in $rows) {
    [void]$sb.AppendLine("| $($r.Sec) | ``$($r.Field)`` | $($r.DescEn) | $($r.DescZh) |")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 4. 术语约定（全文一致）')
[void]$sb.AppendLine()
[void]$sb.AppendLine('Scope / optic → 瞄具；Lens → 镜片；Housing → 镜身；Reticle → 准星；Vignette → 暗角；Blur → 模糊；')
[void]$sb.AppendLine('Cut / Mesh surgery → 切割 / 网格手术；Bypass → 绕过；Collimator → 全息；Magnification / zoom → 放大倍率 / 变焦；')
[void]$sb.AppendLine('Mag → 倍率；Magnified optics → 放大倍镜；Weapon scale → 武器缩放；Recoil → 后坐力；Sway → 晃动；')
[void]$sb.AppendLine('LOD bias → LOD 偏移；Stencil → 模板；Mask → 遮罩；Multiplier → 系数；Gate → 门控。')
[void]$sb.AppendLine()
[void]$sb.AppendLine('保持英文原文的技术专有名（未翻译）：`FOV`、`LOD`、`ADS`、`NVG`、`JSON`、`TAA`、`Kawase`、`Plane1/2/3/4`、`PlaneOffsetMeters` 语义、')
[void]$sb.AppendLine('`linza` / `backLens` / `boreAxis` / `nearPlane` / `Weapon_root`、`renderer`、`stencil`、`ribcage`、')
[void]$sb.AppendLine('`custom_mesh_surgery_settings.json`、`R_geo` / `R_manual`、`after-NVG`、单位为 `米`/`cm`/`px`/`度`/`°` 的数值。')
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 5. ⚠ 旧 .cfg 键名迁移（重要）')
[void]$sb.AppendLine()
[void]$sb.AppendLine('键名与段名都是 BepInEx `com.fiodor.pipdisabler.cfg` 的条目标识。改名后旧键名成为孤儿键，')
[void]$sb.AppendLine('**用户已调好的数值会在下次生成配置时回落到默认值**。迁移脚本：')
[void]$sb.AppendLine()
[void]$sb.AppendLine('```powershell')
[void]$sb.AppendLine("pwsh -File 'Tools\migrate_cfg_names.ps1' -Cfg 'D:\game\EFT-SPT 4.1.5\BepInEx\config\com.fiodor.pipdisabler.cfg'")
[void]$sb.AppendLine('```')
[void]$sb.AppendLine()
[void]$sb.AppendLine('顺序：**先替换 DLL → 再迁移 cfg → 最后启动游戏**（游戏一旦启动就会用新键名重写 cfg，届时旧值已被孤立）。')
[void]$sb.AppendLine('迁移脚本会先自行备份 `.cfg.bak_zh`。')
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 6. 保留英文的条目 / 需说明之处')
[void]$sb.AppendLine()
[void]$sb.AppendLine('**没有任何段名、键名或说明被"不便汉化"而跳过** —— 101 个条目的三个字符串全部汉化。')
[void]$sb.AppendLine('以下三类按其性质保留英文原文，或需单独说明：')
[void]$sb.AppendLine()
[void]$sb.AppendLine('1. **技术专有名（保留英文，未硬译）**：`FOV`、`LOD`、`ADS`、`NVG`、`TAA`、`JSON`、`Kawase`（含 `Dual Kawase`）、')
[void]$sb.AppendLine('   `Plane1/2/3/4`、`backLens` / `linza` / `boreAxis` / `nearPlane`、`Weapon_root`、`renderer`、`stencil`、`ribcage`、')
[void]$sb.AppendLine('   `after-NVG`、`R_geo` / `R_manual`、`pass`、`max(...)` / `min(...)` 表达式、文件名 `custom_mesh_surgery_settings.json`。')
[void]$sb.AppendLine('2. **与外部数据耦合、必须保持英文的标识符**（本次**未改动**，因为它们是 C# 字段名 / JSON 键 / 游戏对象名，不是菜单文字）：')
[void]$sb.AppendLine('   `Settings.Custom*` 系列字段、`ScopeMeshSurgerySettingsEntry` 的 JSON 属性名（`Plane1Radius` 等，写在')
[void]$sb.AppendLine('   `custom_mesh_surgery_settings.json` 里）、瞄具 key（如 `scope_all_eotech_hhs_1`）、`Weapon_root`、`mod_scope`、`linza`。')
[void]$sb.AppendLine('3. **`(?)` 标记**：本工程全部 `.cs` / `.json` / `.md` 中 **0 处** `(?)`，故无标记需要保留（该标记出现在其它版本的源码里）。')
[void]$sb.AppendLine()
[void]$sb.AppendLine('### 上游文档不一致（按原文直译，未擅自修正）')
[void]$sb.AppendLine()
[void]$sb.AppendLine('- `NearPreserveDepth`（`全局网格手术` 段）说明写 `0.01 = 1cm ring (default)`，但该键**实际默认值是 0.02549295**（≈2.5cm 圆环）。')
[void]$sb.AppendLine('  译文保持「0.01 = 1cm 圆环（默认）」，事实上的默认值以 `Settings.cs` 里的第三个参数为准。')
[void]$sb.AppendLine('- `Auto LOD bias multiplier` 原文说明只有一句 "Self explanatory"（顾名思义），译文照译，未扩写。')
[void]$sb.AppendLine()
[void]$sb.AppendLine('### 可读性补充（译文中新增的少量括号说明）')
[void]$sb.AppendLine()
[void]$sb.AppendLine('- 「绕过」类条目补了 `（走原版渲染）`：说明绕过后该瞄具回到游戏原生 PiP 行为，便于判断开关影响。')
[void]$sb.AppendLine('- 译文里交叉引用的其它条目一律使用**汉化后的键名**（如「切割长度(米)」「调试日志」「手动武器缩放」「镜内效果」），')
[void]$sb.AppendLine('  以便在 F12 里按图索骥；原文中的英文交叉引用（`CutLength`、`Debug logging`、`Scope Effects` 等）因此不再逐字出现。')
[void]$sb.AppendLine()

[System.IO.File]::WriteAllText($md, ($sb.ToString() -replace "`r`n", "`n"), $utf8NoBom)
Write-Host "wrote $md"
