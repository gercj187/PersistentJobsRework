using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV;
using DV.Logic.Job;
using DV.Utils;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// Start-Auslastung: Waehrend ein Bahnhof seine Jobs erzeugt, duerfen nur X % seiner Gueter-Gleise (S/I/O)
    /// belegt werden - gezaehlt in GLEISEN, nicht in Laenge. Beispiel: 16 Gleise, 50 % -> hoechstens 8 Gleise
    /// bekommen Wagen, die anderen 8 bleiben leer fuer ankommende Zuege.
    ///
    /// Umsetzung: Ist die Grenze erreicht, meldet die Mod fuer jedes noch LEERE Gleis dieses Bahnhofs
    /// "kein Platz". Bereits belegte Gleise duerfen weiter aufgefuellt werden. Der Vanilla-Generator erzeugt
    /// dadurch von selbst weniger Jobs. Gilt nur fuer den Bahnhof, der gerade generiert, und nur waehrend der
    /// Generierung - Annahme, Ausweichen und Abgabe sehen immer alle Gleise.
    /// </summary>
    internal static class Capacity
    {
        private static readonly Dictionary<string, int> GeneratingYards = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Groesste erlaubte Wagenzahl laut Regelwerk der gerade generierenden Bahnhoefe (0 = unbekannt).</summary>
        public static int MaxCarsOfGeneratingStations()
        {
            int max = 0;
            foreach (var ctl in Controllers.Values)
            {
                if (ctl == null) continue;
                var rs = Reflect.Get<StationProceduralJobsRuleset>(ctl, "generationRuleset");
                if (rs != null && rs.maxCarsPerJob > max) max = rs.maxCarsPerJob;
            }
            return max;
        }

        /// <summary>Irgendein Bahnhof generiert gerade Jobs.</summary>
        public static bool IsGenerating => GeneratingYards.Count > 0;

        /// <summary>Gerade generierender Bahnhof?</summary>
        public static bool IsGeneratingYard(string yardId) => yardId != null && GeneratingYards.ContainsKey(yardId);

        public static bool Active => GeneratingYards.Count > 0 && Main.IsAuthority && Main.Settings.StationFillLimitPercent < 100f;

        // ------------------------------------------------------------------ Generierung verfolgen

        public static void BeginGeneration(StationProceduralJobsController controller)
        {
            string yard = controller?.stationController?.stationInfo?.YardID;
            if (yard == null || Runtime.Spawner == null) return;
            // Zweiter Aufruf waehrend die Generierung schon laeuft (z. B. das Spiel selbst, weil der Weltspawn den
            // Bahnhof gerade "in Reichweite" meldet): nichts zuruecksetzen, sonst zaehlen Limit/Durchlauf doppelt.
            if (GeneratingYards.ContainsKey(yard)) return;

            GeneratingYards[yard] = GeneratingYards.TryGetValue(yard, out int n) ? n + 1 : 1;
            bool roundRobin = Main.Settings.JobsPerPass > 0 && string.Equals(WorldSpawner.SpawningYard, yard, StringComparison.OrdinalIgnoreCase);
            if (roundRobin) RoundRobinRuns.Add(yard); else RoundRobinRuns.Remove(yard);
            Controllers[yard] = controller;
            BaseChains[yard] = ChainCount(controller);
            FinalizedChains[yard] = 0;
            GeneratorChains[yard] = 0;
            FailedAttempts[yard] = 0;
            _generatorRefusals = 0;
            _checks = 0; _blocks = 0; _spawnsBlocked = 0; _jobLimitHits = 0;
            if (Main.Settings.StationFillLimitPercent < 100f) LogUsage(yard, "before generation");
            Runtime.Spawner.StartCoroutine(EndWhenDone(controller, yard));
        }

        private static IEnumerator EndWhenDone(StationProceduralJobsController controller, string yard)
        {
            yield return null;
            float start = Time.realtimeSinceStartup;
            while (controller != null && controller.IsJobGenerationActive && Time.realtimeSinceStartup - start < 60f)
                yield return null;

            if (GeneratingYards.TryGetValue(yard, out int n))
            {
                if (n <= 1) { GeneratingYards.Remove(yard); Controllers.Remove(yard); BaseChains.Remove(yard); FinalizedChains.Remove(yard); GeneratorChains.Remove(yard); }
                else GeneratingYards[yard] = n - 1;
            }
            SpawnProtection.GenerationEnded();
            int chains = ChainCount(controller);
            int failed = FailedAttempts.TryGetValue(yard, out int fa) ? fa : 0;
            Main.Log($"Station {yard}: {chains} job chain(s) (limit {(Main.Settings.MaxJobsPerStation > 0 ? Main.Settings.MaxJobsPerStation.ToString() : "off")}), " +
                     $"{failed} failed generation attempt(s), {_generatorRefusals} attempt(s) and {_jobLimitHits} car spawn(s) refused by the limit.");
            if (chains > 0 && chains < 3 && !RoundRobinRuns.Contains(yard))   // reihum (1 pro Durchlauf) ist wenig gewollt
                StationDiagnostics.Dump(controller, yard, $"generated only {chains} job chain(s) after {failed} failed attempt(s)");
            if (Main.Settings.StationFillLimitPercent < 100f)
            {
                LogUsage(yard, "after generation");
                Main.Log($"Station {yard}: track limit checked {_checks}x, blocked {_blocks}x, car spawns refused {_spawnsBlocked}x.");
            }
        }

        // ------------------------------------------------------------------ Regel

        // ---- Puffer: die Abfragen kommen waehrend der Generierung sehr oft
        private static readonly Dictionary<string, List<Track>> FreightTrackCache = new Dictionary<string, List<Track>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashSet<Track>> OccupiedCache = new Dictionary<string, HashSet<Track>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> OccupiedCacheFrame = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static bool _occupiedDirty = true;

        private static List<Track> FreightTracks(string yardId)
        {
            if (FreightTrackCache.TryGetValue(yardId, out var cached)) return cached;
            var yard = StationController.GetStationByYardID(yardId)?.logicStation?.yard;
            if (yard == null) return null;
            var list = new[] { yard.StorageTracks, yard.TransferInTracks, yard.TransferOutTracks }
                .Where(l => l != null).SelectMany(l => l).Where(t => t != null).Distinct().ToList();
            FreightTrackCache[yardId] = list;
            return list;
        }

        /// <summary>Gleise eines Bahnhofs, auf denen gerade Wagen stehen. Hoechstens einmal pro Frame bzw. nach einem Spawn neu berechnet.</summary>
        private static HashSet<Track> OccupiedTracks(string yardId)
        {
            int frame = Time.frameCount;
            if (_occupiedDirty)
            {
                OccupiedCache.Clear();
                OccupiedCacheFrame.Clear();
                _occupiedDirty = false;
            }
            else if (OccupiedCache.TryGetValue(yardId, out var cached) &&
                     OccupiedCacheFrame.TryGetValue(yardId, out int f) && f == frame)
            {
                return cached;
            }

            var result = new HashSet<Track>();
            var spawner = CarSpawner.Instance;
            if (spawner != null)
            {
                foreach (var tc in spawner.AllCars)
                {
                    var car = tc != null ? tc.logicCar : null;
                    if (car == null) continue;
                    var front = car.FrontBogieTrack;
                    if (front != null && front.ID.yardId == yardId) result.Add(front);
                    var cur = car.CurrentTrack;
                    if (cur != null && cur != front && cur.ID.yardId == yardId) result.Add(cur);
                }
            }
            // Zielgleise angenommener Jobs zaehlen mit (die Wagen sind noch unterwegs)
            foreach (var claimed in TrackAssignment.ClaimedTracks(yardId)) result.Add(claimed);

            OccupiedCache[yardId] = result;
            OccupiedCacheFrame[yardId] = frame;
            return result;
        }

        /// <summary>
        /// Gleis gilt als belegt, wenn Wagen darauf stehen oder ein angenommener Gueter-/Leerwagen-Job dorthin faehrt.
        /// (Die Reservierungsabfrage des Spiels liefert auch fuer leere Gleise Werte und ist dafuer unbrauchbar.)
        /// </summary>
        private static bool IsUsed(Track t, HashSet<Track> occupied) => occupied.Contains(t);

        private static int Allowed(int total)
        {
            float pct = Mathf.Clamp(Main.Settings.StationFillLimitPercent, 10f, 100f);
            return Math.Max(1, (int)Math.Round(total * pct / 100.0, MidpointRounding.AwayFromZero));
        }

        /// <summary>Darf auf dieses Gleis gerade etwas Neues gestellt werden?</summary>
        private static bool IsBlocked(Track t)
        {
            if (t == null || !GeneratingYards.ContainsKey(t.ID.yardId)) return false;
            _checks++;

            var tracks = FreightTracks(t.ID.yardId);
            if (tracks == null || !tracks.Contains(t)) return false; // kein Gueter-Gleis (z. B. L) -> Vanilla

            var occupied = OccupiedTracks(t.ID.yardId);
            if (IsUsed(t, occupied)) return false; // schon belegt -> darf weiter aufgefuellt werden

            int used = tracks.Count(x => IsUsed(x, occupied));
            bool blocked = used >= Allowed(tracks.Count);
            if (blocked) _blocks++;
            return blocked;
        }

        private static int _checks, _blocks, _spawnsBlocked, _jobLimitHits;
        private static readonly Dictionary<string, StationProceduralJobsController> Controllers =
            new Dictionary<string, StationProceduralJobsController>(StringComparer.OrdinalIgnoreCase);

        // Das Spiel fuehrt neue Ketten erst ein paar Frames spaeter in der Bahnhofsliste -> selbst mitzaehlen
        private static readonly Dictionary<string, int> BaseChains = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> FinalizedChains = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Aufgerufen, sobald das Spiel eine neue Jobkette anlegt (ReserveRequiredTracks).</summary>
        public static void NoteChainCreated()
        {
            foreach (var yard in GeneratingYards.Keys.ToList())
                FinalizedChains[yard] = FinalizedChains.TryGetValue(yard, out int n) ? n + 1 : 1;
        }

        // ------------------------------------------------------------------ Job-Limit direkt am Generator (ab v0.05.07)
        // Der Vanilla-Generator ruft pro Versuch StationProceduralJobGenerator.GenerateJobChain auf. Ist das Limit
        // erreicht, wird der Versuch gar nicht erst gestartet (kein Wagen-Spawn). Jede erfolgreich erzeugte Kette
        // wird synchron im Postfix gezaehlt - unabhaengig davon, wann das Spiel sie in seine Listen eintraegt.
        private static readonly HashSet<string> RoundRobinRuns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> GeneratorChains = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static int _generatorRefusals;

        private static string GeneratorYard(object generator)
        {
            var sc = Reflect.Get<StationController>(generator, "stationController");
            return sc?.stationInfo?.YardID;
        }

        private static bool _refusedThisCall;

        public static bool GenerateJobChain_Prefix(object __instance, ref JobChainController __result)
        {
            _refusedThisCall = false;
            int max = Main.Settings.MaxJobsPerStation;
            if (max <= 0) max = int.MaxValue;
            if (!Main.IsAuthority) return true;
            if (max == int.MaxValue && !(WorldSpawner.SpawningYard != null && Main.Settings.JobsPerPass > 0)) return true;
            string yard = GeneratorYard(__instance);
            if (yard == null || !Controllers.TryGetValue(yard, out var ctl) || ctl == null) return true;

            int count = Math.Max(EffectiveChainCount(yard, ctl),
                                 (BaseChains.TryGetValue(yard, out int b) ? b : 0) + (GeneratorChains.TryGetValue(yard, out int g) ? g : 0));
            int perPass = Main.Settings.JobsPerPass;
            bool passFull = perPass > 0 && string.Equals(WorldSpawner.SpawningYard, yard, StringComparison.OrdinalIgnoreCase) && (GeneratorChains.TryGetValue(yard, out int gp) ? gp : 0) >= perPass;
            if (count < max && !passFull) return true;

            if (!passFull) _generatorRefusals++;
            _refusedThisCall = true;
            __result = null;
            return false;
        }

        private static readonly Dictionary<string, int> FailedAttempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>In der laufenden Generierung dieses Bahnhofs neu erzeugte Jobketten.</summary>
        public static int GeneratedThisRun(string yard) => yard != null && GeneratorChains.TryGetValue(yard, out int n) ? n : 0;

        // Zaehler, der nie zurueckgesetzt wird (fuers Overlay: Ketten pro Durchlauf exakt, unabhaengig von den Listen des Spiels)
        private static readonly Dictionary<string, int> TotalGenerated = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public static int TotalGeneratedAt(string yard) => yard != null && TotalGenerated.TryGetValue(yard, out int n) ? n : 0;

        public static void GenerateJobChain_Postfix(object __instance, JobChainController __result)
        {
            if (__result == null)
            {
                if (_refusedThisCall) { _refusedThisCall = false; return; } // vom Limit verweigert, kein echter Fehlversuch
                string fy = GeneratorYard(__instance);
                if (fy != null) FailedAttempts[fy] = FailedAttempts.TryGetValue(fy, out int fn) ? fn + 1 : 1;
                return;
            }
            string yard = GeneratorYard(__instance);
            if (yard == null) return;
            GeneratorChains[yard] = GeneratorChains.TryGetValue(yard, out int n) ? n + 1 : 1;
            TotalGenerated[yard] = TotalGenerated.TryGetValue(yard, out int tg) ? tg + 1 : 1;
        }

        private static int EffectiveChainCount(string yard, StationProceduralJobsController ctl)
        {
            int listed = ChainCount(ctl);
            int counted = (BaseChains.TryGetValue(yard, out int b) ? b : 0) + (FinalizedChains.TryGetValue(yard, out int f) ? f : 0);
            return Math.Max(listed, counted);
        }

        private static int ChainCount(StationProceduralJobsController controller)
        {
            try { return controller?.GetCurrentJobChains()?.Count() ?? 0; }
            catch { return 0; }
        }

        private static void LogUsage(string yardId, string when)
        {
            var tracks = FreightTracks(yardId);
            if (tracks == null || tracks.Count == 0) return;
            var occupied = OccupiedTracks(yardId);
            var used = tracks.Where(x => IsUsed(x, occupied)).Select(x => x.ID.FullDisplayID).ToList();
            Main.Log($"Station {yardId} {when}: {used.Count} of {tracks.Count} freight tracks in use " +
                     $"(limit {Allowed(tracks.Count)} = {Main.Settings.StationFillLimitPercent:0}%) [{string.Join(", ", used)}]");
        }

        // ------------------------------------------------------------------ Patches auf YardTracksOrganizer

        /// <summary>Postfix auf GetFreeSpaceOnTrack(Track): gesperrtes leeres Gleis meldet 0 m.</summary>
        public static void GetFreeSpace_Postfix(object[] __args, ref double __result)
        {
            if (!Active || __args == null || __args.Length == 0) return;
            if (IsBlocked(__args[0] as Track)) __result = 0.0;
        }

        /// <summary>Postfix auf FilterOutTracksWithoutRequiredFreeSpace(tracks, length): gesperrte Gleise entfernen.</summary>
        public static void Filter_Postfix(object[] __args, object __result)
        {
            if (Active && __result is IList list)
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i] is Track t && IsBlocked(t))
                        list.RemoveAt(i);

            InboundFallback.ExtendFilter(__args, __result);
        }

        /// <summary>Postfix fuer Vanilla-Methoden, die ein einzelnes Gleis mit genug Platz zurueckgeben.</summary>
        public static void PickTrack_Postfix(object[] __args, ref Track __result)
        {
            if (__result == null)
            {
                __result = InboundFallback.Pick(__args, null);
                return;
            }
            if (!Active || !IsBlocked(__result)) return;

            IEnumerable<Track> candidates = __args?.OfType<IEnumerable<Track>>().FirstOrDefault();
            double required = 0;
            if (__args != null)
                foreach (var a in __args)
                    if (a is float f) { required = f; break; }
                    else if (a is double d) { required = d; break; }

            var yto = YardTracksOrganizer.Instance;
            __result = candidates?.FirstOrDefault(t => t != null && !IsBlocked(t) && yto.GetFreeSpaceOnTrack(t) >= required);
        }

        /// <summary>
        /// Harte Sicherung: Prefix auf CarSpawner.SpawnCarTypesOnTrack*. Soll waehrend der Generierung auf ein
        /// gesperrtes (leeres) Gleis gespawnt werden, wird der Spawn verweigert (Vanilla behandelt das wie
        /// "kein Platz" und verwirft den Job).
        /// </summary>
        /// <summary>Postfix auf CarSpawner.SpawnCarTypesOnTrack*: nach jedem Spawn Belegung neu berechnen.</summary>
        public static void SpawnDone_Postfix()
        {
            if (GeneratingYards.Count > 0) _occupiedDirty = true;
        }

        public static bool SpawnGuard_Prefix(object[] __args, ref List<TrainCar> __result)
        {
            if (GeneratingYards.Count == 0 || !Main.IsAuthority || __args == null) return true;
            foreach (var a in __args)
            {
                if (!(a is RailTrack rt)) continue;
                var track = rt.LogicTrack();
                if (track == null) break;

                // 1) Job-Limit pro Bahnhof: genug Jobketten -> keine weiteren Wagen spawnen
                int max = Main.Settings.MaxJobsPerStation;
                if (max > 0 && Controllers.TryGetValue(track.ID.yardId, out var ctl) && ctl != null && EffectiveChainCount(track.ID.yardId, ctl) >= max)
                {
                    _jobLimitHits++;
                    __result = null;
                    return false;
                }

                // 2) experimentelle Gleis-Grenze
                if (Active && IsBlocked(track))
                {
                    _spawnsBlocked++;
                    __result = null;
                    return false;
                }
                break;
            }
            return true;
        }
    }
}
