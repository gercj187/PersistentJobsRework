using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.Logic.Job;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// Einmalige Initialisierung pro Spielstand. Im Spielstand steht "Initialized": true, sobald der Weltspawn
    /// einmal komplett durchgelaufen ist. Fehlt der Merker (neue Karriere, alter Spielstand ohne diese Mod oder
    /// mit Banjobenis PersistentJobs), laeuft der Weltspawn nach dem Laden genau einmal.
    ///
    /// Debug: Merker zuruecksetzen (alles noch einmal auffuellen) und harter Neuaufbau (alle abgestellten Wagen
    /// ohne Lok loeschen, alle Jobs entfernen, Welt komplett neu fuellen).
    /// </summary>
    internal static class WorldInit
    {
        public const string FlagKey = "Initialized";

        /// <summary>Weltspawn fuer diesen Spielstand bereits erledigt.</summary>
        public static bool Initialized { get; private set; }

        /// <summary>Kurzzeitig gesetzt, damit unsere "Jobs verfallen nicht"-Sperre den harten Neuaufbau nicht blockiert.</summary>
        public static bool AllowExpire;

        public static bool HardResetRunning { get; private set; }

        /// <summary>Nach dem harten Neuaufbau: bis zum naechsten Laden erzeugt kein Bahnhof Wagen.</summary>
        public static bool ResetPending { get; private set; }

        /// <summary>Zaehlt hoch bei jeder neuen Karriere / jedem geladenen Spielstand. Ein laufender Weltspawn bricht ab, wenn sie sich aendert.</summary>
        public static int Session { get; private set; }
        private static bool _careerJustStarted;

        // ------------------------------------------------------------------ Laden / Speichern / neue Karriere

        public static void OnNewCareer()
        {
            InitialSpawnRules.RestoreAll();
            Session++;
            ResetPending = false;
            _careerJustStarted = true;   // das folgende Laden gehoert noch zu dieser Karriere
            Initialized = false;
            Main.Log("New career - world initialization pending.");
            StartIfNeeded("new career");
        }

        /// <summary>Nach dem Laden der Wagen: Merker aus dem Spielstand lesen (null = nicht vorhanden).</summary>
        public static void OnLoaded(bool? flagInSave)
        {
            InitialSpawnRules.RestoreAll();
            if (!_careerJustStarted) Session++;
            _careerJustStarted = false;
            ResetPending = false;
            Initialized = flagInSave == true;
            if (Initialized)
            {
                Main.Log("Savegame already initialized by PersistentJobsRework - no world spawn.");
                return;
            }
            Main.Log("Savegame not initialized by PersistentJobsRework yet - world spawn runs once.");
            StartIfNeeded("savegame without flag");
        }

        /// <summary>Vom Weltspawn aufgerufen, wenn er komplett durchgelaufen ist.</summary>
        public static void MarkDone()
        {
            if (Initialized) return;
            Initialized = true;
            Main.Log("World initialization finished - flag set (saved with the next save).");
        }

        private static void StartIfNeeded(string reason)
        {
            if (!Main.IsAuthority || Initialized) return;
            if (!Main.Settings.InitializeWorldOncePerSave)
            {
                Main.Log($"World initialization ({reason}) is switched off in the settings.");
                return;
            }
            if (WorldSpawner.Running)
            {
                // ein alter Weltspawn (z. B. aus dem vorher gespielten Spielstand) laeuft noch: er bricht ab,
                // danach startet der neue automatisch
                WorldSpawner.RestartRequested = true;
                return;
            }
            Runtime.Spawner?.StartWorldSpawn(true);
        }

        // ------------------------------------------------------------------ Debug

        /// <summary>Merker und Bahnhofs-Merker zuruecksetzen und die Welt sofort noch einmal auffuellen.</summary>
        public static void ResetFlagAndRespawn()
        {
            if (!Main.IsAuthority) return;
            Initialized = false;
            SpawnRegistry.Clear();
            Main.Log("Debug: initialization flag and station spawn flags reset - world spawn starts now.");
            if (!WorldSpawner.Running) Runtime.Spawner?.StartWorldSpawn(false);
        }

        /// <summary>Harter Reset: alle Jobs entfernen, alle Garnituren ohne Lok loeschen, Merker zuruecksetzen (Neuaufbau erst nach Neuladen).</summary>
        public static void HardReset()
        {
            if (!Main.IsAuthority || HardResetRunning || WorldSpawner.Running || Runtime.Spawner == null) return;
            Runtime.Spawner.StartCoroutine(HardResetRoutine());
        }

        private static IEnumerator HardResetRoutine()
        {
            HardResetRunning = true;
            Main.Log("Debug HARD RESET: removing all jobs and all parked cars without a locomotive and resetting the savegame flags.");
            int jobsRemoved = 0, carsDeleted = 0;

            // 1) angenommene Jobs abbrechen
            try
            {
                var jm = typeof(JobsManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy)?.GetValue(null, null)
                         ?? UnityEngine.Object.FindObjectOfType(typeof(JobsManager));
                if (jm == null) throw new InvalidOperationException("JobsManager not found");
                var current = Reflect.GetAny<IEnumerable>(jm, "currentJobs", "CurrentJobs")?.OfType<Job>().ToList() ?? new List<Job>();
                var abandon = typeof(JobsManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "AbandonJob" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(Job));
                if (abandon != null)
                    foreach (var job in current)
                    {
                        try
                        {
                            var ps = abandon.GetParameters();
                            var args = new object[ps.Length];
                            args[0] = job;
                            for (int i = 1; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                            abandon.Invoke(jm, args);
                            jobsRemoved++;
                        }
                        catch (Exception ex) { Main.Warning($"Hard reset: abandoning {job?.ID} failed: {ex.InnerException?.Message ?? ex.Message}"); }
                    }
                else if (current.Count > 0)
                    Main.Warning("Hard reset: JobsManager.AbandonJob not found - taken jobs stay.");
            }
            catch (Exception ex) { Main.Warning("Hard reset: abandoning jobs failed: " + ex.Message); }
            yield return null;

            // 2) angebotene Jobs aller Bahnhoefe verfallen lassen (gibt auch die Gleisreservierungen frei)
            AllowExpire = true;
            var expire = typeof(StationController).GetMethod("ExpireAllAvailableJobsInStation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            try
            {
                foreach (var sc in StationController.allStations ?? new List<StationController>())
                {
                    if (sc == null) continue;
                    int before = sc.logicStation?.availableJobs?.Count ?? 0;
                    try { expire?.Invoke(sc, null); jobsRemoved += before; }
                    catch (Exception ex) { Main.Warning($"Hard reset: expiring jobs at {sc.logicStation?.ID} failed: {ex.InnerException?.Message ?? ex.Message}"); }
                }
            }
            finally { AllowExpire = false; }
            for (int i = 0; i < 10; i++) yield return null;

            // 3) alle Garnituren ohne Lok/Tender/Begleitwagen loeschen (Zuege der Spieler bleiben stehen)
            try
            {
                var spawner = CarSpawner.Instance;
                var cars = spawner.AllCars.Where(c => c != null).ToList();
                var sets = cars.GroupBy(c => (object)c.trainset ?? c).ToList();
                var toDelete = new List<TrainCar>();
                foreach (var g in sets)
                {
                    var list = g.ToList();
                    bool keep = list.Any(IsProtected);
                    if (!keep) toDelete.AddRange(list);
                }
                if (toDelete.Count > 0)
                {
                    spawner.DeleteTrainCars(toDelete, false); // nicht sofort zerstoeren - sonst NullReferences im TrainsOptimizer
                    carsDeleted = toDelete.Count;
                }
            }
            catch (Exception ex) { Main.Warning("Hard reset: deleting cars failed: " + ex.Message); }
            for (int i = 0; i < 30; i++) yield return null;

            // 4) Spielstand zuruecksetzen, als waere diese Mod nie gelaufen - KEIN Neuaufbau in dieser Sitzung.
            //    Nach Speichern + Neuladen startet die Initialisierung von selbst (Merker fehlt / ist false).
            Reservations.Reset();
            AlternativeTracks.Reset();
            SpawnRegistry.Clear();
            Initialized = false;
            ResetPending = true;
            Main.Log($"Debug HARD RESET: {jobsRemoved} job(s) removed, {carsDeleted} car(s) deleted, savegame flags reset. " +
                     "No new cars until the savegame is saved and loaded again.");
            DebugHud.Notify(Loc.T("n_hard_done"));
            HardResetRunning = false;
        }

        /// <summary>Loks, Tender und Begleitwagen (Caboose) - und damit ihre ganze Garnitur - bleiben erhalten.</summary>
        private static bool IsProtected(TrainCar car)
        {
            if (car == null) return false;
            if (car.IsLoco) return true;
            string n = car.name ?? "";
            // Lok- und Tender-Prefabs heissen "Loco..." (z. B. LocoS282B = Tender) - auch Restaurations-Wracks
            return n.StartsWith("Loco", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf("Tender", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Caboose", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
