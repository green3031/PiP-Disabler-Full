using System;
using System.Collections.Generic;
using EFT;
using EFT.Animations;
using EFT.CameraControl;
using UnityEngine;

namespace PiPDisabler
{
    /// <summary>
    /// Keeps the lens of a scope this mod has taken over hidden, WITHOUT depending on OpticSight
    /// events.
    ///
    /// Why this exists (measured, SPT 4.1.5, session 20260915_125718):
    ///   A 1x collimator mode can have NO OpticSight at all. On the Walther MRS / EOTech HHS-1
    ///   `mod_scope/scope_all_eotech_hhs_1(Clone)` the selected mode_000 is
    ///   `ScopePrefabCache.CurrentModHasOptics == false` while `HasCollimators == true`, the mode node
    ///   carries only a Transform, `PWA.CurrentScope.IsOptic` is false, and the whole raid logs not a
    ///   single `[ScopeLifecycle] ENTER` for it. PiPDiag snapshots of exactly that state show
    ///   `lens.meshCleared=false` (60 verts, `CW FX/OpticSight`) with `opticCam.enabled=false` and
    ///   `_CamTex = NULL` — i.e. this mod had already taken the vanilla optic camera down, the lens
    ///   was still drawing, and a lens that still draws samples a picture source this mod
    ///   deliberately killed (under T7: `_ThermalVisionOn=1` and no thermal image on that path).
    ///
    /// So the scope-enter scan in LensTransparency could never fire for it, and the earlier fix that
    /// only removed the collimator exclusion inside that scan was unreachable. The invariant is
    /// therefore driven by state instead of by events:
    ///
    ///     while the player is aiming through an optic under `mod_scope` of a weapon this mod manages
    ///     (mod enabled, optic not bypassed), the vanilla optic picture must NOT be alive — if any
    ///     piece of it is (the optic RenderTexture, the camera's target texture, or the global
    ///     `_CamTex` the lens shader samples), it is taken back down from the same helpers the scoped
    ///     suppression path uses — and any lens of that optic that would still draw must either stop
    ///     drawing (its geometry is emptied) or stop taking the game's thermal branch (per-material
    ///     `_ThermalVisionOn = 0`).
    ///
    /// The canonical case of the second half is the collimator dot surface: the 1x collimator paints
    /// its reticle dot onto the very mesh that the old rule emptied, and its shader (`CW FX/Collimator`)
    /// has no `_CamTex` at all — so emptying it traded "black sight" for "no dot" (measured, session
    /// 20260915_232202). It keeps its geometry and only loses its dead thermal branch; see the
    /// _refusedDotSurface comment in LensTransparency for the decompiled + shader-bundle evidence.
    ///
    /// The engagement test is deliberately NOT the `VanillaOpticSuppression.ModOwnsOpticCamera` flag:
    /// that flag only tracked `Camera.enabled`, while the mod's real take-over leaves the camera
    /// enabled and removes its picture — which is why the first aim of the previous build never
    /// stayed engaged (measured: log line 1553 ENGAGED → 1558 flag released → 1564 Released).
    ///
    /// Deliberately independent of: OpticSight presence/enabled, any scope-enter event, mode
    /// activity, `activeInHierarchy`, the collimator classification, and every whitelist/blacklist —
    /// this class only ever touches a sight branch the mod already owns by suppressing the camera.
    ///
    /// Ownership is NOT widened: a foreign optic mounted on the same rail is a different scope_* 
    /// branch and is filtered out by LensTransparency's existing sight-root rule, so a canted red dot
    /// belonging to another sight is untouched.
    /// </summary>
    internal static class CollimatorLensGuard
    {
        /// <summary>
        /// How often the cheap gates (player / aiming / camera ownership / sight root) are
        /// re-evaluated once the guard is engaged. A weapon switch or a mode switch is therefore
        /// noticed within a few frames, and the scope-exit / bypass paths keep their existing
        /// unconditional restores.
        /// </summary>
        private const int CheckIntervalFrames = 5;

        /// <summary>
        /// While the player is aiming but the guard is NOT engaged yet, the aim resolution runs at most
        /// this often (the cheap player/aiming gates still run every frame). This is what makes the
        /// FIRST aim work: the picture source is already dead on the frame the player starts aiming, so
        /// a 5-frame poll would leave the lens drawing black for at least that long, and the previous
        /// build never engaged at all because it waited for a take-over flag that the guard itself had
        /// just cleared.
        /// </summary>
        private const int FastResolveIntervalFrames = 2;

        /// <summary>Depth limit while walking up from the aim bone to the scope_* sight node.</summary>
        private const int MaxSightWalk = 8;

