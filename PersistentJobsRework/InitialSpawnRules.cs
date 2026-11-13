using System;
using System.Collections.Generic;
using System.Linq;
using DV.ThingTypes;

namespace PersistentJobsRework
{
    /// <summary>
    /// Regeln fuer den ersten Spawn (Weltspawn bzw. Einmal-Spawn eines Bahnhofs):
    ///  1) Wagengruppen hoechstens N Wagen (Standard 5) - sonst kann ein neuer Spieler anfangs nichts annehmen,
    ///     weil alle Zuege Lizenzen fuer lange Zuege brauchen. Spaeter (Nachfuellen) gelten wieder die Bahnhofsregeln.
    ///  2) Jeder Bahnhof bekommt zuerst mindestens M Rangierjobs (Standard 4): Solange er weniger hat, darf der
    ///     Generator dort nur Belade-/Entlade-Startjobs erzeugen (Gueter- und Leerzug-Startjobs voruebergehend aus).
    /// Das Regelwerk des Bahnhofs wird nur waehrend der Generierung veraendert und danach exakt zurueckgesetzt.
    /// </summary>
    internal static class InitialSpawnRules
    {
        private sealed class Saved
        {
            public int Min, Max;
            public bool? Haul, EmptyHaul;
        }

        /// <summary>Bahnhof -> verbleibende "Rangier-Versuche" (Job-Plaetze, die zuerst als Rangierjob versucht werden).</summary>
        private static readonly Dictionary<string, int> SlotsLeft = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> Fails = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>So viele Fehlversuche im Rangier-Modus verbrauchen einen Platz (dann ist jeder Jobtyp erlaubt).</summary>
        private const int FailsPerSlot = 3;
        /// <summary>Weltspawn reihum: Bahnhof -> in diesem Durchlauf erzeugte Jobs / Durchlauf laeuft reihum.</summary>
        private static readonly Dictionary<string, int> SuccessThisRun = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> RoundRobin = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Hat noch ein Bahnhof offene Rangier-Versuche? (Dann darf der Weltspawn nicht wegen eines leeren Durchlaufs aufhoeren.)</summary>
        public static bool AnyShuntingAttemptsLeft => SlotsLeft.Values.Any(v => v > 0);

        private static readonly Dictionary<StationProceduralJobsRuleset, Saved> Originals = new Dictionary<StationProceduralJobsRuleset, Saved>();

        /// <summary>Rangierjobs (Beladen/Entladen), die am Bahnhof zum Annehmen ausliegen.</summary>
        public static int ShuntingJobsAt(StationController sc)
        {
            var jobs = sc?.logicStation?.availableJobs;
            if (jobs == null) return 0;
            return jobs.Count(j => j != null && (j.jobType == JobType.ShuntingLoad || j.jobType == JobType.ShuntingUnload));
        }

        /// <summary>Aus Capacity.BeginGeneration.</summary>
        public static void Apply(StationProceduralJobsController controller, bool roundRobin)
        {
            try
            {
                var rs = Reflect.Get<StationProceduralJobsRuleset>(controller, "generationRuleset") ?? controller?.stationController?.proceduralJobsRuleset;
                if (rs == null || Originals.ContainsKey(rs)) return;
                var saved = new Saved { Min = rs.minCarsPerJob, Max = rs.maxCarsPerJob };

                int cap = Main.Settings.InitialMaxCarsPerJob;
                if (cap > 0)
                {
                    rs.maxCarsPerJob = Math.Min(rs.maxCarsPerJob, cap);
                    rs.minCarsPerJob = Math.Min(rs.minCarsPerJob, rs.maxCarsPerJob);
                }

                // Rangierjobs als VERSUCH: die ersten M Job-Plaetze eines Bahnhofs werden zuerst als Rangierjob
                // versucht. Klappt das nicht (3 Fehlversuche), ist der Platz verbraucht und jeder Jobtyp erlaubt.
                string yard = controller?.stationController?.stationInfo?.YardID;
                bool canShunt = Reflect.Get<bool>(rs, "loadStartingJobSupported") || Reflect.Get<bool>(rs, "unloadStartingJobSupported");
                if (yard != null && !SlotsLeft.ContainsKey(yard)) SlotsLeft[yard] = Math.Max(0, Main.Settings.MinShuntingJobsPerStation);
                if (yard != null)
                {
                    Fails[yard] = 0;
                    SuccessThisRun[yard] = 0;
                    if (roundRobin) RoundRobin.Add(yard); else RoundRobin.Remove(yard);
                }
                if (canShunt && yard != null && SlotsLeft[yard] > 0)
                {
                    saved.Haul = Reflect.Get<bool>(rs, "haulStartingJobSupported");
                    saved.EmptyHaul = Reflect.Get<bool>(rs, "emptyHaulStartingJobSupported");
                    Reflect.Set(rs, "haulStartingJobSupported", false);
                    Reflect.Set(rs, "emptyHaulStartingJobSupported", false);
                }
                Originals[rs] = saved;
            }
            catch (Exception ex) { Main.WarningOnce("initial-rules", "Initial spawn rules could not be applied: " + ex.Message); }
        }

