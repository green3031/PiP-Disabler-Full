using System;
using System.Diagnostics;
using SPT.Reflection.Patching;

namespace PiPDisabler.Patches
{
    internal static class Patcher
    {
        private static bool _enabled;

        public static void Enable()
        {
            if (_enabled) return;
            _enabled = true;
            SafeEnable<OpticSightOnEnablePatch>();
            SafeEnable<OpticSightOnDisablePatch>();
            SafeEnable<TacticalRangeFinderOnEnablePatch>();
            SafeEnable<TacticalRangeFinderIgnoreLocalBodyPatch>();
            SafeEnable<ChangeAimingModePatch>();
            SafeEnable<SetScopeModePatch>();
            SafeEnableIfAvailable<PlayerOnSetInHandsPatch>(PlayerOnSetInHandsPatch.IsTargetAvailable);
            SafeEnable<PlayerSetInventoryOpenedPatch>();            SafeEnable<OpticCameraManagerEnableOptic_NoPipPatch>();
            SafeEnable<OpticCameraManagerSetResolution_NoPipPatch>();
            SafeEnable<CameraManagerOnOpticEnabled_NoPipPatch>();
            SafeEnable<MainCameraLodBiasSetByFovPatch>();
            SafeEnable<PiPDisabler.OpticComponentUpdaterCopyComponentFromOptic_DisablePiP>();
            SafeEnable<PiPDisabler.OpticComponentUpdaterLateUpdate_DisablePiP>();
            SafeEnable<PiPDisabler.OpticSightLensFade_NoPipPatch>();
            SafeEnable<PWAMethod23Patch>();
            SafeEnable<PlayerLookPatch>();
            SafeEnable<WeaponScalingPatch>();
            VisualRecoilCompensationPatch.Enable();
            SafeEnable<GrassMotionVectorSuppressionPatch>();
            SafeEnable<PWAWeaponRootZOffsetPatch>();
            SafeEnable<FireModeSwitchMovementPatch>();
            SafeEnable<MagnificationSwitchMovementContextPatch>();
            SafeEnable<ModToggleTriggerMovementPatch>();
            SafeEnable<SwayVectorVelocityFovScalingPatch>();
            SafeEnable<SwayComponentVelocityFovScalingPatch>();
            SafeEnable<SpringVectorAccelerationFovScalingPatch>();
            SafeEnable<SpringComponentAccelerationFovScalingPatch>();
            SafeEnable<RecoilReturnToZeroPatch>();
            // External-mod compat shims: FikaCompat guards itself, but DERPCompat and
            // FOVFixCompat call Harmony.Patch() with a reflection-resolved target that can be
            // null when the other mod present but renamed its methods. An exception escaping
            // here would abort Patcher.Enable() and with it ScopeLifecycle.Init(), so these are
            // isolated the same way ModulePatches are.
            SafeRunStatic(FikaCompat.Enable, nameof(FikaCompat));
            SafeRunStatic(FOVFixCompat.Enable, nameof(FOVFixCompat));
            SafeRunStatic(DERPCompat.Enable, nameof(DERPCompat));

        }

        private static void SafeRunStatic(Action enable, string name)
        {
            try
            {
                enable();
            }
            catch (Exception ex)
            {
                LogEnableFailure(name, ex);
            }
        }

        private static void SafeEnable<T>() where T : ModulePatch, new()
        {
            try
            {
                new T().Enable();
            }
            catch (Exception ex)
            {
                LogEnableFailure(typeof(T).Name, ex);
            }
        }

        /// <summary>
        /// For targets that may not exist in this client build: pre-check first and skip
        /// safely (no ModulePatch.Enable() call, which would throw) when unavailable.
        /// </summary>
        private static void SafeEnableIfAvailable<T>(Func<bool> isTargetAvailable) where T : ModulePatch, new()
        {
            try
            {
                if (isTargetAvailable != null && !isTargetAvailable())
                {
                    LogAlways($"[Patcher] Skipped {typeof(T).Name}: target method not found (safe skip)");
                    return;
                }

                new T().Enable();
            }
            catch (Exception ex)
            {
                LogEnableFailure(typeof(T).Name, ex);
            }
        }

        // NOTE: the upstream mod routed this through PiPDisablerPlugin.DebugLogError, which only
        // writes when "Debug logging" is true — so a silently-lost patch was invisible in the
        // default configuration. Here it is unconditional on purpose.
        private static void LogEnableFailure(string patchName, Exception ex)
            => LogAlways($"[Patcher] Failed to enable {patchName}: {ex.Message}");

        private static void LogAlways(string message)
        {
            var log = PiPDisablerPlugin.LogSource;
            if (log != null)
                log.LogError(message);
            else
                Console.WriteLine(message);
        }
    }
}