        private static Transform _anchor;        // mod_scope the owned optic hangs from
        private static Transform _sightRoot;     // scope_* node of the optic the player is looking through
        private static Component _targetSight;   // OpticSight of the selected mode (may be null)
        private static bool _active;
        private static int _nextCheckFrame;
        private static int _nextResolveFrame;
        private static int _registeredGeneration = -1;
        private static bool _everEngaged;

        private static readonly List<Component> _scopeComponentBuffer = new List<Component>(4);
        private static readonly List<ProceduralWeaponAnimation.SightNBone> _aimBoneBuffer =
            new List<ProceduralWeaponAnimation.SightNBone>(2);

        internal static bool IsActive => _active;

        /// <summary>
        /// Full reset for shutdown / mod disable / a config-driven force exit. Restores the material
        /// state this guard forced (the caller then restores the geometry through
        /// LensTransparency.FullRestoreAll), so no forced `_ThermalVisionOn` can outlive it.
        /// </summary>
        internal static void Reset()
        {
            LensTransparency.RestoreForcedThermalOff();

            _anchor = null;
            _sightRoot = null;
            _targetSight = null;
            _active = false;
            _nextCheckFrame = 0;
            _nextResolveFrame = 0;
            _registeredGeneration = -1;
            _everEngaged = false;
        }

        /// <summary>
        /// Called once per frame from the plugin Update, BEFORE the ScopeLifecycle-driven gating
        /// (that gate needs `CurrentScope.IsOptic`, which is exactly what is false in the case this
        /// guard covers). Allocation-free.
        /// </summary>
        internal static void Tick()
        {
            if (_active)
            {
                // Keep the per-frame cost of a wrong "still owned" reading bounded: re-verify the
                // take-over against the optic camera's live state every frame that we act.
                Patches.VanillaOpticSuppression.VerifyModOwnsOpticCamera();

                // Another writer (or a stale per-material latch) must not be able to put the dot
                // surface back on the thermal branch while we hold the picture source down. Cheap
                // per-frame form: a GetFloat compare per already-claimed material.
                LensTransparency.ReassertForcedThermalOff();
            }

            // ── Cheap gates, evaluated EVERY frame (no allocation, no reflection) ──────────────
            if (!Settings.ModEnabled.Value)
            {
                Release("mod disabled");
                return;
            }

            Player player = Helpers.GetLocalPlayer();
            if (player == null)
            {
                Release("no player");
                return;
            }

            bool aiming;
            try { aiming = player.ProceduralWeaponAnimation != null && player.ProceduralWeaponAnimation.IsAiming; }
            catch { aiming = false; }

            if (!aiming)
            {
                Release("not aiming");
                _nextCheckFrame = Time.frameCount + CheckIntervalFrames;
                _nextResolveFrame = 0;
                return;
            }

            if (VanillaPictureSourceIsLive())
            {
                Release("the vanilla optic camera is delivering a picture again");
                return;
            }

            // ── The ownership test is state-based, never the take-over flag alone ──────────────
            //
            // MEASURED root cause of "it did not work on the FIRST aim" (session 20260915_232202,
            // EOTech HHS-1 1x collimator, T7 on, frames 1820-3140): the previous build required
            // `VanillaOpticSuppression.ModOwnsOpticCamera`, and this guard's own per-frame
            // VerifyModOwnsOpticCamera() call cleared that flag because it only looks at
            // `OpticCameraManager.Camera.enabled`. In the suppression path the mod actually uses
            // (OpticCameraManagerEnableOptic_NoPipPatch → ReleaseRenderTexture) the camera object is
            // left ENABLED and merely loses its picture: `CurrentOpticSight = null`,
            // `_renderTexture` destroyed, `Camera.targetTexture = null`, global `_CamTex = null`.
            // Log: line 1553 ENGAGED → line 1558 "Optic camera is live again — releasing the take-over
            // flag" → line 1564 Released → the lens drew against a dead picture source for the whole
            // collimator period (PiPDiag snapshot #1: `opticCam=off _CamTex=NULL`), and the guard
            // never re-armed because re-arming needed ReleaseRenderTexture/ForceDisable to run again.
            if (_active)
            {
                if (Time.frameCount < _nextCheckFrame) return;
                _nextCheckFrame = Time.frameCount + CheckIntervalFrames;
            }
            else
            {
                if (Time.frameCount < _nextResolveFrame) return;
                _nextResolveFrame = Time.frameCount + FastResolveIntervalFrames;
            }

            if (ScopeLifecycle.IsCurrentOrPendingOpticBypassed())
            {
                Release("this optic is bypassed (vanilla PiP is intentionally kept)");
                return;
            }

            Transform anchor, sightRoot;
            Component targetSight;
            if (!TryResolveAimTarget(player, out anchor, out sightRoot, out targetSight))
            {
                Release("could not resolve the aimed optic under mod_scope");
                return;
            }

            Engage(anchor, sightRoot, targetSight, aiming);
        }