        /// <summary>
        /// Nach jedem Generator-Versuch (aus Capacity.GenerateJobChain_Postfix). Im Rangier-Modus verbraucht ein
        /// erzeugter Job einen Platz, ebenso 3 Fehlversuche. Keine Plaetze mehr -> jeder Jobtyp erlaubt.
        /// </summary>
        public static void OnAttempt(StationProceduralJobsController controller, bool success)
        {
            try
            {
                var rs = Reflect.Get<StationProceduralJobsRuleset>(controller, "generationRuleset") ?? controller?.stationController?.proceduralJobsRuleset;
                string yard = controller?.stationController?.stationInfo?.YardID;
                if (rs == null || yard == null || !Originals.TryGetValue(rs, out var s) || !s.Haul.HasValue) return;
                if (!SlotsLeft.TryGetValue(yard, out int left) || left <= 0) { Unrestrict(rs, s); return; }

                if (success) { left--; SuccessThisRun[yard] = (SuccessThisRun.TryGetValue(yard, out int sv) ? sv : 0) + 1; }
                else if (!RoundRobin.Contains(yard))   // reihum: ein Durchlauf ist der Versuch (siehe Restore)
                {
                    int f = (Fails.TryGetValue(yard, out int fv) ? fv : 0) + 1;
                    if (f >= FailsPerSlot) { left--; f = 0; Main.Log($"Initial spawn {yard}: no shunting job possible in {FailsPerSlot} attempts - this slot allows every job type."); }
                    Fails[yard] = f;
                }
                SlotsLeft[yard] = left;
                if (left <= 0) Unrestrict(rs, s);
            }
            catch { }
        }

        private static void Unrestrict(StationProceduralJobsRuleset rs, Saved s)
        {
            if (!s.Haul.HasValue) return;
            Reflect.Set(rs, "haulStartingJobSupported", s.Haul.Value);
            if (s.EmptyHaul.HasValue) Reflect.Set(rs, "emptyHaulStartingJobSupported", s.EmptyHaul.Value);
            s.Haul = null; s.EmptyHaul = null;
        }

        /// <summary>Aus Capacity.EndWhenDone: Regelwerk exakt zuruecksetzen.</summary>
        public static void Restore(StationProceduralJobsController controller)
        {
            try
            {
                var rs = Reflect.Get<StationProceduralJobsRuleset>(controller, "generationRuleset") ?? controller?.stationController?.proceduralJobsRuleset;
                if (rs == null || !Originals.TryGetValue(rs, out var s)) return;
                Originals.Remove(rs);

                // Weltspawn reihum ("Wellen"): jeder Job-Platz dieses Durchlaufs zaehlt als Rangier-Versuch -
                // auch wenn kein Rangierjob moeglich war. Beispiel 4 Rangierjobs, 1 Job je Welle: Wellen 1-4 Rangier-
                // versuche, danach jeder Jobtyp. 5 Rangierjobs, 4 je Welle: Welle 1 komplett, in Welle 2 der erste Platz.
                string yard = controller?.stationController?.stationInfo?.YardID;
                if (yard != null && s.Haul.HasValue && RoundRobin.Contains(yard) && SlotsLeft.TryGetValue(yard, out int left))
                {
                    int perPass = Math.Max(1, Main.Settings.JobsPerPass);
                    int made = SuccessThisRun.TryGetValue(yard, out int m) ? m : 0;
                    int unused = Math.Max(0, perPass - made);
                    if (unused > 0)
                    {
                        SlotsLeft[yard] = Math.Max(0, left - unused);
                        Main.Log($"Initial spawn {yard}: {unused} shunting attempt(s) of this pass found no shunting job - {SlotsLeft[yard]} attempt(s) left.");
                    }
                }
                rs.minCarsPerJob = s.Min;
                rs.maxCarsPerJob = s.Max;
                if (s.Haul.HasValue) Reflect.Set(rs, "haulStartingJobSupported", s.Haul.Value);
                if (s.EmptyHaul.HasValue) Reflect.Set(rs, "emptyHaulStartingJobSupported", s.EmptyHaul.Value);
            }
            catch (Exception ex) { Main.WarningOnce("initial-rules-restore", "Initial spawn rules could not be restored: " + ex.Message); }
        }

        /// <summary>Sicherheitsnetz (z. B. Spiel beendet waehrend der Generierung): alles zuruecksetzen.</summary>
        public static void RestoreAll()
        {
            foreach (var kv in Originals.ToList())
            {
                var rs = kv.Key; var s = kv.Value;
                if (rs == null) continue;
                rs.minCarsPerJob = s.Min;
                rs.maxCarsPerJob = s.Max;
                if (s.Haul.HasValue) Reflect.Set(rs, "haulStartingJobSupported", s.Haul.Value);
                if (s.EmptyHaul.HasValue) Reflect.Set(rs, "emptyHaulStartingJobSupported", s.EmptyHaul.Value);
            }
            Originals.Clear();
            SlotsLeft.Clear();
            Fails.Clear();
            SuccessThisRun.Clear();
            RoundRobin.Clear();
        }
    }
}
