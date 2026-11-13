using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.Logic.Job;
using DV.ThingTypes;

namespace PersistentJobsRework
{
    /// <summary>
    /// Ausweichgleise: Ein Transport-Teilauftrag gilt auch als erfuellt, wenn ALLE Wagen auf einem anderen
    /// Gueter-Gleis (S, I oder O) DESSELBEN Bahnhofs stehen. Pro abweichendem Ziel gibt es einen Abzug:
    /// gleiche Gruppe (z. B. D5 statt D3) und andere Gruppe (z. B. C7 statt D3) getrennt einstellbar.
    /// L-, P-, T- und alle sonstigen Gleise sind nie erlaubt.
    ///
    /// Der Job selbst wird NIE veraendert (Booklet = Host = Client). Fuer die Pruefung wird das Zielgleis nur
    /// fuer die Dauer eines einzigen Aufrufs auf dem Host vertauscht und sofort zurueckgesetzt.
    /// </summary>
    internal static class AlternativeTracks
    {
        private sealed class Deviation
        {
            public Job Job;
            public Track AssignedTrack;
            public Track UsedTrack;
            public string Assigned;
            public string Used;
            public bool SameGroup;
            public float Percent;
        }

        private static readonly Dictionary<Task, Deviation> Deviations = new Dictionary<Task, Deviation>();
        [ThreadStatic] private static bool _reentrant;

        public static bool Active => Main.IsAuthority && Main.Settings.AlternativeTracks;

        public static void Reset() { Deviations.Clear(); Completed.Clear(); ClientDeductions.Clear(); }

        /// <summary>
        /// Abzuege abgeschlossener Jobs: Auszahlung und Auftragsbericht kommen erst NACH CompleteJob.
        /// Bisher wurden die Eintraege dort schon geloescht -> weder Abzug noch Berichtszeile. Jetzt bleiben sie
        /// 10 Minuten (Echtzeit) unter der Job-ID erhalten (Job-IDs werden im Spiel spaeter wiederverwendet).
        /// </summary>
        internal sealed class CompletedDeduction
        {
            public float Percent;
            public List<string> Assigned = new List<string>();   // zugewiesene Gleise (Anzeige-ID ohne Bahnhof)
            public float At;
        }
        internal static readonly Dictionary<string, CompletedDeduction> Completed = new Dictionary<string, CompletedDeduction>();

        /// <summary>Client: vom Host gemeldete Abzuege je Job-ID (gelten fuer Lohn und Bericht).</summary>
        private static readonly Dictionary<string, CompletedDeduction> ClientDeductions = new Dictionary<string, CompletedDeduction>();