        /// <summary>
        /// True when the vanilla optic camera path is genuinely delivering a picture for the optic
        /// the player is aiming through — the one state in which a drawing lens has something real to
        /// sample and this guard must stay out of the way. Delegates to the shared state test
        /// (VanillaOpticSuppression.VanillaOpticPictureSourceIsLive), which requires the whole
        /// pipeline: an enabled camera WITH a target texture, a live `_renderTexture` and a
        /// `CurrentOpticSight`. An enabled camera alone is the suppressed state here, not the live one
        /// — reading only `Camera.enabled` is the bug that made the first aim fail (see Tick).
        /// </summary>
        private static bool VanillaPictureSourceIsLive()
            => Patches.VanillaOpticSuppression.VanillaOpticPictureSourceIsLive();

        /// <summary>
        /// The take-over this guard keys off has ended (scope handed back, player stopped aiming,
        /// weapon unequipped). Restore the lenses through the shared list so no lens is ever left
        /// emptied while the optic camera is live again.
        /// </summary>
        private static void Release(string reason)
        {
            if (!_active)
            {
                _anchor = null;
                _sightRoot = null;
                _targetSight = null;
                return;
            }

            _active = false;
            _anchor = null;
            _sightRoot = null;
            _targetSight = null;
            _registeredGeneration = -1;

            // RestoreAll() also puts back every `_ThermalVisionOn` this guard forced on the dot
            // surface, so the lens geometry and the material state are always handed back together —
            // never "lens restored but the optic camera still suppressed", and never "geometry
            // restored but the dot left on a dead thermal branch".
            LensTransparency.RestoreAll();

            PiPDisablerPlugin.DebugLogInfo(
                $"[CollimatorLensGuard] Released the optic lens ({reason}) — lens geometry restored");
        }

        /// <summary>
        /// The invariant: register every lens surface of the owned sight (emptying the ones drawing
        /// now) and keep them emptied while the take-over lasts.
        /// </summary>
        private static void Engage(Transform anchor, Transform sightRoot, Component targetSight, bool aiming)
        {
            bool targetChanged = _targetSight != targetSight;
            if ((!_active || _anchor != anchor || _sightRoot != sightRoot) | targetChanged)
            {
                _active = true;
                _anchor = anchor;
                _sightRoot = sightRoot;
                _targetSight = targetSight;
                _registeredGeneration = -1;
                LensTransparency.ForceNextRescan();

                LogEngagement(sightRoot, targetSight, aiming, targetChanged);
            }

            // Scope enter / exit / bypass drop the scan state; replay our registration when that
            // happened (cheap check, no allocation).
            if (LensTransparency.NeedsOwnerRegistration(_registeredGeneration))
            {
                LensTransparency.RegisterOwnedSightSurfaces(anchor, sightRoot);
                _registeredGeneration = LensTransparency.ScanGeneration;
            }

            // Re-empties any lens of the owned sight that started drawing (or was handed back by
            // another path), and adopts surfaces that only appeared later. No-op when nothing changed.
            // The collimator dot surface is refused by KillMesh, so this can no longer delete the dot.
            LensTransparency.EnsureHidden();

            // The collimator dot surface of the owned sight must stop taking the game's thermal
            // branch: the thermal image for the path this mod killed does not exist, so a kept
            // surface on that branch renders black under T7 (per-material `_ThermalVisionOn = 0`,
            // restored on Release).
            LensTransparency.ForceThermalOffForOwnedOptic();
        }

        // ===== Aim target resolution (no OpticSight required) =====

