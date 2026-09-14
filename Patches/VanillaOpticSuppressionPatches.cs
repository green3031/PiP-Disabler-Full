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
