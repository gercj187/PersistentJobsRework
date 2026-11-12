using System;
using System.Reflection;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// Uebersetzungen in alle 26 Spielsprachen (Tabelle in LocTable.cs).
    /// Sprache = Spielsprache (einmal beim ersten Gebrauch aus der I2 Localization des Spiels gelesen), optional in den Einstellungen fest waehlbar.
    /// Fehlt ein Text, wird Englisch verwendet. Log-Ausgaben bleiben absichtlich Englisch.
    /// </summary>
    internal static partial class Loc
    {
        private static int _index = 0;          // 0 = Englisch
        private static float _nextCheck;
        private static PropertyInfo _codeProp, _nameProp;
        private static bool _lookedUp;
        private static string _lastSeen;
        private static string _lastForced = "";

        /// <summary>Uebersetzter Text zum Schluessel.</summary>
        public static string T(string key)
        {
            Refresh();
            if (key == null || !Table.TryGetValue(key, out var arr)) return key ?? "";
            var s = _index < arr.Length ? arr[_index] : null;
            return string.IsNullOrEmpty(s) ? arr[0] : s;
        }

        /// <summary>Uebersetzter Text mit Platzhaltern {0}, {1} ...</summary>
        public static string F(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch { return T(key); }
        }

        public static string CurrentCode => Codes[_index];

        private static bool _resolved;      // Sprache steht fest - keine weiteren Abfragen
        private static int _gameIndex = -1; // einmal erkannte Spielsprache

        /// <summary>
        /// Sprache festlegen: einmal die Spielsprache abfragen und merken. Danach wird nur noch neu entschieden,
        /// wenn in den Einstellungen eine andere Sprache gewaehlt wird. Kein regelmaessiges Abfragen.
        /// </summary>
        private static void Refresh()
        {
            string forced = Main.Settings?.Language ?? "";
            if (_resolved && forced == _lastForced) return;

            // Spielsprache noch unbekannt (z. B. ganz frueh beim Start): hoechstens alle 5 s erneut versuchen, bis es klappt
            if (_gameIndex < 0 && string.IsNullOrEmpty(forced))
            {
                if (Time.realtimeSinceStartup < _nextCheck) return;
                _nextCheck = Time.realtimeSinceStartup + 5f;
            }
            _lastForced = forced;

            if (_gameIndex < 0 && string.IsNullOrEmpty(forced)) _gameIndex = Detect();

            int idx = !string.IsNullOrEmpty(forced) ? Array.IndexOf(Codes, forced) : _gameIndex;
            _resolved = !string.IsNullOrEmpty(forced) ? idx >= 0 : _gameIndex >= 0;
            if (idx < 0) idx = 0;
            if (idx != _index)
            {
                _index = idx;
                Main.Log($"Language: {NativeNames[_index]} ({Codes[_index]}).");
            }
        }

        /// <summary>Spielsprache ueber I2.Loc.LocalizationManager (CurrentLanguageCode / CurrentLanguage).</summary>
        private static int Detect()
        {
            try
            {
                if (!_lookedUp)
                {
                    _lookedUp = true;
                    Type lm = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        lm = asm.GetType("I2.Loc.LocalizationManager", false);
                        if (lm != null) break;
                    }
                    if (lm != null)
                    {
                        _codeProp = lm.GetProperty("CurrentLanguageCode", BindingFlags.Static | BindingFlags.Public);
                        _nameProp = lm.GetProperty("CurrentLanguage", BindingFlags.Static | BindingFlags.Public);
                    }
                    else Main.Log("Language: game localization not found - using English (can be chosen in the settings).");
                }

                string code = _codeProp?.GetValue(null, null) as string;
                string name = _nameProp?.GetValue(null, null) as string;
                string seen = code + "|" + name;
                if (seen != _lastSeen)
                {
                    _lastSeen = seen;
                    Main.Log($"Language: game reports code '{code}', name '{name}'.");
                }
                int i = FromCode(code);
                return i >= 0 ? i : FromName(name);
            }
            catch
            {
                return -1;
            }
        }

        internal static int FromCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return -1;
            string c = code.Trim().Replace('_', '-').ToLowerInvariant();
            switch (c)
            {
                case "zh": case "zh-cn": case "zh-hans": case "zh-sg": return Array.IndexOf(Codes, "zh-CN");
                case "zh-tw": case "zh-hant": case "zh-hk": return Array.IndexOf(Codes, "zh-TW");
                case "pt-br": return Array.IndexOf(Codes, "pt-BR");
                case "pt": case "pt-pt": return Array.IndexOf(Codes, "pt-PT");
                case "nb": case "nn": case "no": case "nb-no": case "nn-no": return Array.IndexOf(Codes, "no");
            }
            for (int i = 0; i < Codes.Length; i++)
                if (string.Equals(Codes[i], c, StringComparison.OrdinalIgnoreCase)) return i;
            string two = c.Length >= 2 ? c.Substring(0, 2) : c;
            return Array.IndexOf(Codes, two);
        }

        private static int FromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            string n = name.ToLowerInvariant();
            if (n.Contains("brazil") || n.Contains("brasil")) return Array.IndexOf(Codes, "pt-BR");
            if (n.Contains("portug")) return Array.IndexOf(Codes, "pt-PT");
            if (n.Contains("tradition") || n.Contains("taiwan") || n.Contains("正體") || n.Contains("繁")) return Array.IndexOf(Codes, "zh-TW");
            if (n.Contains("chinese") || n.Contains("中文") || n.Contains("简")) return Array.IndexOf(Codes, "zh-CN");
            string[][] map =
            {
                new[] { "bg", "bulgar", "бълг" }, new[] { "cs", "czech", "češ" }, new[] { "da", "danish", "dansk" },
                new[] { "nl", "dutch", "neder" }, new[] { "fi", "finn", "suomi" }, new[] { "fr", "french", "fran" },
                new[] { "de", "german", "deutsch" }, new[] { "hi", "hindi", "हिन्दी" }, new[] { "hu", "hungar", "magyar" },
                new[] { "it", "italian", "italiano" }, new[] { "ja", "japan", "日本" }, new[] { "ko", "korean", "한국" },
                new[] { "no", "norweg", "norsk" }, new[] { "pl", "polish", "polski" }, new[] { "ro", "romanian", "român" },
                new[] { "ru", "russian", "русск" }, new[] { "sk", "slovak", "sloven" }, new[] { "es", "spanish", "españ" },
                new[] { "sv", "swedish", "svensk" }, new[] { "tr", "turkish", "türk" }, new[] { "uk", "ukrain", "україн" },
                new[] { "en", "english" }
            };
            foreach (var m in map)
                for (int j = 1; j < m.Length; j++)
                    if (n.Contains(m[j])) return Array.IndexOf(Codes, m[0]);
            return -1;
        }
    }
}
