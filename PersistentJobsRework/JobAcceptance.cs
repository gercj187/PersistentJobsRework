using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DV.Logic.Job;
using DV.ThingTypes;

namespace PersistentJobsRework
{
    /// <summary>
    /// Annahme eines Jobs:
    ///  1) Abgelaufen ("expired") wird ein verfuegbarer Job NUR noch beim Annehmen - und nur, wenn in genau diesem
    ///     Moment nicht alle seine Wagen im Bahnhof stehen. Wagen ohne aktiven Job ueber die Bahnhofsgrenze zu
    ///     fahren (z. B. beim Aufraeumen) laesst die Jobkette nicht mehr verfallen.
    ///  2) Beim Annehmen wird einmalig geprueft, wo die Wagen wirklich stehen. Wurden sie umgestellt, bekommt der
    ///     Job das tatsaechliche Abholgleis (Aufgaben + Job-Definition, damit es auch nach Speichern/Laden stimmt;
    ///     im Multiplayer an die Clients gesendet).
    /// </summary>
    internal static class JobAcceptance
    {
        /// <summary>Waehrend unserer eigenen Annahme-Pruefung darf ExpireJob durch.</summary>
        private static bool _allowExpire;

        /// <summary>Etwas ausfuehren, bei dem verfuegbare Jobs ausdruecklich verfallen duerfen (z. B. Kette aufloesen).</summary>
        internal static void WithExpireAllowed(Action a)
        {
            bool old = _allowExpire;
            _allowExpire = true;
            try { a(); }
            finally { _allowExpire = old; }
        }

        // ------------------------------------------------------------------ 1) Verfall nur bei der Annahme

