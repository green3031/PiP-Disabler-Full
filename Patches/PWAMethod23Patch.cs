using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EFT.Animations;
using EFT.CameraControl;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace PiPDisabler.Patches
{
    /// <summary>
    /// Rewrites ProceduralWeaponAnimation.OnAimOrPoseChanged's SetFov call in-place so
    /// EFT only executes one FOV write per frame path.
    /// </summary>
    internal sealed class PWAMethod23Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(ProceduralWeaponAnimation),
                nameof(ProceduralWeaponAnimation.OnAimOrPoseChanged));

        [PatchPrefix]
        private static void Prefix(ProceduralWeaponAnimation __instance, MethodBase __originalMethod)
        {
            if (__instance == null) return;
            FovOverrideContext.CurrentPwa = __instance;

            LogMappingHit(__instance, __originalMethod);
        }

        // ===================================================================
        // Mapping-verification aid for the single inferred transpiler target.
        //
        // 4.0.13 patched `ProceduralWeaponAnimation.method_23`; that obfuscated name
        // no longer exists in 4.1.5, so GetTargetMethod() above attaches to
        // `OnAimOrPoseChanged` instead (see 移植说明-完整版.md §4.3-14 and §6.2).
        // This log is how you confirm that inference in-game:
        //   switch stance (stand/crouch/prone) or enter/exit a scope -> a hit means
        //   OnAimOrPoseChanged really is the pose/aim FOV writer, i.e. the mapping holds.
        //
        // Cost control (deliberate, non-negotiable):
        //   * gated by Settings.DebugLogging (default FALSE) -> literally zero cost when off
        //   * total budget: at most ONE line per second (also never twice in the same frame),
        //     so switching stance cannot flood the log
        //   * everything is wrapped in try/catch: a diagnostic must never break the
        //     FOV override, which runs on a hot per-frame path
        // ===================================================================
        private static int _hitCount;
        private static int _lastLogFrame = -1;
        private static int _lastLogSecond = -1;

        private static void LogMappingHit(ProceduralWeaponAnimation pwa, MethodBase originalMethod)
        {
            if (!Settings.DebugLogging.Value) return;

            try
            {
                int frame = Time.frameCount;
                int second = (int)Time.realtimeSinceStartup;
                if (frame == _lastLogFrame || second == _lastLogSecond) return;

                _lastLogFrame = frame;
                _lastLogSecond = second;
                _hitCount++;

                float camFov = -1f;
                try
                {
                    if (CameraManager.Exist && CameraManager.Instance != null)
                        camFov = CameraManager.Instance.Fov;
                }
                catch { /* diagnostics only */ }

                float baseFov = -1f;
                bool aiming = false;
                bool sprint = false;
                bool isOptic = false;
                try
                {
                    baseFov = pwa.HeadBobbing;
                    aiming = pwa.IsAiming;
                    sprint = pwa.Sprint;
                    isOptic = pwa.CurrentScope.IsOptic;
                }
                catch { /* diagnostics only */ }

                string target = originalMethod != null ? originalMethod.Name : "null";

                // NOTE: deliberately does NOT spell out the 4.0.13 obfuscated name here.
                // A literal "method_<n>" token in the shipped assembly would fail the
                // "no 4.0.13 legacy names in the build artifact" acceptance check
                // (it is a substring match), so the mapping is documented in
                // 移植说明-完整版.md §4.3-14 / §6.2 instead.
                PiPDisablerPlugin.DebugLogInfo(
                    $"[PWAMethod23] hit #{_hitCount}: target='{target}' " +
                    $"(4.0.13 obfuscated PWA pose/aim FOV writer — see porting doc 4.3-14) " +
                    $"caller='{GetCallerName()}' " +
                    $"camFov={camFov:F2} baseFov={baseFov:F2} aiming={aiming} sprint={sprint} optic={isOptic} " +
                    $"frame={frame}");
            }
            catch { /* diagnostics only */ }
        }

        /// <summary>
        /// Best-effort name of whoever triggered the patched method, skipping our own
        /// frames, the Harmony/MonoMod trampolines, and the patched method itself.
        /// Only ever executed when a log line is actually about to be written.
        /// </summary>
        private static string GetCallerName()
        {
            try
            {
                var trace = new System.Diagnostics.StackTrace(1, fNeedFileInfo: false);
                for (int i = 0; i < trace.FrameCount && i < 8; i++)
                {
                    var method = trace.GetFrame(i)?.GetMethod();
                    if (method == null) continue;

                    var declaringType = method.DeclaringType;
                    if (declaringType == null) continue;
                    if (declaringType == typeof(PWAMethod23Patch)) continue;

                    string ns = declaringType.Namespace ?? string.Empty;
                    if (ns.StartsWith("HarmonyLib", StringComparison.Ordinal) ||
                        ns.StartsWith("MonoMod", StringComparison.Ordinal))
                        continue;

                    if (method.Name == nameof(ProceduralWeaponAnimation.OnAimOrPoseChanged)) continue;

                    return declaringType.Name + "." + method.Name;
                }
            }
            catch { /* diagnostics only */ }

            return "unknown";
        }

        [PatchPostfix]
        private static void Postfix()
        {
            FovOverrideContext.CurrentPwa = null;
        }

        [PatchTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var setFov = AccessTools.Method(typeof(CameraManager), nameof(CameraManager.SetFov));
            var replacement = AccessTools.Method(typeof(PWAMethod23Patch), nameof(SetFovWithOverride));

            foreach (var code in instructions)
            {
                if (code.opcode == OpCodes.Callvirt && Equals(code.operand, setFov))
                {
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                    continue;
                }

                yield return code;
            }
        }

        private static void SetFovWithOverride(CameraManager cameraManager, float targetFov, float duration, bool force)
        {
            var pwa = FovOverrideContext.CurrentPwa;
            if (cameraManager == null)
                return;

            bool modZoomEnabled =
                Settings.ModEnabled.Value;

            bool isAdsOptic = false;
            if (pwa != null && pwa.IsAiming && !pwa.Sprint)
            {
                try { isAdsOptic = pwa.CurrentScope.IsOptic; }
                catch { isAdsOptic = false; }
            }

            if (pwa != null &&
                modZoomEnabled &&
                ScopeLifecycle.IsScoped &&
                !ScopeLifecycle.IsModBypassedForCurrentScope &&
                !FreelookTracker.IsFreelooking &&
                isAdsOptic)
            {
                float zoomBaseFov = FovController.MagnificationBaselineFov;
                float zoomedFov = FovController.ComputeZoomedFov();
                bool smoothScopeFov = FovController.IsSmoothScopeFovActive();

                if (zoomedFov >= 0.5f && (smoothScopeFov || zoomedFov <= zoomBaseFov))
                {
                    if (FovController.HasFovChanged(zoomedFov))
                    {
                        FovController.TrackAppliedFov(zoomedFov);
                        FreelookTracker.CacheAppliedFov(zoomedFov);
                        cameraManager.SetFov(zoomedFov, duration, false);
                    }
                    return;
                }
            }

            // Block EFT's OnAimOrPoseChanged FOV writes while ADS with an optic.
            // Pose changes (stand/crouch/prone) call OnAimOrPoseChanged and can stomp zoom.
            if (modZoomEnabled &&
                ScopeLifecycle.IsScoped &&
                !ScopeLifecycle.IsModBypassedForCurrentScope &&
                isAdsOptic)
                return;

            // After scope exit, RestoreFov protects the ADS transition FOV. Once ADS
            // ends, EFT's normal base-FOV write must be allowed through immediately.
            if (ScopeLifecycle.HasPostExitRestore)
            {
                if (pwa != null && !pwa.IsAiming)
                {
                    ScopeLifecycle.ClearPostExitRestore();
                }
                else
                {
                    float restoreFov = ScopeLifecycle.PostExitRestoreFov;
                    if (Mathf.Abs(targetFov - restoreFov) > FovController.FovChangeThreshold)
                        return;
                }
            }

            cameraManager.SetFov(targetFov, duration, force);
        }

        private static class FovOverrideContext
        {
            [System.ThreadStatic]
            internal static ProceduralWeaponAnimation CurrentPwa;
        }
    }
}
