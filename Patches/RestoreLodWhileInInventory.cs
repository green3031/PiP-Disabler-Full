using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using System.Reflection;

namespace PiPDisabler.Patches
{
    /// <summary>
    /// Restores the scoped LOD bias as soon as the inventory is opened.
    ///
    /// 4.1.5 note: the original target Player.SetInventoryOpened no longer exists on
    /// EFT.Player — it is declared by IHandsController / IItemRelatedView and implemented
    /// per controller. Player.FirearmController.SetInventoryOpened(bool) is the concrete
    /// override that runs while a weapon is in hands, which is the only path that can
    /// matter for a scoped weapon's LOD bias. Empty-hand / other controller paths are
    /// intentionally not covered.
    /// </summary>
    internal sealed class PlayerSetInventoryOpenedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(Player.FirearmController), "SetInventoryOpened", new[] { typeof(bool) });

        [PatchPostfix]
        private static void Postfix(bool opened)
        {
            if (!Settings.ModEnabled.Value) return;
            if (!opened) return;

            CameraSettingsManager.RestoreIfPending();
        }
    }
}