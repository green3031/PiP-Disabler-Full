using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace PiPDisabler
{
    /// <summary>
    /// Display-language layer for the ConfigurationManager (F12) window.
    ///
    /// DESIGN
    ///   * The cfg section names and key names are STABLE ENGLISH IDENTIFIERS and never change
    ///     with the language - only what ConfigurationManager *draws* changes.
    ///   * Every visible string comes from an external language file under
    ///     &lt;plugin dir&gt;\i18n\&lt;code&gt;.json (shipped next to the DLL). Translating a
    ///     language means editing one JSON file - no recompile, no cfg migration.
    ///   * The text is pushed into the tag object that is already attached to every
    ///     ConfigDescription (ConfigurationManagerAttributes.DispName / .Description /
    ///     .Category). ConfigurationManager reads those once per setting-list rebuild, so
    ///     the strings in ConfigDescription (English) stay in place as the built-in fallback.
    ///   * ConfigurationManager does not re-read the tags while its window is open, so a
    ///     language switch is applied on the next list rebuild (opening F12 again, or the
    ///     explicit rebuild this class requests when the window is already open).
    ///
    /// Detection chain (first hit wins, every step is allowed to fail):
    ///   1. runtime   - EFT.Settings.SettingsManager.Instance.Game.Settings.Language.Value
    ///                  (SPT/EFT locale code, e.g. "en" / "ru" / "ch"); not available while
    ///                  BepInEx loads plugins, so it is probed again for a while from Tick().
    ///   2. settings  - the game's own Game.ini (JSON) written by the client.
    ///   3. system    - UnityEngine.Application.systemLanguage.
    ///   4. built-in  - "en".
    /// </summary>
    internal static class I18n
    {
        /// <summary>Value of General/Language that means "use the detection chain".</summary>
        internal const string AutoValue = "Auto";

        /// <summary>Code used when nothing else matched; en.json is the guaranteed fallback.</summary>
        internal const string FallbackCode = "en";

        private const string LanguageFolderName = "i18n";
        private const float RuntimeProbeWindowSeconds = 120f;
        private const int RuntimeProbeIntervalFrames = 120;

        private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

        private static ConfigEntry<string> _languageEntry;
        private static LanguageFile _applied;
        private static float _probeDeadline;
        private static int _probeFrame;
        private static bool _probeDone;
        private static readonly HashSet<string> WarnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Absolute path of the folder the language files are read from.</summary>
        internal static string LanguageDirectory { get; private set; }

        /// <summary>Language code actually applied to the UI (after fallbacks).</summary>
        internal static string AppliedCode { get; private set; }

        /// <summary>Human readable name of the applied language file (or "English (built-in)").</summary>
        internal static string AppliedLanguageName { get; private set; }

        /// <summary>Code the detection chain reported last (Auto mode only).</summary>
        internal static string DetectedCode { get; private set; }

        /// <summary>Which step of the chain produced DetectedCode.</summary>
        internal static string DetectedSource { get; private set; }

        // ===================================================================================
        // lifecycle
        // ===================================================================================

        internal static void Initialize(ConfigEntry<string> languageEntry)
        {
            try
            {
                _languageEntry = languageEntry;
                LanguageDirectory = ResolveLanguageDirectory();

                if (!string.IsNullOrEmpty(LanguageDirectory))
                    PiPDisablerPlugin.LogSource?.LogInfo("[I18n] language folder: " + LanguageDirectory);
                else
                    PiPDisablerPlugin.LogSource?.LogWarning("[I18n] could not resolve the plugin folder - built-in English text is used.");

                _probeDeadline = Time.realtimeSinceStartup + RuntimeProbeWindowSeconds;

                if (_languageEntry != null)
                    _languageEntry.SettingChanged += OnLanguageSettingChanged;

                ResolveAndApply(true);
            }
            catch (Exception ex)
            {
                LogWarning("[I18n] init failed (" + ex.GetType().Name + ": " + ex.Message + ") - built-in English text stays in place.");
            }
        }

        internal static void Shutdown()
        {
            try
            {
                if (_languageEntry != null)
                    _languageEntry.SettingChanged -= OnLanguageSettingChanged;
            }
            catch
            {
                // nothing sensible to do while the plugin is being torn down
            }
        }

        private static void OnLanguageSettingChanged(object sender, EventArgs e)
        {
            try
            {
                _probeDone = true; // an explicit choice always beats the automatic probe
                ResolveAndApply(true);
            }
            catch (Exception ex)
            {
                LogWarning("[I18n] applying the new language failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Called once per frame from the plugin's Update(). Cheap: it only does real work a
        /// couple of dozen times per session, and gives up permanently as soon as it has an
        /// answer (or after RuntimeProbeWindowSeconds).
        /// </summary>
        internal static void Tick()
        {
            if (_probeDone)
                return;

            try
            {
                _probeFrame++;
                if (_probeFrame % RuntimeProbeIntervalFrames != 0)
                    return;

                if (Time.realtimeSinceStartup > _probeDeadline)
                {
                    _probeDone = true;
                    return;
                }

                if (_languageEntry != null && !IsAuto(_languageEntry.Value))
                {
                    _probeDone = true;
                    return;
                }

                string runtimeCode;
                if (!TryReadRuntimeCodeSafe(out runtimeCode) || string.IsNullOrWhiteSpace(runtimeCode))
                    return;

                _probeDone = true;
                DetectedCode = NormalizeCode(runtimeCode);
                DetectedSource = "runtime probe (EFT.Settings.SettingsManager.Game.Settings.Language)";
                PiPDisablerPlugin.LogSource?.LogInfo("[I18n] runtime language available: '" + runtimeCode + "' -> '" + DetectedCode + "'.");

                if (string.Equals(DetectedCode, AppliedCode, StringComparison.OrdinalIgnoreCase))
                    return;

                Apply(Load(DetectedCode, null), DetectedCode, DetectedSource, true);
            }
            catch (Exception ex)
            {
                _probeDone = true;
                LogWarning("[I18n] runtime language probe failed: " + ex.Message);
            }
        }

        // ===================================================================================
        // resolution
        // ===================================================================================

        private static void ResolveAndApply(bool log)
        {
            string manualValue = _languageEntry != null ? (_languageEntry.Value ?? string.Empty) : string.Empty;
            bool manual = !IsAuto(manualValue);

            string wanted;
            string source;
            if (manual)
            {
                wanted = NormalizeCode(manualValue);
                source = "manual (General / Language = '" + manualValue + "')";
                DetectedCode = wanted;
                DetectedSource = source;
            }
            else
            {
                wanted = DetectCode(out source);
                DetectedCode = wanted;
                DetectedSource = source;
            }

            Apply(Load(wanted, manual ? manualValue : null), wanted, source, log);
        }

        /// <summary>
        /// The detection chain. Every step is individually guarded: a step that is unavailable -
        /// including one whose JIT compilation fails because the game type it names moved - is
        /// skipped and the next step runs. The last step cannot fail.
        /// </summary>
        private static string DetectCode(out string source)
        {
            string runtimeCode;
            if (TryReadRuntimeCodeSafe(out runtimeCode) && !string.IsNullOrWhiteSpace(runtimeCode))
            {
                source = "runtime (EFT.Settings.SettingsManager.Game.Settings.Language)";
                return NormalizeCode(runtimeCode);
            }

            try
            {
                string fileCode;
                string filePath;
                if (TryReadGameSettingsFile(out fileCode, out filePath))
                {
                    source = "game settings file (" + filePath + ")";
                    return NormalizeCode(fileCode);
                }
            }
            catch (Exception ex)
            {
                LogWarningOnce("settings-file", "[I18n] step 2 (game settings file) unavailable: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                string systemCode;
                if (TryReadSystemLocale(out systemCode))
                {
                    source = "OS locale (UnityEngine.Application.systemLanguage)";
                    return NormalizeCode(systemCode);
                }
            }
            catch (Exception ex)
            {
                LogWarningOnce("system-locale", "[I18n] step 3 (OS locale) unavailable: " + ex.GetType().Name + ": " + ex.Message);
            }

            source = "built-in fallback";
            return FallbackCode;
        }

        /// <summary>
        /// Step 1 - the live EFT settings singleton. Wrapped in a NoInlining helper so a
        /// missing/renamed member surfaces as a catchable exception here instead of being
        /// folded into the caller at JIT time (same reasoning as the Json bridge in
        /// PerScopeMeshSurgerySettings).
        /// </summary>
        private static bool TryReadRuntimeCodeSafe(out string code)
        {
            try
            {
                return TryReadRuntimeCode(out code);
            }
            catch (Exception ex)
            {
                code = null;
                LogWarningOnce("runtime-probe", "[I18n] step 1 (runtime language) unavailable: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadRuntimeCode(out string code)
        {
            code = null;
            try
            {
                if (!Comfort.Common.Singleton<EFT.Settings.SettingsManager>.Instantiated)
                    return false;
                EFT.Settings.SettingsManager manager = Comfort.Common.Singleton<EFT.Settings.SettingsManager>.Instance;
                if (manager == null)
                    return false;
                var setting = manager.Game != null && manager.Game.Settings != null ? manager.Game.Settings.Language : null;
                if (setting == null)
                    return false;
                code = setting.Value;
                return !string.IsNullOrWhiteSpace(code);
            }
            catch
            {
                code = null;
                return false;
            }
        }

        /// <summary>Step 2 - the game's own settings file (SPT redirects it under SPT_Runtime).</summary>
        /// <remarks>
        /// NoInlining keeps the Newtonsoft.Json reference inside this one frame: if the assembly
        /// fails to resolve, the failure is raised here and is caught by the caller's try/catch
        /// instead of being folded into DetectCode/ResolveAndApply at JIT time. Same reasoning and
        /// the same guard as the Json bridge in PerScopeMeshSurgerySettings.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadGameSettingsFile(out string code, out string path)
        {
            code = null;
            path = null;

            foreach (string candidate in EnumerateGameSettingsCandidates())
            {
                try
                {
                    if (!File.Exists(candidate))
                        continue;

                    JObject root = JObject.Parse(StripLineComments(File.ReadAllText(candidate, Encoding.UTF8)));
                    JToken token = root["Language"] ?? root["language"] ?? root["Settings/Game/Language"];
                    if (token == null || token.Type == JTokenType.Null)
                        continue;

                    string value = token.ToString();
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    code = value.Trim();
                    path = candidate;
                    return true;
                }
                catch
                {
                    // unreadable / not JSON / half written - try the next candidate
                }
            }

            return false;
        }

        private static IEnumerable<string> EnumerateGameSettingsCandidates()
        {
            string gameRoot = null;
            try
            {
                gameRoot = BepInEx.Paths.GameRootPath;
            }
            catch
            {
                gameRoot = null;
            }

            if (!string.IsNullOrEmpty(gameRoot))
            {
                yield return Path.Combine(gameRoot, "SPT_Runtime", "user", "sptSettings", "Game.ini");
                yield return Path.Combine(gameRoot, "SPT_Runtime", "user", "sptSettings", "Game.json");
                yield return Path.Combine(gameRoot, "user", "sptSettings", "Game.ini");
            }

            string appData = null;
            try
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            catch
            {
                appData = null;
            }

            if (!string.IsNullOrEmpty(appData))
                yield return Path.Combine(appData, "Battlestate Games", "Escape from Tarkov", "Settings", "Game.ini");
        }

        /// <summary>
        /// Step 3 - the OS language, mapped the same way EFT's own LocalizationManager maps it.
        /// Returns false for languages we have no code for so step 4 can take over.
        /// </summary>
        private static bool TryReadSystemLocale(out string code)
        {
            code = null;
            try
            {
                switch (Application.systemLanguage)
                {
                    case SystemLanguage.ChineseSimplified: code = "zh-CN"; break;
                    case SystemLanguage.ChineseTraditional: code = "zh-TW"; break;
                    case SystemLanguage.Chinese: code = "zh-CN"; break;
                    case SystemLanguage.English: code = "en"; break;
                    case SystemLanguage.Russian: code = "ru"; break;
                    case SystemLanguage.German: code = "de"; break;
                    case SystemLanguage.French: code = "fr"; break;
                    case SystemLanguage.Japanese: code = "ja"; break;
                    case SystemLanguage.Korean: code = "ko"; break;
                    case SystemLanguage.Spanish: code = "es"; break;
                    case SystemLanguage.Italian: code = "it"; break;
                    case SystemLanguage.Portuguese: code = "pt"; break;
                    case SystemLanguage.Polish: code = "pl"; break;
                    case SystemLanguage.Turkish: code = "tr"; break;
                    case SystemLanguage.Czech: code = "cs"; break;
                    case SystemLanguage.Thai: code = "th"; break;
                    case SystemLanguage.Vietnamese: code = "vi"; break;
                    case SystemLanguage.Ukrainian: code = "uk"; break;
                    case SystemLanguage.Hungarian: code = "hu"; break;
                    case SystemLanguage.Dutch: code = "nl"; break;
                    case SystemLanguage.Romanian: code = "ro"; break;
                    case SystemLanguage.Swedish: code = "sv"; break;
                    case SystemLanguage.Danish: code = "da"; break;
                    case SystemLanguage.Finnish: code = "fi"; break;
                    case SystemLanguage.Norwegian: code = "no"; break;
                    case SystemLanguage.Greek: code = "el"; break;
                    case SystemLanguage.Bulgarian: code = "bg"; break;
                    default: code = null; break;
                }
            }
            catch
            {
                code = null;
            }

            return !string.IsNullOrEmpty(code);
        }

        // ===================================================================================
        // applying
        // ===================================================================================

        private static void Apply(LanguageFile file, string wantedCode, string source, bool log)
        {
            _applied = file;
            AppliedCode = file != null ? file.Code : FallbackCode;
            AppliedLanguageName = file != null ? file.DisplayName : "English (built-in)";

            ApplyToConfigEntries();
            ConfigManagerBridge.RequestRebuild();

            if (!log)
                return;

            if (file == null)
            {
                Log("[I18n] no language file matched '" + wantedCode + "' in '" + (LanguageDirectory ?? "<unresolved>") +
                    "' - the built-in English text is used. Selected by: " + source);
            }
            else
            {
                Log("[I18n] display language '" + AppliedCode + "' (" + AppliedLanguageName + ") loaded from " + file.FilePath +
                    " - " + file.CountEntries() + " entries / " + file.SectionOrder.Count + " sections. Selected by: " + source);
            }
        }

        /// <summary>
        /// Pushes the loaded strings into the ConfigurationManager tag attached to every entry.
        /// A null field means "leave what is already there", which is the English text from
        /// ConfigDescription / the raw cfg key - that is the per-entry fallback.
        /// </summary>
        private static void ApplyToConfigEntries()
        {
            List<ConfigEntryBase> entries = Settings.ConfigEntries;
            if (entries == null)
                return;

            for (int i = 0; i < entries.Count; i++)
            {
                ConfigEntryBase entry = entries[i];
                if (entry == null)
                    continue;

                ConfigurationManagerAttributes tag = ConfigManagerBridge.GetTag(entry);
                if (tag == null)
                    continue;

                ConfigDefinition definition = entry.Definition;
                if (definition == null)
                    continue;

                string section = definition.Section;
                string key = definition.Key;

                // Section headers are localized through the same tag object: ConfigurationManager
                // groups settings by SettingEntryBase.Category and draws the group name as the
                // section header. Always set it (identity mapping when untranslated) so that a
                // partially translated file can never split one section into two groups.
                tag.Category = _applied != null ? _applied.Header(section) : section;

                LanguageEntry text = _applied != null ? _applied.Lookup(section, key) : null;
                tag.DispName = text != null && !string.IsNullOrEmpty(text.Name) ? text.Name : null;
                tag.Description = text != null && !string.IsNullOrEmpty(text.Description) ? text.Description : null;
            }
        }

        // ===================================================================================
        // language files
        // ===================================================================================

        private static string ResolveLanguageDirectory()
        {
            try
            {
                string pluginDir = Path.GetDirectoryName(typeof(I18n).Assembly.Location);
                return string.IsNullOrEmpty(pluginDir) ? null : Path.Combine(pluginDir, LanguageFolderName);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Finds and parses the best language file for <paramref name="wantedCode"/>.
        /// Lookup order (so a hand-made file always wins over the built-in alias table):
        ///   1. file name == the raw setting value   ("ch" -> ch.json)
        ///   2. file name == the normalized code     ("ch" -> zh-CN.json)
        ///   3. the file's declared code matches either of the two
        ///   4. en.json
        /// Returns null when the i18n folder is missing/empty/unreadable.
        /// </summary>
        private static LanguageFile Load(string wantedCode, string rawValue)
        {
            string directory = LanguageDirectory;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return null;

            string[] paths;
            try
            {
                paths = Directory.GetFiles(directory, "*.json");
            }
            catch
            {
                return null;
            }

            if (paths == null || paths.Length == 0)
                return null;

            var files = new List<LanguageFile>();
            for (int i = 0; i < paths.Length; i++)
            {
                LanguageFile parsed = ParseLanguageFile(paths[i]);
                if (parsed != null)
                    files.Add(parsed);
            }

            if (files.Count == 0)
                return null;

            List<string> candidates = BuildCandidates(wantedCode, rawValue);

            for (int i = 0; i < candidates.Count; i++)
            {
                LanguageFile hit = files.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f.FilePath), candidates[i], StringComparison.OrdinalIgnoreCase));
                if (hit != null)
                    return hit;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                LanguageFile hit = files.FirstOrDefault(f => string.Equals(f.Code, candidates[i], StringComparison.OrdinalIgnoreCase));
                if (hit != null)
                    return hit;
            }

            return files.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f.FilePath), FallbackCode, StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault(f => string.Equals(f.Code, FallbackCode, StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> BuildCandidates(string wantedCode, string rawValue)
        {
            var list = new List<string>();
            AddCandidate(list, rawValue);
            AddCandidate(list, wantedCode);
            AddCandidate(list, FallbackCode);
            return list;
        }

        private static void AddCandidate(List<string> list, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            string trimmed = value.Trim();
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], trimmed, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            list.Add(trimmed);
        }

        /// <summary>
        /// Parses one language file. Newtonsoft.Json types appear only as locals here, so the
        /// NoInlining attribute is enough to keep every Newtonsoft reference inside a wrapper
        /// (same containment rule the Json bridge in PerScopeMeshSurgerySettings follows).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static LanguageFile ParseLanguageFile(string path)
        {
            try
            {
                JObject root = JObject.Parse(StripLineComments(File.ReadAllText(path, Encoding.UTF8)));

                var file = new LanguageFile { FilePath = path };
                file.Code = ReadString(root, "_languageCode", "language_code", "languageCode", "code");
                if (string.IsNullOrWhiteSpace(file.Code))
                    file.Code = Path.GetFileNameWithoutExtension(path);

                file.DisplayName = ReadString(root, "_language", "language_name", "languageName");
                if (string.IsNullOrWhiteSpace(file.DisplayName))
                    file.DisplayName = file.Code;

                JObject sections = root["sections"] as JObject;
                if (sections != null)
                {
                    foreach (JProperty property in sections.Properties())
                    {
                        if (property.Name.StartsWith("_", StringComparison.Ordinal))
                            continue;
                        if (property.Value == null || property.Value.Type == JTokenType.Null)
                            continue;

                        string header = property.Value.ToString();
                        file.SectionOrder.Add(property.Name);
                        file.SectionHeaders[property.Name] = string.IsNullOrWhiteSpace(header) ? property.Name : header;
                    }
                }

                JObject settings = root["settings"] as JObject;
                if (settings != null)
                {
                    foreach (JProperty sectionProperty in settings.Properties())
                    {
                        if (sectionProperty.Name.StartsWith("_", StringComparison.Ordinal))
                            continue;

                        JObject group = sectionProperty.Value as JObject;
                        if (group == null)
                            continue;

                        var map = new Dictionary<string, LanguageEntry>(KeyComparer);
                        foreach (JProperty keyProperty in group.Properties())
                        {
                            var entry = new LanguageEntry();
                            JObject body = keyProperty.Value as JObject;
                            if (body != null)
                            {
                                entry.Name = ReadString(body, "name", "displayName", "display_name", "dispName");
                                entry.Description = ReadString(body, "description", "desc", "tooltip");
                            }
                            else if (keyProperty.Value != null && keyProperty.Value.Type == JTokenType.String)
                            {
                                entry.Name = keyProperty.Value.ToString();
                            }

                            map[keyProperty.Name] = entry;
                        }

                        file.Settings[sectionProperty.Name] = map;
                    }
                }

                return file;
            }
            catch (Exception ex)
            {
                LogWarningOnce(path, "[I18n] ignoring '" + path + "': " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Reads the first present, non-empty string property out of a language-file object.
        /// JObject appears in the signature, so this counts as a Newtonsoft user too and is kept
        /// NoInlining for the same reason as <see cref="ParseLanguageFile"/> - its only caller is
        /// that wrapper, so a resolution failure is still raised inside a try/catch.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadString(JObject obj, params string[] names)
        {
            if (obj == null || names == null)
                return null;

            for (int i = 0; i < names.Length; i++)
            {
                JToken token = obj[names[i]];
                if (token == null || token.Type == JTokenType.Null)
                    continue;

                string value = token.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        /// <summary>
        /// Drops whole-line // comments so a translator can annotate a language file.
        /// Same convention as the ConfigurationManager zh-cn localization plugin.
        /// </summary>
        private static string StripLineComments(string json)
        {
            if (string.IsNullOrEmpty(json) || json.IndexOf("//", StringComparison.Ordinal) < 0)
                return json;

            string[] lines = json.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var kept = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    kept.Add(lines[i]);
            }

            return string.Join("\n", kept);
        }

        // ===================================================================================
        // language codes
        // ===================================================================================

        /// <summary>True for the "follow the game" sentinel (also treated as: empty / unset).</summary>
        internal static bool IsAuto(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;
            string trimmed = value.Trim();
            return string.Equals(trimmed, AutoValue, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "\u81ea\u52a8", StringComparison.Ordinal); // 自动
        }

        /// <summary>
        /// Maps whatever the setting / the game reports onto the language-file code used on disk.
        /// Unknown values are passed through unchanged so that "any language file code" works.
        /// </summary>
        internal static string NormalizeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return FallbackCode;

            string key = value.Trim();
            string mapped;
            return Aliases.TryGetValue(key, out mapped) ? mapped : key;
        }

        // EFT's own locale codes first ("ch" = Chinese, "ge" = German, "jp" = Japanese,
        // "kr" = Korean - see LocalizationManager in Assembly-CSharp), then the usual extras.
        private static readonly Dictionary<string, string> Aliases = BuildAliases();

        private static Dictionary<string, string> BuildAliases()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            Add(map, "en", "en", "eng", "english", "en-us", "en-gb");
            Add(map, "zh-CN", "zh-cn", "zh_cn", "zh-hans", "zh-hans-cn", "zh", "cn", "chs", "ch", "chinese",
                "\u4e2d\u6587", "\u7b80\u4f53\u4e2d\u6587", "\u4e2d\u6587\uff08\u7b80\u4f53\uff09");
            Add(map, "zh-TW", "zh-tw", "zh_tw", "zh-hant", "cht", "tw", "\u7e41\u4f53\u4e2d\u6587", "\u7e41\u9ad4\u4e2d\u6587");
            Add(map, "ru", "ru", "rus", "russian", "\u0440\u0443\u0441\u0441\u043a\u0438\u0439");
            Add(map, "de", "de", "ge", "ger", "german", "deutsch");
            Add(map, "fr", "fr", "fra", "fre", "french", "fran\u00e7ais");
            Add(map, "ja", "ja", "jp", "jpn", "japanese", "\u65e5\u672c\u8a9e");
            Add(map, "ko", "ko", "kr", "kor", "korean", "\ud55c\uad6d\uc5b4");
            Add(map, "es", "es", "sp", "spanish", "espa\u00f1ol");
            Add(map, "it", "it", "ita", "italian", "italiano");
            Add(map, "pt", "pt", "pt-br", "pt_br", "br", "portuguese", "portugu\u00eas");
            Add(map, "pl", "pl", "pol", "polish", "polski");
            Add(map, "tr", "tr", "tur", "turkish", "t\u00fcrk\u00e7e");
            Add(map, "cs", "cs", "cz", "cze", "czech", "\u010de\u0161tina");
            Add(map, "th", "th", "tha", "thai");
            Add(map, "vi", "vi", "vietnamese", "ti\u1ebfng vi\u1ec7t");
            Add(map, "uk", "uk", "ua", "ukrainian", "\u0443\u043a\u0440\u0430\u0457\u043d\u0441\u044c\u043a\u0430");
            Add(map, "hu", "hu", "hun", "hungarian", "magyar");
            Add(map, "nl", "nl", "dut", "dutch", "nederlands");
            Add(map, "ro", "ro", "romanian", "rom\u00e2n\u0103");
            Add(map, "sv", "sv", "swe", "swedish", "svenska");
            Add(map, "da", "da", "dan", "danish", "dansk");
            Add(map, "fi", "fi", "fin", "finnish", "suomi");
            Add(map, "no", "no", "nor", "norwegian", "norsk");
            Add(map, "el", "el", "gre", "greek", "\u03b5\u03bb\u03bb\u03b7\u03bd\u03b9\u03ba\u03ac");
            Add(map, "bg", "bg", "bul", "bulgarian", "\u0431\u044a\u043b\u0433\u0430\u0440\u0441\u043a\u0438");

            return map;
        }

        private static void Add(Dictionary<string, string> map, string code, params string[] aliases)
        {
            map[code] = code;
            for (int i = 0; i < aliases.Length; i++)
            {
                if (!string.IsNullOrEmpty(aliases[i]))
                    map[aliases[i]] = code;
            }
        }

        // ===================================================================================
        // logging helpers
        // ===================================================================================

        private static void Log(string message)
        {
            var log = PiPDisablerPlugin.LogSource;
            if (log != null)
                log.LogInfo(message);
            else
                Console.WriteLine(message);
        }

        private static void LogWarning(string message)
        {
            var log = PiPDisablerPlugin.LogSource;
            if (log != null)
                log.LogWarning(message);
            else
                Console.WriteLine(message);
        }

        private static void LogWarningOnce(string key, string message)
        {
            if (!WarnedPaths.Add(key))
                return;
            LogWarning(message);
        }

        // ===================================================================================
        // data holders
        // ===================================================================================

        private sealed class LanguageEntry
        {
            internal string Name;
            internal string Description;
        }

        private sealed class LanguageFile
        {
            internal string Code;
            internal string DisplayName;
            internal string FilePath;
            internal readonly List<string> SectionOrder = new List<string>();
            internal readonly Dictionary<string, string> SectionHeaders = new Dictionary<string, string>(KeyComparer);
            internal readonly Dictionary<string, Dictionary<string, LanguageEntry>> Settings =
                new Dictionary<string, Dictionary<string, LanguageEntry>>(KeyComparer);

            internal int CountEntries()
            {
                int count = 0;
                foreach (var pair in Settings)
                {
                    if (pair.Value != null)
                        count += pair.Value.Count;
                }

                return count;
            }

            /// <summary>Localized section header, or the raw cfg section name when untranslated.</summary>
            internal string Header(string rawSection)
            {
                if (string.IsNullOrEmpty(rawSection))
                    return rawSection;

                string header;
                return SectionHeaders.TryGetValue(rawSection, out header) && !string.IsNullOrWhiteSpace(header) ? header : rawSection;
            }

            /// <summary>Text for one cfg entry; null when this file does not translate it.</summary>
            internal LanguageEntry Lookup(string section, string key)
            {
                if (string.IsNullOrEmpty(key))
                    return null;

                Dictionary<string, LanguageEntry> map;
                if (!string.IsNullOrEmpty(section) && Settings.TryGetValue(section, out map))
                {
                    LanguageEntry hit;
                    return map.TryGetValue(key, out hit) ? hit : null;
                }

                // The file has no such section (renamed or removed upstream). Fall back to a
                // deterministic cross-section scan so the entry still gets localized.
                for (int i = 0; i < SectionOrder.Count; i++)
                {
                    if (Settings.TryGetValue(SectionOrder[i], out map))
                    {
                        LanguageEntry hit;
                        if (map.TryGetValue(key, out hit))
                            return hit;
                    }
                }

                return null;
            }
        }
    }
}
