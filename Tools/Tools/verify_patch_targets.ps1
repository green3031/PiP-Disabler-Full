#requires -Version 7
<#
  PiP-Disabler-Full : Harmony patch target existence check.
  Reads the game's Assembly-CSharp.dll metadata with Mono.Cecil (read-only, Deferred mode)
  and verifies every runtime-resolved target the mod references.

  Usage:  pwsh -File pip_full_verify.ps1
#>
$ErrorActionPreference = 'Stop'

$game    = 'D:\game\EFT-SPT 4.1.5'
$managed = Join-Path $game 'EscapeFromTarset_Data\Managed'
$managed = Join-Path $game 'EscapeFromTarkov_Data\Managed'
$core    = Join-Path $game 'BepInEx\core'

Add-Type -Path (Join-Path $core 'Mono.Cecil.dll')

function Load-Asm([string]$path) {
    $rp = New-Object Mono.Cecil.ReaderParameters
    $rp.ReadingMode = [Mono.Cecil.ReadingMode]::Deferred
    $rp.InMemory = $true
    return [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path, $rp)
}

$acs = Load-Asm (Join-Path $managed 'Assembly-CSharp.dll')
$core0 = Load-Asm (Join-Path $managed 'UnityEngine.CoreModule.dll')

function Get-TypeDef($asm, [string]$fullName) {
    $t = $asm.MainModule.GetType($fullName)
    return $t
}

function Format-Method([Mono.Cecil.MethodDefinition]$m) {
    $ps = @()
    foreach ($p in $m.Parameters) { $ps += ($p.ParameterType.FullName) }
    $ret = $m.ReturnType.FullName
    return "$($m.Name)($([string]::Join(', ', $ps))) -> $ret"
}

