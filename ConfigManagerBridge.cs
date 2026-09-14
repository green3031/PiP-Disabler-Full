using BepInEx.Bootstrap;
using BepInEx.Configuration;
using System;
using System.Reflection;

namespace PiPDisabler
{
    /// <summary>
    /// Minimal, reflection-only bridge to BepInEx.ConfigurationManager (F12).
    ///
    /// Why reflection instead of a project reference: ConfigurationManager.dll lives under
    /// BepInEx\plugins\spt\ConfigurationManager\ and is not part of the plugin's compile-time
    /// reference set. Calling it through reflection keeps the plugin loading (and the mod
    /// working) with ConfigurationManager absent, and avoids pinning the port to one
    /// ConfigurationManager build.
    ///
    /// Everything this class relies on was verified against the shipped 18.4 assembly:
    ///   * SettingEntryBase.DispName / .Description / .Category / .DefaultValue are public
    ///     instance properties, and ConfigurationManager copies same-named fields from the
    ///     tag object whose type is named "ConfigurationManagerAttributes" into them
    ///     (SettingEntryBase.SetFromAttributes), so mutation of the tag changes the UI.
    ///   * SettingEntryBase.DefaultValue is re-read from the tag every time a ConfigSettingEntry
    ///     is constructed, i.e. on every ConfigurationManager.BuildSettingList().
    ///   * ConfigurationManager.BuildSettingList() is a public instance method and the window
    ///     setter calls it when the window opens, which is exactly when the tags are consumed.
    /// </summary>
    internal static class ConfigManagerBridge
    {
        /// <summary>GUID of BepInEx.ConfigurationManager 18.4.</summary>
        private const string ConfigurationManagerGuid = "com.bepis.bepinex.configurationmanager";

        private static object _instance;
        private static MethodInfo _buildSettingList;
        private static PropertyInfo _displayingWindow;

        /// <summary>
        /// The ConfigurationManagerAttributes instance attached to this entry as a
        /// ConfigDescription tag, or null when the entry has no tag. All of this plugin's
        /// entries pass exactly one, so Tags[0] is the one ConfigurationManager reads.
        /// </summary>
        internal static ConfigurationManagerAttributes GetTag(ConfigEntryBase entry)
        {
            if (entry == null)
                return null;

            try
            {
                ConfigDescription description = entry.Description;
                if (description == null)
                    return null;

                object[] tags = description.Tags;
                if (tags == null || tags.Length == 0)
                    return null;

                return tags[0] as ConfigurationManagerAttributes;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when ConfigurationManager is loaded and its window is currently shown.</summary>
        internal static bool IsWindowOpen()
        {
            try
            {
                object instance = Resolve();
                if (instance == null || _displayingWindow == null)
                    return false;
                return _displayingWindow.GetValue(instance, null) is bool open && open;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Asks ConfigurationManager to rebuild its setting list so tag changes (display names,
        /// descriptions, dynamic Reset targets) become visible. Only does anything while the
        /// window is actually open - opening the window rebuilds the list by itself.
        /// </summary>
        internal static void RequestRebuild()
        {
            try
            {
                object instance = Resolve();
                if (instance == null || _buildSettingList == null)
                    return;

                if (_displayingWindow != null && !(_displayingWindow.GetValue(instance, null) is bool open && open))
                    return;

                _buildSettingList.Invoke(instance, null);
            }
            catch
            {
                // A display refresh is never worth an exception; the next F12 open rebuilds anyway.
            }
        }

        private static object Resolve()
        {
            if (_instance != null)
                return _instance;

            try
            {
                var infos = Chainloader.PluginInfos;
                BepInEx.PluginInfo info;
                if (infos == null || !infos.TryGetValue(ConfigurationManagerGuid, out info) || info == null)
                    return null;

                object instance = info.Instance;
                if (instance == null)
                    return null;

                Type type = instance.GetType();
                _displayingWindow = type.GetProperty("DisplayingWindow", BindingFlags.Instance | BindingFlags.Public);
                _buildSettingList = type.GetMethod("BuildSettingList", BindingFlags.Instance | BindingFlags.Public);

                _instance = instance;
                return _instance;
            }
            catch
            {
                return null;
            }
        }
    }
}
