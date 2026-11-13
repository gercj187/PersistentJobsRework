using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// DEBUG / Performance-Test: laesst ALLE Bahnhoefe einmalig ihre (Vanilla-)Jobs mit Wagen generieren.
    /// Gleiche Idee wie in PersistentLocos: der Bahnhof, der gerade generiert, gilt kurz als "Spieler in Reichweite".
    /// </summary>
    internal sealed class WorldSpawner : MonoBehaviour
    {
        /// <summary>Bahnhof, dessen Spieler-Entfernung gerade auf 0 gezwungen wird (siehe Patches.ForcedStationDistance_Prefix).</summary>
        public static StationJobGenerationRange ForcedRange;

        public static bool Running { get; private set; }
        public static int Pass { get; private set; }

        /// <summary>Waehrend ein alter Weltspawn noch lief, wurde ein neuer angefordert (neue Karriere / anderer Spielstand).</summary>
        public static bool RestartRequested;
        public static int Passes { get; private set; }

        /// <summary>Bahnhof, der im Zusatz-Durchlauf trotz Einmal-Spawn-Merker generieren darf.</summary>
        public static string ExtraPassStation;

        /// <summary>
        /// Yard-ID des Bahnhofs, dessen Generierung der Weltspawn gerade selbst ausgeloest hat. Nur fuer diese gilt
        /// "max. N Ketten pro Durchlauf" - eine normale Generierung (z. B. Startbahnhof beim Laden) nie.
        /// </summary>
        public static string SpawningYard;
        public static int StationsDone { get; private set; }
        public static int StationsTotal { get; private set; }

        /// <summary>Fortschritt pro Bahnhof fuers Overlay: 0 = ausstehend (gelb), 1 = Wagen gespawnt (gruen), 2 = nichts (rot), 3 = uebersprungen (grau).</summary>
        public static readonly List<KeyValuePair<string, int>> Progress = new List<KeyValuePair<string, int>>();
        public static float FinishedAt = -1000f;

        /// <summary>Overlay: neue Jobketten pro Bahnhof und Durchlauf (-1 = uebersprungen) und aktuelle Gesamtzahl.</summary>
        public static readonly List<string> StationOrder = new List<string>();
        public static readonly Dictionary<string, List<int>> PassChains = new Dictionary<string, List<int>>();
        public static readonly Dictionary<string, int> Totals = new Dictionary<string, int>();

        private static void Record(string id, int added, StationProceduralJobsController controller)
        {
            if (!PassChains.TryGetValue(id, out var list)) PassChains[id] = list = new List<int>();
            list.Add(added);
            int prev = Totals.TryGetValue(id, out int p) ? p : 0;
            int counted = prev + Math.Max(0, added);           // die Liste des Spiels hinkt ein paar Frames hinterher
            Totals[id] = controller != null ? Math.Max(CountChains(controller), counted) : counted;
        }

        private static void SetProgress(string id, int state)
        {
            for (int i = 0; i < Progress.Count; i++)
                if (Progress[i].Key == id) { Progress[i] = new KeyValuePair<string, int>(id, state); return; }
            Progress.Add(new KeyValuePair<string, int>(id, state));
        }

        public void StartWorldSpawn(bool waitForWorldLoad)
        {
            if (!Main.IsAuthority)
            {
                Main.Warning("World spawn is only possible in singleplayer or as multiplayer host.");
                return;
            }
            if (Running)
            {
                Main.Warning("World spawn is already running.");
                return;
            }
            StartCoroutine(Run(waitForWorldLoad));
        }

        private IEnumerator Run(bool waitForWorldLoad)
        {
            Running = true;
            int session = WorldInit.Session;
            bool Lost() => WorldInit.Session != session || !WorldStreamingInit.IsLoaded || PlayerManager.PlayerTransform == null;
            StationsDone = 0;
            StationsTotal = 0;
            Main.Log(waitForWorldLoad ? "World spawn scheduled - waiting until the world is fully loaded ..." : "World spawn started.");

            try
            {
                if (waitForWorldLoad)
                {
                    float waitStart = Time.realtimeSinceStartup;
                    yield return new WaitUntil(() =>
                        (WorldStreamingInit.IsLoaded && PlayerManager.PlayerTransform != null && AStartGameData.carsAndJobsLoadingFinished)
                        || Time.realtimeSinceStartup - waitStart > 600f);
                    yield return new WaitForSecondsRealtime(5f);
                }

                var stations = StationController.allStations?.Where(s => s != null).ToList();
                if (stations == null || stations.Count == 0)
                {
                    Main.Warning("World spawn: no stations found.");
                    yield break;
                }

                // zuerst die Prioritaetsliste aus den Einstellungen (z. B. SM,HB,MF,FF,GF,CW), dann der Rest nach Entfernung
                var priority = (Main.Settings.PriorityStations ?? "")
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToUpperInvariant())
                    .ToList();
                var playerPos = PlayerManager.PlayerTransform != null ? PlayerManager.PlayerTransform.position : Vector3.zero;
                stations = stations
                    .OrderBy(s =>
                    {
                        int i = priority.IndexOf((s.logicStation?.ID ?? "").ToUpperInvariant());
                        return i < 0 ? int.MaxValue : i;
                    })
                    .ThenBy(s => (s.transform.position - playerPos).sqrMagnitude)
                    .ToList();
                Main.Log("World spawn order: " + string.Join(", ", stations.Select(s => s.logicStation?.ID ?? s.name)));
                StationsTotal = stations.Count;
                StationOrder.Clear(); PassChains.Clear(); Totals.Clear();
                foreach (var st in stations)
                {
                    string sid = st.logicStation?.ID ?? st.name;
                    StationOrder.Add(sid);
                    if (st.ProceduralJobsController != null) Totals[sid] = CountChains(st.ProceduralJobsController);
                }
                int carsAtStart = CountCars();
                var total = System.Diagnostics.Stopwatch.StartNew();
                var participants = new HashSet<string>();   // Bahnhoefe des 1. Durchlaufs (auch schon vorher gespawnte) bekommen die weiteren Durchlaeufe
                // Reihum-Modus: pro Durchlauf bekommt jeder Bahnhof nur 1-2 neue Ketten, so fuellt sich die Welt
                // gleichmaessig und kein Bahnhof belegt vorab alle Eingangsgleise der anderen.
                int perPass = Main.Settings.JobsPerPass;
                int lim0 = Main.Settings.MaxJobsPerStation;
                int regular = perPass > 0 && lim0 > 0 ? (lim0 + perPass - 1) / perPass : 1;
                Passes = regular + Math.Max(0, Main.Settings.ExtraWorldSpawnPasses);
                Main.Log(perPass > 0
                    ? $"World spawn round-robin: max. {perPass} new job chain(s) per station and pass, {regular} regular + {Passes - regular} extra pass(es)."
                    : $"World spawn: all jobs at once, {Passes - 1} extra pass(es).");

                // Mindestbestand ZUERST: seltene Wagentypen (z. B. CCH) bekommen ihren Platz auf den S-Gleisen,
                // bevor die normalen Durchlaeufe die Gleise fuellen. Am Ende wird noch einmal nachgeprueft.
                if (Main.Settings.MinCarsPerType > 0 && !Lost())
                {
                    var first = MinStock.Fill(Main.Settings.MinCarsPerType);
                    while (first.MoveNext()) yield return first.Current;
                }

                for (Pass = 1; Pass <= Passes; Pass++)
                {
                    bool extra = Pass > 1;
                    if (extra && participants.Count == 0) break;
                    if (Lost())
                    {
                        Main.Log("World spawn aborted - the game was unloaded or another savegame was started.");
                        yield break;
                    }
                    StationsDone = 0;
                    Progress.Clear();
                    foreach (var st in stations) Progress.Add(new KeyValuePair<string, int>(st.logicStation?.ID ?? st.name, 0));
                    int carsAtPass = CountCars();
                    int chainsAtPass = participants.Count == 0 ? -1 : TotalChains(stations);
                    if (extra) Main.Log($"World spawn: pass {Pass}/{Passes} for stations below the job limit.");

                    foreach (var station in stations)
                    {
                        if (Lost() || station == null)
                        {
                            Main.Log("World spawn aborted - the game was unloaded or another savegame was started.");
                            yield break;
                        }
                        StationsDone++;
                        string id = station.logicStation?.ID ?? station.name;
                        var controller = station.ProceduralJobsController;

                        if (!extra && Main.Settings.OneTimeStationSpawn && SpawnRegistry.HasSpawned(id))
                        {
                            Main.Log($"World spawn: {id} already spawned before - skipped.");
                            SetProgress(id, 3); // grau: uebersprungen, nichts Neues
                            Record(id, -1, controller);
                            participants.Add(id); // z. B. Startbahnhof: ab Durchlauf 2 bis zum Limit auffuellen
                            continue;
                        }
                        if (extra && !participants.Contains(id)) { SetProgress(id, 3); Record(id, -1, controller); continue; }

                        if (controller == null)
                        {
                            Main.Log($"World spawn: {id} has no job controller - skipped.");
                            SetProgress(id, 2);
                            Record(id, -1, null);
                            continue;
                        }

                        int lim = Main.Settings.MaxJobsPerStation;
                        if (extra && lim > 0 && CountChains(controller) >= lim)
                        {
                            SetProgress(id, 3); // Limit erreicht
                            Record(id, -1, controller);
                            continue;
                        }
                        if (!extra) participants.Add(id);

                        while (CarSpawner.Instance != null && CarSpawner.Instance.PoolSetupInProgress)
                            yield return null;

                        int carsBefore = CountCars();
                        string yardId = station.stationInfo?.YardID;
                        int chainsBefore = Capacity.TotalGeneratedAt(yardId);
                        // StationController.stationRange ist privat -> Komponente direkt am Bahnhof suchen
                        ForcedRange = station.GetComponentInChildren<StationJobGenerationRange>(true)
                                      ?? station.GetComponentInParent<StationJobGenerationRange>();
                        if (ForcedRange == null)
                            Main.WarningOnce("no-range", "World spawn: StationJobGenerationRange not found - stations far away may not generate.");
                        ExtraPassStation = extra ? id : null; // ab Durchlauf 2: Einmal-Spawn-Sperre fuer diesen Bahnhof aufheben
                        SpawningYard = station.stationInfo?.YardID;
                        yield return null; // ein Frame, damit der Bahnhof die "Naehe" mitbekommt

                        try
                        {
                            controller.TryToGenerateJobs();
                        }
                        catch (Exception ex)
                        {
                            Main.Warning($"World spawn: generation at {id} failed: {ex.Message}");
                        }

                        yield return null;
                        float start = Time.realtimeSinceStartup;
                        while (controller.IsJobGenerationActive && Time.realtimeSinceStartup - start < 30f)
                        {
                            // Reihum: genug Ketten fuer diesen Durchlauf -> Generierung sofort beenden statt 30 Fehlversuche abzuwarten
                            if (perPass > 0 && Capacity.GeneratedThisRun(station.stationInfo?.YardID) >= perPass)
                            {
                                StopGeneration(controller);
                                break;
                            }
                            yield return null;
                        }

                        ForcedRange = null;
                        ExtraPassStation = null;
                        SpawningYard = null;
                        int added = CountCars() - carsBefore;
                        Main.Log($"World spawn{(extra ? $" pass {Pass}" : "")}: {id} ({StationsDone}/{StationsTotal}) +{added} cars");
                        SetProgress(id, added > 0 ? 1 : 2);
                        Record(id, Math.Max(0, Capacity.TotalGeneratedAt(yardId) - chainsBefore), controller);
                        yield return null;
                    }

                    int addedThisPass = CountCars() - carsAtPass;
                    int newChains = chainsAtPass < 0 ? -1 : TotalChains(stations) - chainsAtPass;
                    Main.Log($"World spawn: pass {Pass}/{Passes} added {addedThisPass} cars{(newChains >= 0 ? $", {newChains} job chain(s)" : "")}.");
                    if (extra && addedThisPass == 0 && !InitialSpawnRules.AnyShuntingAttemptsLeft)
                    {
                        Main.Log("World spawn: nothing more could be generated - remaining passes skipped.");
                        break;
                    }

                    // FPS-Grenze: nach jedem Durchlauf kurz beruhigen lassen, dann messen. Faellt die Bildrate unter
                    // die Grenze, kommen keine weiteren Durchlaeufe (Durchlauf 1 laeuft immer: jeder Bahnhof bekommt Wagen).
                    int minFps = Main.Settings.MinFpsWorldSpawn;
                    if (minFps > 0 && Pass < Passes)
                    {
                        float settleUntil = Time.realtimeSinceStartup + 3f;
                        while (Time.realtimeSinceStartup < settleUntil) yield return null;
                        int f0 = Time.frameCount;
                        float t0 = Time.realtimeSinceStartup;
                        while (Time.realtimeSinceStartup - t0 < 3f) yield return null;
                        float fps = (Time.frameCount - f0) / Mathf.Max(0.001f, Time.realtimeSinceStartup - t0);
                        if (Lost()) continue;   // oben im naechsten Durchlauf: Abbruch mit Log
                        if (fps < minFps)
                        {
                            Main.Log($"World spawn: {fps:0} FPS after pass {Pass} is below the limit of {minFps} FPS - remaining passes skipped ({CountCars()} cars).");
                            break;
                        }
                        Main.Log($"World spawn: {fps:0} FPS after pass {Pass} (limit {minFps}).");
                    }
                }

                // Mindestbestand je Wagentyp (vor dem Setzen des Merkers, Schadensschutz ist noch aktiv)
                if (Main.Settings.MinCarsPerType > 0 && !Lost())
                {
                    var fill = MinStock.Fill(Main.Settings.MinCarsPerType);
                    while (fill.MoveNext()) yield return fill.Current;
                }
                // Wagen aus dem Mindestbestand (leer auf S) bekommen jetzt ihre Beladejobs
                if (Main.Settings.MinCarsPerType > 0) JobRefill.Start();

                WorldInit.MarkDone();
                SpawnProtection.RepairParkedCars();
                Main.Log($"World spawn finished in {total.Elapsed.TotalSeconds:F1}s: {CountCars() - carsAtStart} new cars, {CountCars()} cars in total. " +
                         $"Freezing starts in {Main.Settings.FreezeDelayAfterWorldSpawn:F0}s.");
            }
            finally
            {
                ForcedRange = null;
                ExtraPassStation = null;
                SpawningYard = null;
                Running = false;
                FinishedAt = Time.realtimeSinceStartup;
                if (Main.Settings.AwakeDuringWorldSpawn)
                    CarFreezer.SuspendedUntil = Time.realtimeSinceStartup + Main.Settings.FreezeDelayAfterWorldSpawn;
                Runtime.Freezer?.RequestScan();

                if (RestartRequested && Main.IsAuthority)
                {
                    RestartRequested = false;
                    Main.Log("World spawn restarts for the new career / savegame.");
                    StartWorldSpawn(true);
                }
            }
        }

        /// <summary>
        /// Wartet, bis die erste Generierung eines Bahnhofs fertig ist. Sind dabei keine Wagen entstanden
        /// (z. B. "Couldn't generate any player licensed job"), wird der Einmal-Spawn-Merker wieder entfernt.
        /// </summary>
        public void VerifyFirstGeneration(StationProceduralJobsController controller, string stationId)
        {
            if (controller == null || stationId == null) return;
            StartCoroutine(VerifyRoutine(controller, stationId, CountChains(controller)));
        }

        private IEnumerator VerifyRoutine(StationProceduralJobsController controller, string stationId, int chainsBefore)
        {
            yield return null;
            float start = Time.realtimeSinceStartup;
            while (controller != null && controller.IsJobGenerationActive && Time.realtimeSinceStartup - start < 60f)
                yield return null;
            yield return null;

            // Jobketten DIESES Bahnhofs zaehlen (Wagenzahl ist unzuverlaessig, weil anderswo z. B. Loks spawnen)
            if (CountChains(controller) <= chainsBefore && SpawnRegistry.HasSpawned(stationId))
            {
                SpawnRegistry.Unmark(stationId);
                Main.Log($"Station {stationId}: no jobs were generated - station may spawn again later.");
                int lim = Main.Settings.MaxJobsPerStation;
                if (lim <= 0 || CountChains(controller) < lim)   // am Limit = gewollt leer, keine Diagnose
                    StationDiagnostics.DumpEmptyStation(controller, stationId);
            }
        }

        private static int CountChains(StationProceduralJobsController controller)
        {
            try { return controller?.GetCurrentJobChains()?.Count() ?? 0; }
            catch { return 0; }
        }

        private static int TotalChains(List<StationController> stations)
        {
            int n = 0;
            foreach (var st in stations) n += CountChains(st?.ProceduralJobsController);
            return n;
        }

        /// <summary>
        /// Beendet die laufende Vanilla-Generierung eines Bahnhofs sauber. Sie wartet nur zwischen zwei Versuchen
        /// (Jobkette ist dann fertig angelegt), danach wird das Coroutine-Feld geleert wie im Original am Ende.
        /// </summary>
        private static void StopGeneration(StationProceduralJobsController controller)
        {
            try
            {
                var coro = Reflect.Get<Coroutine>(controller, "generationCoro");
                if (coro != null) controller.StopCoroutine(coro);
                Reflect.Set(controller, "generationCoro", null);
            }
            catch (Exception ex)
            {
                Main.WarningOnce("stop-gen", "Could not stop job generation early: " + ex.Message);
            }
        }

        private static int CountCars()
        {
            try { return CarSpawner.Instance != null ? CarSpawner.Instance.AllCars.Count() : 0; }
            catch { return 0; }
        }
    }
}