# Each entry: Type | Member | Kind(Method|Field|Property|Any) | ParamSignature(human readable, '' = any)
$targets = @(
  # --- core PiP suppression ------------------------------------------------
  @('EFT.CameraControl.OpticCameraManager', 'OnOpticSightEnabled', 'Method', 'EFT.CameraControl.OpticSight'),
  @('EFT.CameraControl.OpticCameraManager', 'SetResolution', 'Method', 'System.Int32'),
  @('EFT.CameraControl.CameraManager', 'method_10', 'Method', ''),
  @('EFT.CameraControl.OpticCameraManager', '_renderTexture', 'Field', ''),
  @('EFT.CameraControl.OpticCameraManager', '_updater', 'Field', ''),
  @('EFT.CameraControl.OpticCameraManager', '_camTexId', 'Field', ''),
  @('EFT.CameraControl.OpticCameraManager', 'OpticFinalResolution', 'Field', ''),
  @('EFT.CameraControl.OpticCameraManager', 'Camera', 'Property', ''),
  @('EFT.CameraControl.OpticCameraManager', 'CurrentOpticSight', 'Property', ''),
  @('EFT.CameraControl.OpticCameraManager', 'OpticRetrice', 'Property', ''),
  @('EFT.CameraControl.OpticCameraManager', 'Updater', 'Property', ''),
  @('EFT.CameraControl.CameraManager', 'Instance', 'Property', ''),
  @('EFT.CameraControl.CameraManager', 'Exist', 'Property', ''),
  @('EFT.CameraControl.CameraManager', 'OpticCameraManager', 'Property', ''),
  @('EFT.CameraControl.CameraManager', 'SetFov', 'Method', 'System.Single, System.Single, System.Boolean'),
  @('EFT.CameraControl.CameraManager', 'Fov', 'Property', ''),
  @('EFT.CameraControl.CameraManager', '_cameraLodBiasController', 'Field', ''),

  # --- OpticSight / updater / lens ----------------------------------------
  @('EFT.CameraControl.OpticSight', 'OnEnable', 'Method', ''),
  @('EFT.CameraControl.OpticSight', 'OnDisable', 'Method', ''),
  @('EFT.CameraControl.OpticSight', 'LensFade', 'Method', 'System.Boolean'),
  @('EFT.CameraControl.OpticSight', 'CameraData', 'Field', ''),
  @('EFT.CameraControl.OpticSight', 'ScopeData', 'Field', ''),
  @('EFT.CameraControl.OpticSight', 'LensRenderer', 'Field', ''),
  @('EFT.CameraControl.OpticComponentUpdater', 'CopyComponentFromOptic', 'Method', 'EFT.CameraControl.OpticSight'),
  @('EFT.CameraControl.OpticComponentUpdater', 'LateUpdate', 'Method', ''),
  @('UnityEngine.Camera', 'Render', 'Method', ''),

  # --- scope / aim hooks ---------------------------------------------------
  @('EFT.Player/FirearmController', 'ChangeAimingMode', 'Method', ''),
  @('EFT.Player/FirearmController', 'SetScopeMode', 'Method', 'EFT.ScopeState[]'),
  @('EFT.Player/FirearmController', 'SetInventoryOpened', 'Method', 'System.Boolean'),
  @('EFT.Player', 'EFT.ISetInHandsHandler.OnSetInHands', 'Method', 'EFT.InventoryLogic.SetInHandsEventArgs'),
  @('EFT.Player', 'HasBodyPartCollider', 'Method', 'UnityEngine.Collider'),
  @('EFT.Player', 'Look', 'Method', 'System.Single, System.Single, System.Boolean'),
  @('EFT.Player', 'SetCompensationScale', 'Method', 'System.Boolean'),
  @('EFT.Player', 'InventoryController', 'Property', ''),
  @('EFT.Player', 'ProceduralWeaponAnimation', 'Property', ''),
  @('EFT.Player', 'IsYourPlayer', 'Property', ''),
  @('EFT.Player', 'RibcageScaleCurrentTarget', 'Field', ''),
  @('EFT.Player', 'RibcageScaleCurrent', 'Field', ''),

  # --- PWA / FOV -----------------------------------------------------------
  @('EFT.Animations.ProceduralWeaponAnimation', 'OnAimOrPoseChanged', 'Method', ''),
  @('EFT.Animations.ProceduralWeaponAnimation', 'LateTransformations', 'Method', ''),
  @('EFT.Animations.ProceduralWeaponAnimation', 'HeadBobbing', 'Property', ''),
  @('EFT.Animations.ProceduralWeaponAnimation', '_tacticalReload', 'Field', ''),
  @('EFT.Animations.ProceduralWeaponAnimation', 'CurrentScope', 'Property', ''),
  @('EFT.Animations.ProceduralWeaponAnimation', 'IsAiming', 'Property', ''),
  @('EFT.Animations.ProceduralWeaponAnimation', 'Sprint', 'Property', ''),
  @('EFT.Animations.NewRotationRecoilProcess', 'CalculateAfterRecoilWeaponOffset', 'Method', ''),
  @('EFT.Animations.NewRotationRecoilProcess', '_afterRecoilDefaultPosition', 'Field', ''),

  # --- springs / sway ------------------------------------------------------
  @('EFT.Animations.Spring', 'AddAcceleration', 'Method', 'UnityEngine.Vector3'),
  @('EFT.Animations.Spring', 'AddAcceleration', 'Method', 'System.Int32, System.Single'),
  @('BetterSpring', 'ApplyVelocity', 'Method', 'UnityEngine.Vector3'),
  @('BetterSpring', 'ApplyVelocity', 'Method', 'System.Int32, System.Single'),

  # --- weapons / LOD / GPU -------------------------------------------------
  @('FirearmsAnimator', 'SetFireMode', 'Method', 'EFT.InventoryLogic.Weapon/EFireMode, System.Boolean'),
  @('FirearmsAnimator', 'ModToggleTrigger', 'Method', ''),
  @('ObjectInHandsAnimator', '_eventsConsumers', 'Field', ''),
  @('EFT.CameraControl.CameraLodBiasController', 'SetBiasByFov', 'Method', 'System.Single'),

  # --- range finder --------------------------------------------------------
  @('EFT.TacticalRangeFinderController', 'OnEnable', 'Method', ''),
  @('EFT.TacticalRangeFinderController', 'MeasureDistance', 'Method', ''),
  @('EFT.TacticalRangeFinderController', '_distanceOutputFormat', 'Field', ''),
  @('EFT.TacticalRangeFinderController', '_noDistanceText', 'Field', ''),
  @('EFT.TacticalRangeFinderController', '_textOnDisplay', 'Field', ''),
  @('EFT.TacticalRangeFinderController', '_boneToCastRay', 'Field', ''),
  @('EFT.TacticalRangeFinderController', '_rayStartOffset', 'Field', ''),
  @('EFT.TacticalRangeFinderController', '_maxCastDistance', 'Field', ''),
  @('EFT.TacticalRangeFinderController', '_mask', 'Field', ''),

  # --- misc helpers --------------------------------------------------------
  @('EFT.StringExtensions', 'SetMonospaceText', 'Method', ''),
  @('EFT.InventoryLogic.SightComponent', 'GetCurrentOpticZoom', 'Method', ''),
  @('EFT.InventoryLogic.SightComponent', '_template', 'Field', ''),
  @('ScopePrefabCache', 'CurrentModOpticSight', 'Property', ''),
  @('EFT.InventoryLogic.AddItemEventArgs', 'To', 'Property', ''),
  @('EFT.InventoryLogic.RemoveItemEventArgs', 'From', 'Property', ''),
  @('EFT.InventoryLogic.ItemEventArgs', 'Item', 'Field', ''),
  @('EFT.InventoryLogic.ItemEventArgs', 'Status', 'Field', '')
)