        /// <summary>Prefix auf Job.ExpireJob: verfuegbare Jobs verfallen nicht mehr von selbst.</summary>
        public static bool ExpireJob_Prefix(Job __instance)
        {
            if (!Main.IsActive || !Main.IsAuthority || __instance == null) return true;
            if (__instance.State != JobState.Available) return true;
            if (_allowExpire || WorldInit.AllowExpire) return true;

            // Spieler-Aktionen (Job wegwerfen) und das Spiel am Job-Validator duerfen weiterhin
            var st = new StackTrace(1, false);
            for (int i = 0; i < st.FrameCount && i < 20; i++)
            {
                var type = st.GetFrame(i)?.GetMethod()?.DeclaringType;
                string n = type?.FullName ?? "";
                if (n.IndexOf("JobAbandoner", StringComparison.Ordinal) >= 0 ||
                    n.IndexOf("Trash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("JobValidator", StringComparison.Ordinal) >= 0)
                    return true;
            }

            Stats.ExpiriesBlocked++;
            if (Stats.ExpiriesBlocked <= 30)
                Main.Log($"Job {__instance.ID}: expiring blocked (cars left the station without an active job) - it is only checked when the job is taken.");
            return false;
        }

        // ------------------------------------------------------------------ Annahme

        /// <summary>Prefix auf JobValidator.ProcessJobOverview (vor der Gleiswahl): Wagen im Bahnhof? Abholgleis aktuell?</summary>
        public static void ProcessJobOverview_Prefix(JobOverview jobOverview, bool __runOriginal)
        {
            if (!__runOriginal || !Main.IsActive || !Main.IsAuthority) return;
            Job job = null;
            try
            {
                job = jobOverview != null ? jobOverview.job : null;
                if (job == null || job.State != JobState.Available) return;

                var station = StationController.allStations.FirstOrDefault(s => s?.logicStation?.availableJobs != null && s.logicStation.availableJobs.Contains(job));
                if (station == null) return;
                string yardId = station.stationInfo.YardID;

                var cars = JobCars(job);
                if (cars.Count == 0) return;
                var outside = cars.Where(c => c?.CurrentTrack == null || c.CurrentTrack.ID.yardId != yardId).ToList();
                if (outside.Count > 0)
                {
                    Main.Log($"Job {job.ID}: {outside.Count} of {cars.Count} car(s) are not in {yardId} when taking the job -> expired.");
                    _allowExpire = true;
                    try { job.ExpireJob(); }
                    finally { _allowExpire = false; }
                    return;   // das Spiel druckt jetzt den "abgelaufen"-Bericht
                }

                UpdatePickupTracks(job);
            }
            catch (Exception ex)
            {
                Main.Warning($"Checking job {job?.ID} on acceptance failed: {ex.Message}");
            }
        }

        /// <summary>Prefix auf JobsManager.TakeJob (z. B. Annahme ueber das Netzwerk): nur das Abholgleis aktualisieren.</summary>
        public static void TakeJob_Prefix(object[] __args)
        {
            if (!Main.IsActive || !Main.IsAuthority || __args == null) return;
            try
            {
                var job = __args.OfType<Job>().FirstOrDefault();
                if (job != null && job.State == JobState.Available) UpdatePickupTracks(job);
            }
            catch (Exception ex) { Main.WarningOnce("take-pickup", "Updating pickup tracks failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------ 2) Abholgleis

        private static List<Car> JobCars(Job job)
            => TrackAssignment.LeafTasks(job)
                .SelectMany(t => Reflect.Get<List<Car>>(t, "cars") ?? new List<Car>())
                .Where(c => c != null).Distinct().ToList();

        private static void UpdatePickupTracks(Job job)
        {
            var transport = TrackAssignment.LeafTasks(job).OfType<TransportTask>().ToList();
            if (transport.Count == 0) return;

            // Abholaufgabe = Startgleis ist nicht Ziel einer anderen Aufgabe desselben Jobs (z. B. nicht das Ladegleis)
            var destinations = new HashSet<Track>(transport.Select(t => Reflect.Get<Track>(t, "destinationTrack")).Where(t => t != null));
            var changes = new List<(TransportTask task, Track oldTrack, Track newTrack, List<Car> cars)>();

            foreach (var task in transport)
            {
                var start = Reflect.Get<Track>(task, "startingTrack");
                if (start == null || destinations.Contains(start)) continue;
                var cars = Reflect.Get<List<Car>>(task, "cars");
                if (cars == null || cars.Count == 0) continue;

                // Gleis, auf dem die meisten Wagen dieser Aufgabe stehen
                var actual = cars.Where(c => c?.CurrentTrack != null)
                                 .GroupBy(c => c.CurrentTrack)
                                 .OrderByDescending(g => g.Count())
                                 .Select(g => g.Key).FirstOrDefault();
                if (actual == null || actual == start) continue;
                if (!Reflect.Set(task, "startingTrack", actual)) continue;
                changes.Add((task, start, actual, cars));
            }
            if (changes.Count == 0) return;

            // Job-Definition mitziehen (Speicherstand) und Clients informieren
            var chain = Reservations.FindChain(job);
            var def = chain != null ? TrackAssignment.ChainDefinitions(chain).FirstOrDefault(d => d != null && d.job == job) : null;
            foreach (var ch in changes)
            {
                UpdateDefinition(def, ch.oldTrack, ch.newTrack, ch.cars);
                Mp.SendJobStartTrack(job, new List<Task> { ch.task }, ch.newTrack);
                Main.Log($"Job {job.ID}: cars were moved - pickup track {ch.oldTrack.ID.FullDisplayID} -> {ch.newTrack.ID.FullDisplayID}.");
            }
        }

        private static void UpdateDefinition(StaticJobDefinition def, Track oldTrack, Track newTrack, List<Car> cars)
        {
            if (def == null) return;
            try
            {
                switch (def)
                {
                    case StaticTransportJobDefinition t when t.startingTrack == oldTrack: t.startingTrack = newTrack; break;
                    case StaticEmptyHaulJobDefinition e when e.startingTrack == oldTrack: e.startingTrack = newTrack; break;
                    case StaticShuntingUnloadJobDefinition u when u.startingTrack == oldTrack: u.startingTrack = newTrack; break;
                    case StaticShuntingLoadJobDefinition l when l.carsPerStartingTrack != null:
                        for (int i = 0; i < l.carsPerStartingTrack.Count; i++)
                        {
                            var cpt = l.carsPerStartingTrack[i];
                            if (cpt?.track == oldTrack && cpt.cars != null && cpt.cars.Intersect(cars).Any())
                                l.carsPerStartingTrack[i] = new CarsPerTrack(newTrack, cpt.cars);
                        }
                        break;
                }
            }
            catch (Exception ex) { Main.WarningOnce("pickup-def", "Updating the job definition failed: " + ex.Message); }
        }
    }
}