        /// <summary>Aus TrackSync: Paket "Prozent|G3O,D5I" vom Host.</summary>
        internal static void OnClientDeduction(string jobId, string payload)
        {
            if (string.IsNullOrEmpty(jobId) || payload == null) return;
            var parts = payload.Split('|');
            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pct)) return;
            var d = new CompletedDeduction { Percent = pct, At = UnityEngine.Time.realtimeSinceStartup };
            if (parts.Length > 1 && parts[1].Length > 0) d.Assigned.AddRange(parts[1].Split(','));
            ClientDeductions[jobId] = d;
            Main.Log($"[MP] {jobId}: deduction -{pct:0.#}% for alternative track(s) received from host.");
        }

        /// <summary>Host: aktuellen Abzug eines Jobs an die Clients senden.</summary>
        private static void SendDeduction(Job job)
        {
            if (job == null || !Mp.Available) return;
            var mine = Deviations.Where(kv => BelongsTo(kv.Key, kv.Value, job)).ToList();
            float total = Math.Min(mine.Sum(kv => kv.Value.Percent), 100f);
            TrackSync.SendDeduction(job.ID, total, mine.Select(kv => ShortId(kv.Value.Assigned)));
        }

        private static string ShortId(string a)
        {
            a = a ?? "";
            int dash = a.IndexOf('-');
            return dash >= 0 ? a.Substring(dash + 1) : a;
        }

        internal static CompletedDeduction FindCompleted(string jobId)
        {
            if (!Main.IsAuthority)
            {
                if (string.IsNullOrEmpty(jobId) || !ClientDeductions.TryGetValue(jobId, out var cd)) return null;
                if (UnityEngine.Time.realtimeSinceStartup - cd.At > 3600f) { ClientDeductions.Remove(jobId); return null; }
                return cd;
            }
            if (string.IsNullOrEmpty(jobId) || !Completed.TryGetValue(jobId, out var d)) return null;
            if (UnityEngine.Time.realtimeSinceStartup - d.At > 600f) { Completed.Remove(jobId); return null; }
            return d;
        }

        // ================================================================== Patch: TransportTask.UpdateTaskState

        /// <summary>Variante fuer UpdateTaskState mit Rueckgabewert TaskState.</summary>
        public static void UpdateTaskState_Postfix_WithResult(Task __instance, MethodBase __originalMethod, object[] __args, ref TaskState __result)
        {
            var result = Evaluate(__instance, __originalMethod, __args);
            if (result.HasValue) __result = result.Value;
        }

        /// <summary>Variante fuer UpdateTaskState ohne Rueckgabewert.</summary>
        public static void UpdateTaskState_Postfix_Void(Task __instance, MethodBase __originalMethod, object[] __args)
        {
            Evaluate(__instance, __originalMethod, __args);
        }

        private static TaskState? Evaluate(Task task, MethodBase original, object[] args)
        {
            if (_reentrant || !Active || !(task is TransportTask)) return null;

            try
            {
                var assigned = Reflect.Get<Track>(task, "destinationTrack");
                if (assigned == null) return null;

                var cars = Reflect.Get<List<Car>>(task, "cars");
                if (cars == null || cars.Count == 0) return null;

                if (GetState(task) == TaskState.Done)
                {
                    // Spieler hat doch noch aufs zugewiesene Gleis umgestellt -> Abzug entfaellt
                    if (AllCarsOn(cars, assigned)) Deviations.Remove(task);
                    return null;
                }

                var used = cars[0]?.FrontBogieTrack;
                if (used == null || used == assigned || !AllCarsOn(cars, used)) return null;
                var ownerJob = Reflect.GetAny<Job>(task, "Job", "job");
                if (!IsAllowedAlternative(assigned, used, ownerJob, out bool sameGroup)) return null;

                // Vanilla-Pruefung (Ladung, Kupplung, Handbremse ...) mit dem Ausweichgleis wiederholen
                object ret;
                _reentrant = true;
                try
                {
                    Reflect.Set(task, "destinationTrack", used);
                    ret = original.Invoke(task, args);
                }
                finally
                {
                    Reflect.Set(task, "destinationTrack", assigned);
                    _reentrant = false;
                }

                if (GetState(task) != TaskState.Done) return null;

                float pct = sameGroup ? Main.Settings.PenaltySameGroup : Main.Settings.PenaltyOtherGroup;
                string usedId = used.ID.FullDisplayID;
                if (!Deviations.TryGetValue(task, out var d) || d.Used != usedId)
                {
                    var job = ownerJob;
                    Deviations[task] = new Deviation
                    {
                        Job = job,
                        AssignedTrack = assigned,
                        UsedTrack = used,
                        Assigned = assigned.ID.FullDisplayID,
                        Used = usedId,
                        SameGroup = sameGroup,
                        Percent = pct
                    };
                    string msg = $"{job?.ID ?? "Job"}: {usedId} instead of {assigned.ID.FullDisplayID} " +
                                 $"({(sameGroup ? "same group" : "other group")}) -{pct:0.#}%";
                    Main.Log("Alternative track accepted - " + msg);
                    SendDeduction(job);   // Clients: gleicher Abzug bei Lohn und Bericht
                    // keine Texteinblendung im Bild (nur Log)
                }

                return ret is TaskState ts ? (TaskState?)ts : TaskState.Done;
            }
            catch (Exception ex)
            {
                Main.WarningOnce("alt-track", "Alternative track check failed: " + (ex.InnerException?.Message ?? ex.Message));
                return null;
            }
        }

        // ================================================================== Patch: Lohn

        /// <summary>Postfix auf Job.GetWageForTheJob: Abzuege aller abweichenden Ziele dieses Jobs addieren.</summary>
        public static void GetWage_Postfix(Job __instance, ref float __result)
        {
            if (__instance == null) return;
            if (!Main.IsAuthority)
            {
                // Client: Abzug kommt vom Host (sonst bekaeme der Client den vollen Lohn)
                float cp = FindCompleted(__instance.ID)?.Percent ?? 0f;
                if (cp > 0f) __result *= Math.Max(0f, 1f - cp / 100f);
                return;
            }
            float pct = TotalPenaltyPercent(__instance);
            if (pct <= 0f && __instance.State == JobState.Completed) pct = FindCompleted(__instance.ID)?.Percent ?? 0f;
            if (pct > 0f) __result *= Math.Max(0f, 1f - pct / 100f);
        }

        /// <summary>Ersatz, falls GetWageForTheJob nicht existiert: Grundlohn direkt vor der Abrechnung kuerzen.</summary>
        public static void CompleteJob_Prefix_ScaleWage(Job __instance)
        {
            if (!Main.IsAuthority || __instance == null) return;
            float pct = TotalPenaltyPercent(__instance);
            if (pct <= 0f) return;

            float wage = Reflect.Get<float>(__instance, "initialWage");
            if (wage > 0f) Reflect.Set(__instance, "initialWage", wage * Math.Max(0f, 1f - pct / 100f));
        }

        /// <summary>
        /// Prefix auf Job.CompleteJob: Folgejobs der Kette auf das tatsaechlich genutzte Gleis umstellen,
        /// BEVOR die Kette den naechsten Job erzeugt (das passiert waehrend CompleteJob).
        /// </summary>
        public static void CompleteJob_Prefix_Redirect(Job __instance)
        {
            if (!Main.IsAuthority || __instance == null) return;
            try
            {
                foreach (var kv in Deviations)
                    if (BelongsTo(kv.Key, kv.Value, __instance))
                        TrackAssignment.RedirectFollowUpStart(__instance, kv.Value.AssignedTrack, kv.Value.UsedTrack);
            }
            catch (Exception ex)
            {
                Main.WarningOnce("redirect", "Could not redirect follow-up job: " + ex.Message);
            }
        }

        /// <summary>Postfix auf Job.CompleteJob: Zusammenfassung ins Log, Eintraege aufraeumen.</summary>
        public static void CompleteJob_Postfix(Job __instance)
        {
            if (__instance == null) return;
            var mine = Deviations.Where(kv => BelongsTo(kv.Key, kv.Value, __instance)).ToList();
            if (mine.Count == 0) return;

            float total = mine.Sum(kv => kv.Value.Percent);
            Main.Log($"Job {__instance.ID} completed with {mine.Count} alternative track(s), total deduction -{total:0.#}%: " +
                     string.Join(", ", mine.Select(kv => $"{kv.Value.Used} instead of {kv.Value.Assigned} -{kv.Value.Percent:0.#}%")));

            var done = new CompletedDeduction { Percent = Math.Min(total, 100f), At = UnityEngine.Time.realtimeSinceStartup };
            foreach (var kv in mine)
            {
                string a = kv.Value.Assigned ?? "";
                int dash = a.IndexOf('-');
                done.Assigned.Add(dash >= 0 ? a.Substring(dash + 1) : a);   // "HB-G3O" -> "G3O"
            }
            Completed[__instance.ID] = done;
            if (Main.IsAuthority) TrackSync.SendDeduction(__instance.ID, done.Percent, done.Assigned);

            foreach (var kv in mine) Deviations.Remove(kv.Key);
        }

        private static float TotalPenaltyPercent(Job job)
        {
            float sum = 0f;
            foreach (var kv in Deviations)
                if (BelongsTo(kv.Key, kv.Value, job)) sum += kv.Value.Percent;
            return Math.Min(sum, 100f);
        }

        private static bool BelongsTo(Task task, Deviation d, Job job)
        {
            if (d.Job != null) return d.Job == job;
            return job.tasks != null && job.tasks.Any(t => ContainsTask(t, task));
        }

        private static bool ContainsTask(Task root, Task wanted)
        {
            if (root == null) return false;
            if (root == wanted) return true;
            if (Reflect.Get<object>(root, "tasks") is IEnumerable children)
                foreach (var c in children)
                    if (c is Task ct && ContainsTask(ct, wanted)) return true;
            return false;
        }

        // ================================================================== Regeln

        private static bool AllCarsOn(List<Car> cars, Track track)
        {
            foreach (var c in cars)
                if (c == null || c.FrontBogieTrack != track) return false;
            return true;
        }

        private static TaskState GetState(Task task) => Reflect.GetAny<TaskState>(task, "state", "State");

        /// <summary>
        /// Erlaubte Ausweichgleise je Auftragsart (immer nur im selben Bahnhof, das zugewiesene Gleis muss selbst
        /// ein Gueter-Gleis sein - die Fahrt zum Ladegleis kann also nie ausweichen):
        ///  - Gueterzug / Leerwagen (FH/LH): S, I, O
        ///  - Beladen (ShuntingLoad):        nur I, O
        ///  - Entladen (ShuntingUnload):     nur S
        ///  - andere Auftragsarten (z. B. anderer Mods): keine
        /// </summary>
        private static bool IsAllowedAlternative(Track assigned, Track used, Job job, out bool sameGroup)
        {
            sameGroup = false;
            if (assigned.ID.yardId != used.ID.yardId) return false;

            var station = StationController.GetStationByYardID(assigned.ID.yardId);
            var yard = station?.logicStation?.yard;
            if (yard == null) return false;

            bool IsS(Track t) => yard.StorageTracks?.Contains(t) ?? false;
            bool IsI(Track t) => yard.TransferInTracks?.Contains(t) ?? false;
            bool IsO(Track t) => yard.TransferOutTracks?.Contains(t) ?? false;
            bool IsFreight(Track t) => IsS(t) || IsI(t) || IsO(t);

            if (!IsFreight(assigned)) return false;

            bool allowed;
            var type = job != null ? job.jobType : JobType.Transport;
            if (type == JobType.Transport || type == JobType.EmptyHaul) allowed = IsI(used) || IsO(used); // nie auf S enden
            else if (type == JobType.ShuntingLoad) allowed = IsI(used) || IsO(used);
            else if (type == JobType.ShuntingUnload)
            {
                // Folgt eine Leerwagenfahrt (Logistical Haul), muessen die Wagen auf I/O stehen,
                // bleiben sie fuer spaetere Beladeauftraege im Bahnhof, nur auf S.
                bool lhFollows = UnloadFollowedByLogisticHaul(job);
                allowed = lhFollows ? (IsO(used) || IsI(used)) : IsS(used);   // Leerzug folgt: O (Ausweichen I), sonst nur S-Gleise
                Main.LogOnce("su-rule-" + job.ID, $"Unload job {job.ID}: alternatives limited to {(lhFollows ? "O/I tracks (logistic haul follows)" : "S tracks (cars stay for loading)")}.");
            }
            else allowed = false;

            if (!allowed) return false;

            sameGroup = string.Equals(GroupOf(assigned), GroupOf(used), StringComparison.OrdinalIgnoreCase);
            return true;
        }

        /// <summary>
        /// Folgt auf diesen Entladeauftrag eine Leerwagenfahrt? Entweder steht sie als naechstes Glied in der Kette,
        /// oder die Kette ist die Vanilla-Sorte, die nach dem Entladen automatisch eine Leerwagenfahrt erzeugt.
        /// </summary>
        private static bool UnloadFollowedByLogisticHaul(Job job)
        {
            var chain = job != null ? Reservations.FindChain(job) : null;
            if (chain == null) return false;

            bool afterCurrent = false;
            foreach (var def in TrackAssignment.ChainDefinitions(chain))
            {
                if (def == null) continue;
                if (def.job == job) { afterCurrent = true; continue; }
                if (afterCurrent) return def is StaticEmptyHaulJobDefinition;
            }

            return chain.GetType().Name.IndexOf("EmptyHaul", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Gruppe = Unter-Yard (z. B. "D" bei HB-D3I). Erst ueber das Feld, sonst aus der Anzeige-ID gelesen.</summary>
        internal static string GroupOf(Track track)
        {
            object id = track.ID;
            var sub = Reflect.GetAny<object>(id, "subYardId", "SubYardId");
            if (sub != null) return sub.ToString();

            string full = track.ID.FullDisplayID ?? "";
            string yard = track.ID.yardId ?? "";
            if (full.StartsWith(yard, StringComparison.OrdinalIgnoreCase)) full = full.Substring(yard.Length);
            full = full.TrimStart('-', ' ', '_');
            int n = 0;
            while (n < full.Length && char.IsLetter(full[n])) n++;
            return n > 0 ? full.Substring(0, n) : full;
        }
    }
}