$results = New-Object System.Collections.Generic.List[object]

foreach ($t in $targets) {
    $typeName = $t[0]; $member = $t[1]; $kind = $t[2]; $sig = $t[3]
    $asm = $acs
    if ($typeName -like 'UnityEngine.*') { $asm = $core0 }

    $td = Get-TypeDef $asm $typeName
    if ($td -eq $null) {
        $results.Add([pscustomobject]@{ Type=$typeName; Member=$member; Kind=$kind; Status='TYPE-MISSING'; Detail='' })
        continue
    }

    $found = @()
    if ($kind -eq 'Method' -or $kind -eq 'Any') {
        foreach ($m in $td.Methods) { if ($m.Name -eq $member) { $found += (Format-Method $m) } }
    }
    if ($kind -eq 'Field' -or $kind -eq 'Any') {
        foreach ($f in $td.Fields) { if ($f.Name -eq $member) { $found += ("[field] " + $f.FieldType.FullName) } }
    }
    if ($kind -eq 'Property' -or $kind -eq 'Any') {
        foreach ($p in $td.Properties) { if ($p.Name -eq $member) { $found += ("[prop] " + $p.PropertyType.FullName) } }
    }

    if ($found.Count -eq 0) {
        $results.Add([pscustomobject]@{ Type=$typeName; Member=$member; Kind=$kind; Status='MISSING'; Detail='' })
    } else {
        $st = 'EXISTS'
        if ($kind -eq 'Method' -and $sig -ne '') {
            $hit = $false
            foreach ($f in $found) { if ($f.StartsWith("$member($sig)")) { $hit = $true } }
            if (-not $hit) { $st = 'SIGNATURE-MISMATCH' }
        }
        $results.Add([pscustomobject]@{ Type=$typeName; Member=$member; Kind=$kind; Status=$st; Detail=([string]::Join(' | ', $found)) })
    }
}

# ---------- transpiler dependency checks (call sites inside patched methods) ----------
$callChecks = @(
  @('EFT.Player', 'Look', 'EFT.CameraControl.CameraManager', 'SetFov'),
  @('EFT.Animations.ProceduralWeaponAnimation', 'OnAimOrPoseChanged', 'EFT.CameraControl.CameraManager', 'SetFov'),
  @('GPUInstancer.GPUInstancerManager', 'Update', 'GPUInstancer.GPUInstancerManager', 'get_bGenerateMotionVectors'),
  @('EFT.CameraControl.OpticComponentUpdater', 'LateUpdate', 'UnityEngine.Camera', 'Render')
)

