using System;
using System.Reflection;
using EFT.CameraControl;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace PiPDisabler.Patches
{
    internal static class VanillaOpticSuppression
    {
        private static bool _allowSetResolution;

        /// <summary>
        /// True while this mod is the reason the vanilla optic camera is off. This is the
        /// authoritative "this mod has taken the scope over" state, available without any
        /// OpticSight and without any scope-enter event:
        ///   set  → whenever these patches force the optic camera down
        ///          (OpticCameraManagerEnableOptic_NoPipPatch / ForceDisable of the optic camera),
        ///   clear→ whenever the vanilla optic camera path is handed back
        ///          (RestoreCameraPathForHandBack / ReleaseRenderTexture), on shutdown or mod
        ///          disable (PiPDisabler.RestoreAllCameras), and when the optic camera is observed
        ///          to be rendering again (the game re-enabling itself).
        ///
        /// CollimatorLensGuard keys the lens-hiding invariant off this instead of off an
        /// OpticSight event, which is what the failing 1x collimator mode never produces.
        /// </summary>
        internal static bool ModOwnsOpticCamera { get; private set; }

        internal static void SetModOwnsOpticCamera(bool owns)
        {
            ModOwnsOpticCamera = owns;
        }

        public static bool ShouldSuppress(OpticSight opticSight)
        {
            if (!Settings.ModEnabled.Value)
                return false;

            return !ScopeLifecycle.ShouldBypassForCurrentOptic(opticSight);
        }

        public static void EnsureRenderTextureForVanilla(OpticCameraManager manager)
        {
            if (manager == null || manager.Camera == null)
                return;

            if (manager._renderTexture != null && manager.Camera.targetTexture != null)
                return;

            try
            {
                _allowSetResolution = true;
                manager.SetResolution(manager.OpticFinalResolution);
            }
            finally
            {
                _allowSetResolution = false;
            }
        }

        public static void RestoreVanillaOpticState(OpticSight opticSight)
        {
            if (opticSight == null || !CameraManager.Exist || CameraManager.Instance == null)
                return;

            // The vanilla optic path is being handed back for this optic — this mod no longer owns
            // the optic camera, so the lens-hiding invariant must not keep a lens emptied under a
            // live optic camera (that inverse asymmetry would be just as broken as the one this
            // flag exists to close).
            SetModOwnsOpticCamera(false);

            var manager = CameraManager.Instance.OpticCameraManager;
            if (manager == null)
                return;

            try
            {
                manager.CurrentOpticSight = opticSight;

                if (opticSight.CameraData != null)
                {
                    manager.OpticRetrice?.SetOpticSight(opticSight);
                    manager._updater?.CopyComponentFromOptic(opticSight);
                }

                global::PiPDisabler.PiPDisabler.ForceLensFade(opticSight, false);

                if (manager.Camera != null)
                {
                    manager.Camera.enabled = true;
                    manager.Camera.gameObject.SetActive(true);
                }

                EnsureRenderTextureForVanilla(manager);
                CameraManager.Instance.method_10();
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[VanillaOpticSuppression] Restore vanilla optic state failed: {ex.Message}");
            }
        }

        public static void ReleaseRenderTexture(OpticCameraManager manager)
        {
            if (manager == null)
                return;

            // The optic camera's picture source is being removed: from here on this mod is the one
            // that owns the optic camera path (see ModOwnsOpticCamera).
            SetModOwnsOpticCamera(true);

            try
            {
                if (manager.Camera != null)
                    manager.Camera.targetTexture = null;

                if (manager._renderTexture != null)
                {
                    manager._renderTexture.Release();
                    UnityEngine.Object.Destroy(manager._renderTexture);
                    manager._renderTexture = null;
                }

                Shader.SetGlobalTexture(OpticCameraManager._camTexId, null);
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[VanillaOpticSuppression] ReleaseRenderTexture failed: {ex.Message}");
            }
        }

        public static bool ShouldKeepSetResolution()
            => _allowSetResolution || !Settings.ModEnabled.Value || ScopeLifecycle.IsCurrentOrPendingOpticBypassed();

        /// <summary>
        /// Re-checks the recorded take-over against the optic camera's live state, so a path that
        /// re-enables the camera without going through the hand-back helpers cannot leave the flag
        /// stuck at true (which would keep a lens emptied under a live optic camera). Cheap: two
        /// Unity null checks and two field reads, called at most once every few frames.
        ///
        /// The test is the whole picture pipeline, not `Camera.enabled` alone. MEASURED
        /// (session 20260915_232202): the suppression path this mod actually uses
        /// (OpticCameraManagerEnableOptic_NoPipPatch → ReleaseRenderTexture) leaves the camera
        /// GameObject ENABLED and removes its picture instead (`CurrentOpticSight = null`,
        /// `_renderTexture` destroyed, `targetTexture = null`, global `_CamTex = null`). Reading only
        /// `enabled` therefore declared "the game owns it again" while this mod was in fact still the
        /// reason no lens had a picture source — that false release is what made the 1x collimator
        /// stay black on the first aim (log: line 1553 ENGAGED → 1558 released → 1564 Released while
        /// PiPDiag snapshot #1 still read `opticCam=off _CamTex=NULL`).
        /// </summary>
        internal static void VerifyModOwnsOpticCamera()
        {
            if (!ModOwnsOpticCamera)
                return;

            try
            {
                if (!CameraManager.Exist || CameraManager.Instance == null)
                {
                    ModOwnsOpticCamera = false;
                    return;
                }

                var manager = CameraManager.Instance.OpticCameraManager;
                if (manager == null || manager.Camera == null)
                {
                    ModOwnsOpticCamera = false;
                    return;
                }

                if (VanillaOpticPictureSourceIsLive())
                {
                    ModOwnsOpticCamera = false;
                    PiPDisablerPlugin.DebugLogInfo(
                        "[VanillaOpticSuppression] The vanilla optic picture path is live again" +
                        " (camera enabled + targetTexture + _renderTexture + CurrentOpticSight) —" +
                        " releasing the take-over flag");
                }
            }
            catch
            {
                ModOwnsOpticCamera = false;
            }
        }

        /// <summary>
        /// True when the vanilla optic camera path is genuinely delivering a picture that a lens
        /// drawn through this optic could sample. Every piece is required, because the take-over
        /// removes exactly the pieces a lens samples: an enabled camera with no target texture, no
        /// render texture and no CurrentOpticSight is the SUPPRESSED state, not the live one.
        ///
        /// Used by <see cref="VerifyModOwnsOpticCamera"/> and by CollimatorLensGuard as its
        /// event-free engagement test. Allocation-free: field/property reads and null checks only.
        /// </summary>
        internal static bool VanillaOpticPictureSourceIsLive()
        {
            try
            {
                if (!CameraManager.Exist || CameraManager.Instance == null)
                    return false;

                var manager = CameraManager.Instance.OpticCameraManager;
                if (manager == null)
                    return false;

                var cam = manager.Camera;
                if (cam == null)
                    return false;

                if (!cam.enabled)
                    return false;

                if (cam.targetTexture == null)
                    return false;

                if (manager._renderTexture == null)
                    return false;

                if (manager.CurrentOpticSight == null)
                    return false;

                return true;
            }
            catch
            {
                // Could not read the state ⇒ do not claim it is live.
                return false;
            }
        }

        /// <summary>
        /// Undoes exactly what the suppressed vanilla enable left behind — camera disabled and the
        /// _CamTex global nulled by ReleaseRenderTexture — WITHOUT re-asserting a stale
        /// CurrentOpticSight. Needed when the scope is handed back to vanilla while a lens mesh has
        /// already been restored: without this the restored lens samples a null picture source.
        /// Deliberately narrower than RestoreVanillaOpticState(), which also re-points the manager and
        /// the reticle at a specific optic.
        /// </summary>
        public static void RestoreCameraPathForHandBack()
        {
            if (!CameraManager.Exist || CameraManager.Instance == null)
                return;

            SetModOwnsOpticCamera(false);

            var manager = CameraManager.Instance.OpticCameraManager;
            if (manager == null)
                return;

            try
            {
                if (manager.Camera != null)
                {
                    manager.Camera.enabled = true;
                    manager.Camera.gameObject.SetActive(true);
                }

                EnsureRenderTextureForVanilla(manager);
                CameraManager.Instance.method_10();
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[VanillaOpticSuppression] Hand-back camera restore failed: {ex.Message}");
            }
        }
    }

    internal sealed class OpticCameraManagerEnableOptic_NoPipPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(OpticCameraManager), "OnOpticSightEnabled", new[] { typeof(OpticSight) });

        [PatchPrefix]
        private static bool Prefix(OpticCameraManager __instance, OpticSight opticSight)
        {
            if (__instance == null)
                return true;

            if (!VanillaOpticSuppression.ShouldSuppress(opticSight))
            {
                // Handed back to the game: this mod must not keep claiming ownership of the optic
                // camera, or a lens it emptied would stay emptied under a live optic camera.
                VanillaOpticSuppression.SetModOwnsOpticCamera(false);
                VanillaOpticSuppression.EnsureRenderTextureForVanilla(__instance);
                return true;
            }

            try
            {
                __instance.CurrentOpticSight = null;
                __instance.OpticRetrice?.SetOpticSight(null);

                if (opticSight?.CameraData != null && __instance._updater != null)
                    __instance._updater.CopyComponentFromOptic(opticSight);

                if (__instance.Camera != null)
                    __instance.Camera.gameObject.SetActive(true);

                VanillaOpticSuppression.ReleaseRenderTexture(__instance);

                PiPDisablerPlugin.DebugLogInfo(
                    $"[VanillaOpticSuppression] Skipped vanilla optic manager enable for '{opticSight?.name ?? "null"}' but kept updater sync active");
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[VanillaOpticSuppression] Manager enable suppression failed: {ex.Message}");
            }

            return false;
        }
    }

    internal sealed class OpticCameraManagerSetResolution_NoPipPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(OpticCameraManager), nameof(OpticCameraManager.SetResolution), new[] { typeof(int) });

        [PatchPostfix]
        private static void Postfix(OpticCameraManager __instance)
        {
            if (VanillaOpticSuppression.ShouldKeepSetResolution())
                return;

            VanillaOpticSuppression.ReleaseRenderTexture(__instance);
        }
    }

    internal sealed class CameraManagerOnOpticEnabled_NoPipPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(CameraManager), "method_10", Type.EmptyTypes);

        [PatchPrefix]
        private static bool Prefix()
        {
            if (!Settings.ModEnabled.Value || ScopeLifecycle.IsCurrentOrPendingOpticBypassed())
                return true;

            var currentOptic = CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight;
            if (currentOptic != null && ScopeLifecycle.ShouldBypassForCurrentOptic(currentOptic))
                return true;

            PiPDisablerPlugin.DebugLogInfo(
                "[VanillaOpticSuppression] Skipped CameraManager optic SSAA/lens enable path");
            return false;
        }
    }
}
