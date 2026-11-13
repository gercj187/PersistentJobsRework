using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace PersistentJobsRework
{
    public static class Main
    {
        public static UnityModManager.ModEntry Mod;
        public static Settings Settings;

        /// <summary>Mod in UMM aktiviert.</summary>
        public static bool Enabled;

        /// <summary>Banjobenis PersistentJobsMod ist ebenfalls aktiv - dann bleiben wir komplett passiv.</summary>
        public static bool ConflictWithPersistentJobs;

        private static Harmony _harmony;
        private static bool _patched;
        private static readonly HashSet<string> _loggedOnce = new HashSet<string>();

        public static bool IsActive => Enabled && !ConflictWithPersistentJobs;

        /// <summary>Darf diese Instanz die Welt veraendern? (Singleplayer oder MP-Host, niemals MP-Client)</summary>
        public static bool IsAuthority => IsActive && Mp.CanModifyWorld;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;

            try
            {
                Settings = UnityModManager.ModSettings.Load<Settings>(modEntry) ?? new Settings();
            }
            catch (Exception ex)
            {
                Warning("Settings could not be loaded, using defaults: " + ex.Message);
                Settings = new Settings();
            }
            Settings.Sanitize();

            var pj = UnityModManager.FindMod("PersistentJobsMod");
            if (pj != null && pj.Enabled)
            {
                ConflictWithPersistentJobs = true;
                Error("PersistentJobsMod (Banjobeni) is enabled. Both mods must not run at the same time - " +
                      "Persistent Jobs Rework stays completely inactive. Disable one of them.");
            }

            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUpdate = OnUpdate;

            // Genau EIN Patch-Durchlauf pro Spielstart (kein PatchAll bei jedem Toggle).
            if (!_patched && !ConflictWithPersistentJobs)
            {
                _harmony = new Harmony(modEntry.Info.Id);
                Patches.ApplyAll(_harmony);
                _patched = true;
            }

            Runtime.Ensure();
            Log($"Loaded v{modEntry.Info.Version}. Multiplayer available: {Mp.Available}");
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;
            Log(value ? "Enabled" : "Disabled (all frozen cars will be woken up)");
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry) => Settings.Draw();

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Sanitize();
            Settings.Save(modEntry);
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            Mp.TryRegisterCompatibility(modEntry.Info.Id);
            Mp.Tick();
            try { JobRefill.Tick(); } catch (Exception ex) { Main.WarningOnce("refill_tick", "Job refill check failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------ logging

        public static void Log(string msg) => Mod?.Logger.Log(msg);
        public static void Warning(string msg) => Mod?.Logger.Warning(msg);
        public static void Error(string msg) => Mod?.Logger.Error(msg);

        /// <summary>Info nur einmal pro Schluessel schreiben.</summary>
        public static void LogOnce(string key, string msg)
        {
            if (_loggedOnce.Add(key)) Log(msg);
        }

        /// <summary>Gleiche Meldung nur einmal pro Sitzung schreiben (verhindert Log-Spam).</summary>
        public static void WarningOnce(string key, string msg)
        {
            if (_loggedOnce.Add(key)) Warning(msg);
        }
    }

    /// <summary>Haelt die Laufzeit-Komponenten (ein GameObject fuer die ganze Sitzung).</summary>
    internal static class Runtime
    {
        private static GameObject _go;
        public static CarFreezer Freezer;
        public static WorldSpawner Spawner;
        public static DebugHud Hud;

        public static void Ensure()
        {
            if (_go != null) return;
            _go = new GameObject("PersistentJobsRework_Runtime");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            Freezer = _go.AddComponent<CarFreezer>();
            Spawner = _go.AddComponent<WorldSpawner>();
            Hud = _go.AddComponent<DebugHud>();
            _go.AddComponent<TooltipOverlay>();
        }
    }

    /// <summary>Zaehler fuer HUD und Log.</summary>
    internal static class Stats
    {
        public static int DeleteChecksBlocked;
        public static int InstantDeletesBlocked;
        public static int CarsDeleted;
        public static int GenerationsBlocked;
        public static int ExpiriesBlocked;
        public static int OptimizerFramesSkipped;
        public static int ReservationsDeferred;
        public static long BrakeTicksSkipped;
    }
}