        /// <summary>
        /// True when the player is aiming through a mounted optic on the held weapon.
        ///
        /// The primary signal is the game's own: `PWA.CurrentScope` (the bone the game selected for
        /// the current aim index) plus `SightNBone.BoneRelatesToOptics`, which the game computes from
        /// `ScopePrefabCache.IsOpticBone(bone)` and which is exactly how the game itself decides
        /// whether an aim bone is an optic — available for a collimator mode that has no OpticSight.
        ///
        /// The bones are offered to the resolver in order (CurrentScope first, then
        /// ScopeAimTransforms[AimIndex], which is what CurrentScope aliases), so a client build whose
        /// `BoneRelatesToOptics` does not report an optic bone still gets a second chance. In that
        /// fallback the resolver demands positive evidence of an optic prefab at the resolved sight
        /// root (a ScopePrefabCache / OpticSight / CollimatorSight component), so a plain iron sight
        /// can never be treated as one.
        /// </summary>
        private static bool TryResolveAimTarget(Player player, out Transform anchor,
            out Transform sightRoot, out Component targetSight)
        {
            anchor = null;
            sightRoot = null;
            targetSight = null;

            try
            {
                var pwa = player.ProceduralWeaponAnimation;
                if (pwa == null) return false;

                _aimBoneBuffer.Clear();

                var current = pwa.CurrentScope;
                if (current != null)
                    _aimBoneBuffer.Add(current);

                var transforms = pwa.ScopeAimTransforms;
                if (transforms != null && transforms.Count > 0)
                {
                    int index = pwa.AimIndex;
                    if (index < 0 || index >= transforms.Count)
                        index = 0;

                    var byIndex = transforms[index];
                    if (byIndex != null && !_aimBoneBuffer.Contains(byIndex))
                        _aimBoneBuffer.Add(byIndex);
                }

                for (int i = 0; i < _aimBoneBuffer.Count; i++)
                {
                    if (TryResolveBone(_aimBoneBuffer[i], out anchor, out sightRoot, out targetSight))
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveBone(ProceduralWeaponAnimation.SightNBone candidate,
            out Transform anchor, out Transform sightRoot, out Component targetSight)
        {
            anchor = null;
            sightRoot = null;
            targetSight = null;

            if (candidate == null) return false;

            Transform bone = candidate.Bone;
            if (bone == null) return false;

            Transform scopeRoot = ResolveSightRoot(bone);
            if (scopeRoot == null) return false;

            // Prefer the prefab cache's own node when it sits at/under the resolved root: it is the
            // node that owns the mode_* branches (ScopePrefabCache.SetModeId activates them there).
            var scopeCache = candidate.ScopePrefabCache;
            if (scopeCache != null)
            {
                Transform cacheRoot = scopeCache.transform;
                if (cacheRoot != null && (cacheRoot == scopeRoot || cacheRoot.IsChildOf(scopeRoot)))
                    scopeRoot = cacheRoot;
            }

            Transform modScope = FindModScope(scopeRoot);
            if (modScope == null) return false;

            // The selected mode's OpticSight, when it has one. Null for a pure 1x collimator mode —
            // and nothing below this line requires it.
            _scopeComponentBuffer.Clear();
            scopeRoot.GetComponentsInChildren(true, _scopeComponentBuffer);

            bool opticRelated = candidate.BoneRelatesToOptics;
            Component opticSight = null;

            for (int i = 0; i < _scopeComponentBuffer.Count; i++)
            {
                var c = _scopeComponentBuffer[i];
                if (c == null) continue;

                if (c is OpticSight)
                {
                    if (opticSight == null) opticSight = c;
                    opticRelated = true;
                }
                else if (c is ScopePrefabCache || c is CollimatorSight)
                {
                    opticRelated = true;
                }
            }

            if (!opticRelated) return false;

            anchor = modScope;
            sightRoot = scopeRoot;
            targetSight = opticSight;
            return true;
        }

        /// <summary>
        /// The optic's own root: the highest node below mod_scope whose name starts with "scope".
        /// Falls back to the aim bone's parent (a sight whose prefab node is not named scope*).
        /// Never climbs past mod_scope, so a second optic on the same rail stays a different root.
        /// </summary>
        private static Transform ResolveSightRoot(Transform bone)
        {
            Transform best = bone.parent;

            for (var cur = bone.parent; cur != null; cur = cur.parent)
            {
                string n = cur.name;
                if (string.IsNullOrEmpty(n)) continue;

                if (n.IndexOf("mod_scope", StringComparison.OrdinalIgnoreCase) >= 0)
                    break;

                if (n.StartsWith("scope", StringComparison.OrdinalIgnoreCase))
                    best = cur;
            }

            return best;
        }

        /// <summary>
        /// The mod_scope container the sight hangs from. Prefers the outermost mod_scope (the rail
        /// section the sight is clamped to) and stops before the weapon / player / hands roots.
        /// </summary>
        private static Transform FindModScope(Transform sightRoot)
        {
            Transform best = null;

            int depth = 0;
            for (var cur = sightRoot; cur != null && depth++ < MaxSightWalk; cur = cur.parent)
            {
                string n = cur.name;
                if (string.IsNullOrEmpty(n)) continue;

                if (n.IndexOf("weapon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("hands", StringComparison.OrdinalIgnoreCase) >= 0)
                    break;

                if (n.IndexOf("mod_scope", StringComparison.OrdinalIgnoreCase) >= 0)
                    best = cur;
            }

            return best;
        }

        private static void LogEngagement(Transform sightRoot, Component targetSight, bool aiming, bool targetChanged)
        {
            if (_everEngaged && !targetChanged)
                return;

            _everEngaged = true;

            PiPDisablerPlugin.DebugLogInfo(
                $"[CollimatorLensGuard] ENGAGED on '{sightRoot.name}'" +
                $" (aiming={aiming}, opticSight={(targetSight != null ? targetSight.name : "none")})" +
                " — owning the optic lens because this mod holds the optic camera down");
        }
    }
}