$callResults = New-Object System.Collections.Generic.List[object]
foreach ($c in $callChecks) {
    $declType = $c[0]; $declMethod = $c[1]; $calleeType = $c[2]; $callee = $c[3]
    $asm = $acs
    if ($declType -like 'UnityEngine.*') { $asm = $core0 }
    $td = Get-TypeDef $asm $declType
    if ($td -eq $null) { $callResults.Add([pscustomobject]@{ Where="$declType::$declMethod"; Expects="$calleeType::$callee"; Found='TYPE-MISSING'; OpCode='' }); continue }
    $md = $null
    foreach ($m in $td.Methods) { if ($m.Name -eq $declMethod) { $md = $m; break } }
    if ($md -eq $null -or -not $md.HasBody) { $callResults.Add([pscustomobject]@{ Where="$declType::$declMethod"; Expects="$calleeType::$callee"; Found='METHOD-MISSING'; OpCode='' }); continue }
    $ops = @()
    foreach ($ins in $md.Body.Instructions) {
        if ($ins.OpCode.Name -like 'call*') {
            $op = $ins.Operand
            if ($op -ne $null -and $op.DeclaringType -ne $null) {
                $dt = $op.DeclaringType.FullName
                if ($dt -eq $calleeType -and $op.Name -eq $callee) { $ops += $ins.OpCode.Name }
            }
        }
    }
    $callResults.Add([pscustomobject]@{ Where="$declType::$declMethod"; Expects="$calleeType::$callee"; Found=$(if($ops.Count){'FOUND'}else{'NOT-FOUND'}); OpCode=([string]::Join(',', $ops)) })
}

# ---------- report ----------
$ok = ($results | Where-Object { $_.Status -eq 'EXISTS' }).Count
$bad = ($results | Where-Object { $_.Status -ne 'EXISTS' }).Count
Write-Host "=== Harmony target check: $ok EXISTS / $bad PROBLEM (of $($results.Count)) ==="
$results | Where-Object { $_.Status -ne 'EXISTS' } | Format-Table -AutoSize
Write-Host ""
Write-Host "=== Transpiler call-site check ==="
$callResults | Format-Table -AutoSize
Write-Host ""
Write-Host "=== Full list ==="
$results | Format-Table -AutoSize

# machine readable dump
$dump = @()
foreach ($r in $results) { $dump += "| ``$($r.Type)::$($r.Member)`` | $($r.Kind) | $($r.Status) | $($r.Detail) |" }
$dump | Set-Content -Encoding utf8 (Join-Path $PSScriptRoot '_verify_targets.md')
Write-Host "wrote _verify_targets.md ($($dump.Count) rows)"

$opDump = @()
foreach ($r in $callResults) { $opDump += "| ``$($r.Where)`` | ``$($r.Expects)`` | $($r.Found) | $($r.OpCode) |" }
$opDump | Set-Content -Encoding utf8 (Join-Path $PSScriptRoot '_verify_transpiler.md')
Write-Host "wrote _verify_transpiler.md ($($opDump.Count) rows)"

# ---------- legacy-name scan of the BUILT DLL (regression guard) ----------
# The acceptance criterion is "no 4.0.13 legacy names in the build artifact". This bites in
# non-obvious ways: a leftover comment in a log string, or any literal that merely CONTAINS
# one of these tokens (the check is a substring match, so "method_2" also matches "method_23").
# NOTE: `method_10` is intentionally NOT in this list - it is a REAL 4.1.5 member name on
# EFT.CameraControl.CameraManager (verified above), not a 4.0.13 leftover.
$legacyTokens = @(
    'GClass', 'CameraClass', 'method_23', 'method_3', 'method_2', 'method_0',
    'Single_0', 'Single_1', 'Single_2', 'Int_0', 'Int_1', 'String_0',
    'RenderTexture_0', 'OpticComponentUpdater_0', 'CameraLodBiasController_0',
    'FirearmScopeStateStruct', 'GEventArgs', 'SharedGameSettingsClass',
    'MagazineItemClass', 'AmmoItemClass', 'IActorEvents', 'EventsConsumers'
)
# `method_1` is deliberately NOT in the list above: it is a substring of `method_10`,
# which is a REAL 4.1.5 member. Other method_<n> tokens are found by regex and then filtered
# by VALUE against the allow-list below.
#
# Why value-filtering instead of a lookahead like 'method_(?!10\b)\d+': the metadata heaps
# concatenate strings without separators, so `method_10` can be immediately followed by a
# word character, in which case `\b` does NOT match and the negative lookahead wrongly lets
# the match through. Comparing the matched text itself is stable regardless of neighbours.
$legacyRegex = [regex]'method_\d+'
$methodTokenAllowList = @('method_10')   # EFT.CameraControl.CameraManager::method_10 - real in 4.1.5

$dllPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'PiP-Disabler.dll'
Write-Host ""
if (-not (Test-Path $dllPath)) {
    Write-Host "=== Legacy-name scan: SKIPPED (no $dllPath) ==="
} else {
    $rawBytes = [System.IO.File]::ReadAllBytes($dllPath)
    $asText   = [System.Text.Encoding]::ASCII.GetString($rawBytes)
    $u16Text  = [System.Text.Encoding]::Unicode.GetString($rawBytes)
    $legacyHits = New-Object System.Collections.Generic.List[object]
    foreach ($tok in $legacyTokens) {
        $a = ([regex]::Matches($asText,  [regex]::Escape($tok))).Count
        $u = ([regex]::Matches($u16Text, [regex]::Escape($tok))).Count
        if (($a + $u) -gt 0) {
            $legacyHits.Add([pscustomobject]@{ Token=$tok; Ascii=$a; Utf16=$u })
        }
    }
    foreach ($m in $legacyRegex.Matches($asText)) {
        if ($methodTokenAllowList -contains $m.Value) { continue }
        $legacyHits.Add([pscustomobject]@{ Token=$m.Value; Ascii=1; Utf16=0 })
    }
    foreach ($m in $legacyRegex.Matches($u16Text)) {
        if ($methodTokenAllowList -contains $m.Value) { continue }
        $legacyHits.Add([pscustomobject]@{ Token=$m.Value; Ascii=0; Utf16=1 })
    }

    Write-Host "=== Legacy-name scan of PiP-Disabler.dll: $($legacyHits.Count) token(s) with hits (expect 0) ==="
    if ($legacyHits.Count -gt 0) { $legacyHits | Format-Table -AutoSize }
    Write-Host "(each hit is a substring match; a literal like 'method_23' also trips 'method_2')"

    # ---------- Newtonsoft.Json containment check (regression guard) ----------
    # Every method that touches Newtonsoft.Json must be a [MethodImpl(NoInlining)] wrapper, so
    # that an assembly-resolution failure surfaces inside a try/catch in the caller instead of at
    # JIT time of the caller itself (which would throw every frame out of MeshSurgeryManager /
    # ReticleRenderer). The guarantee only holds while no *unwrapped* method body references
    # Newtonsoft.Json - so assert exactly that, here, automatically.
    # Current wrapped users: PerScopeMeshSurgerySettings.DeserializeJson / SerializeJson and
    # I18n.TryReadGameSettingsFile / I18n.ParseLanguageFile.
    Write-Host ""
    $rpJson = New-Object Mono.Cecil.ReaderParameters
    $rpJson.ReadingMode = [Mono.Cecil.ReadingMode]::Deferred
    $rpJson.InMemory = $true
    $pipAsm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dllPath, $rpJson)
    function __AllTypes($ts) { foreach ($t in $ts) { $t; if ($t.HasNestedTypes) { __AllTypes $t.NestedTypes } } }
    $jsonUsers = New-Object System.Collections.Generic.List[object]
    foreach ($t in (__AllTypes $pipAsm.MainModule.Types)) {
        foreach ($m in $t.Methods) {
            if (-not $m.HasBody) { continue }
            $refsNewtonsoft = $false
            foreach ($ins in $m.Body.Instructions) {
                $op = $ins.Operand
                if ($op -ne $null -and $op.DeclaringType -ne $null -and $op.DeclaringType.Namespace -like 'Newtonsoft*') {
                    $refsNewtonsoft = $true; break
                }
            }
            if ($refsNewtonsoft) {
                $jsonUsers.Add([pscustomobject]@{
                    Method     = "$($t.FullName)::$($m.Name)"
                    ImplAttrs  = ($m.ImplAttributes -join ',')
                })
            }
        }
    }
    $badJson = @($jsonUsers | Where-Object { $_.ImplAttrs -notlike '*NoInlining*' })
    Write-Host "=== Newtonsoft.Json containment: $($jsonUsers.Count) method(s) reference it, $($badJson.Count) of them lack NoInlining (expect $($jsonUsers.Count) / 0 - every user must be a NoInlining wrapper) ==="
    if ($jsonUsers.Count -gt 0) { $jsonUsers | Format-Table -AutoSize }
    if ($badJson.Count -gt 0) {
        Write-Host "!! FAIL: a Newtonsoft.Json reference leaked outside a NoInlining wrapper - the"
        Write-Host "!!       'degrade to defaults instead of throwing every frame' guarantee is broken."
    }
}
