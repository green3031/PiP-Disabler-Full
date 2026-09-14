using BepInEx.Configuration;

namespace PiPDisabler
{
    /// <summary>
    /// Makes the "Reset" button of the per-scope (Custom*) entries meaningful.
    ///
    /// THE PROBLEM
    ///   ConfigurationManager's Reset button writes SettingEntryBase.DefaultValue back into the
    ///   setting. By default that is the ConfigEntry's compile-time default, which for the
    ///   Custom* entries is a generic value that has nothing to do with the scope the player is
    ///   currently holding - so "Reset" threw the per-scope tuning away instead of restoring it.
    ///
    /// HOW IT IS SOLVED (verified against ConfigurationManager 18.4)
    ///   * ConfigSettingEntry's constructor assigns base.DefaultValue = entry.DefaultValue and
    ///     THEN calls SetFromAttributes(), which copies the tag's DefaultValue field into
    ///     SettingEntryBase.DefaultValue whenever the tag's value is non-null. A non-null tag
    ///     value therefore WINS, and a null tag value falls back to the ConfigEntry default.
    ///   * ConfigSettingEntry is constructed from scratch on every
    ///     ConfigurationManager.BuildSettingList(), which runs whenever the window is opened.
    ///   * DrawDefaultButton() reads setting.DefaultValue each frame and passes it to
    ///     setting.Set() on click.
    ///   => Re-writing the tag's DefaultValue field is enough; no reflection into
    ///      SettingEntryBase is needed, and the object is the same one already reachable as
    ///      entry.Description.Tags[0] (which Settings.RecalcOrder also mutates).
    ///
    /// The values written here are produced by PerScopeMeshSurgerySettings when it loads the
    /// active scope's saved values into the Custom* entries, so Reset restores the saved
    /// per-scope tuning. With no override for the active scope the tag value is cleared and
    /// Reset goes back to the normal built-in default.
    /// </summary>
    internal static class ConfigDefaultSync
    {
        /// <summary>Point every per-scope Reset button at the active scope's saved values.</summary>
        internal static void ApplyFromScope(ScopeMeshSurgerySettingsEntry entry)
        {
            if (entry == null)
            {
                Clear();
                return;
            }

            bool changed = false;

            changed |= Set(Settings.CustomPlaneOffsetMeters, entry.PlaneOffsetMeters);
            changed |= Set(Settings.CustomPlane1Radius, entry.Plane1Radius);
            changed |= Set(Settings.CustomPlane1OffsetMeters, entry.Plane1OffsetMeters);
            changed |= Set(Settings.CustomPlane2Position, entry.Plane2Position);
            changed |= Set(Settings.CustomPlane2Radius, entry.Plane2Radius);
            changed |= Set(Settings.CustomPlane3Position, entry.Plane3Position);
            changed |= Set(Settings.CustomPlane3Radius, entry.Plane3Radius);
            changed |= Set(Settings.CustomPlane4Position, entry.Plane4Position);
            changed |= Set(Settings.CustomPlane4Radius, entry.Plane4Radius);
            changed |= Set(Settings.CustomCutStartOffset, entry.CutStartOffset);
            changed |= Set(Settings.CustomCutLength, entry.CutLength);
            changed |= Set(Settings.CustomNearPreserveDepth, entry.NearPreserveDepth);
            changed |= Set(Settings.CustomReticleBaseSize, entry.ReticleBaseSize);
            changed |= Set(Settings.CustomReticleSizeMultiplier, entry.ReticleSizeMultiplier > 0f ? entry.ReticleSizeMultiplier : 1f);
            changed |= Set(Settings.CustomMeshReticleMinScale, entry.MeshReticleMinScale);
            changed |= Set(Settings.CustomMeshReticleMaxScale, entry.MeshReticleMaxScale);
            changed |= Set(Settings.CustomWeaponScaleMinMagnification, entry.WeaponScaleMinMagnification);
            changed |= Set(Settings.CustomWeaponScaleMaxMagnification, entry.WeaponScaleMaxMagnification);
            changed |= Set(Settings.CustomWeaponScaleMultiplier, entry.WeaponScaleMultiplier > 0f ? entry.WeaponScaleMultiplier : 1f);
            changed |= Set(Settings.CustomVisualRecoilCompensation, entry.VisualRecoilCompensation);
            changed |= Set(Settings.CustomVignetteOpacity, entry.VignetteOpacity);
            changed |= Set(Settings.CustomVignetteRadius, entry.VignetteRadius);
            changed |= Set(Settings.CustomVignetteSoftness, entry.VignetteSoftness);
            changed |= Set(Settings.CustomExpandSearchToWeaponRoot, entry.ExpandSearchToWeaponRoot);

            if (changed)
                ConfigManagerBridge.RequestRebuild();
        }

        /// <summary>Drop the per-scope Reset targets (null = use the ConfigEntry default again).</summary>
        internal static void Clear()
        {
            bool changed = false;

            changed |= Set(Settings.CustomPlaneOffsetMeters, null);
            changed |= Set(Settings.CustomPlane1Radius, null);
            changed |= Set(Settings.CustomPlane1OffsetMeters, null);
            changed |= Set(Settings.CustomPlane2Position, null);
            changed |= Set(Settings.CustomPlane2Radius, null);
            changed |= Set(Settings.CustomPlane3Position, null);
            changed |= Set(Settings.CustomPlane3Radius, null);
            changed |= Set(Settings.CustomPlane4Position, null);
            changed |= Set(Settings.CustomPlane4Radius, null);
            changed |= Set(Settings.CustomCutStartOffset, null);
            changed |= Set(Settings.CustomCutLength, null);
            changed |= Set(Settings.CustomNearPreserveDepth, null);
            changed |= Set(Settings.CustomReticleBaseSize, null);
            changed |= Set(Settings.CustomReticleSizeMultiplier, null);
            changed |= Set(Settings.CustomMeshReticleMinScale, null);
            changed |= Set(Settings.CustomMeshReticleMaxScale, null);
            changed |= Set(Settings.CustomWeaponScaleMinMagnification, null);
            changed |= Set(Settings.CustomWeaponScaleMaxMagnification, null);
            changed |= Set(Settings.CustomWeaponScaleMultiplier, null);
            changed |= Set(Settings.CustomVisualRecoilCompensation, null);
            changed |= Set(Settings.CustomVignetteOpacity, null);
            changed |= Set(Settings.CustomVignetteRadius, null);
            changed |= Set(Settings.CustomVignetteSoftness, null);
            changed |= Set(Settings.CustomExpandSearchToWeaponRoot, null);

            if (changed)
                ConfigManagerBridge.RequestRebuild();
        }

        /// <summary>
        /// Writes one Reset target. The boxed value must be of the entry's exact type: BepInEx's
        /// ConfigEntry&lt;T&gt;.BoxedValue setter does an unguarded (T) cast, so a double would
        /// throw for a float entry. Returns true when the tag actually changed.
        /// </summary>
        private static bool Set(ConfigEntryBase entry, object value)
        {
            ConfigurationManagerAttributes tag = ConfigManagerBridge.GetTag(entry);
            if (tag == null)
                return false;

            if (object.Equals(tag.DefaultValue, value))
                return false;

            tag.DefaultValue = value;
            return true;
        }
    }
}
