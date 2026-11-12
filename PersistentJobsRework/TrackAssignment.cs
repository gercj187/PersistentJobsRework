using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.Booklets;
using DV.Logic.Job;
using DV.ThingTypes;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// Buendeln bei Annahme: Gueter- und Leerwagen-Fahrten (FH/LH) behalten ihr bei der Erzeugung reserviertes
    /// Zielgleis (Vanilla). Hat derselbe Spieler aber schon einen laufenden FH/LH-Job zum selben Zielbahnhof und
    /// passt die Laenge noch dazu, wird der neue Job auf DASSELBE Gleis gelegt. Die Reservierung zieht mit um,
    /// das alte Gleis wird freigegeben.
    ///
    /// Das passiert auf dem Host, bevor das Booklet gedruckt wird, und wird vorher an alle Clients geschickt -
    /// das Booklet wird also bei allen mit dem neuen Gleis aufgebaut. Danach wird der Job nie mehr veraendert.
    /// </summary>
    internal static class TrackAssignment
    {
        public static bool Active => Main.IsAuthority && Main.Settings.AssignTrackOnAcceptance;

        /// <summary>Prefix auf JobValidator.ProcessJobOverview (nach den Pruefungen anderer Mods).</summary>
        public static void ProcessJobOverview_Prefix(JobValidator __instance, JobOverview jobOverview, bool __runOriginal)
        {
            if (!__runOriginal || !Active) return;   // andere Mod (z. B. DVMultiplayerIP) hat die Annahme abgelehnt

            Job job = null;
            try
            {
                job = jobOverview != null ? jobOverview.job : null;
                if (job == null || job.State != JobState.Available) return;

                string owner = Owners.CurrentAcceptor(__instance != null ? __instance.transform.position : Vector3.zero);
                Owners.Set(job.ID, owner);

                if (job.jobType != JobType.Transport && job.jobType != JobType.EmptyHaul) return;
                Assign(job, owner);
            }
            catch (Exception ex)
            {
                Main.Warning($"Track assignment for {job?.ID} failed, job keeps its original track: {ex.Message}");
            }
        }

        // ================================================================== Kern

        private static void Assign(Job job, string owner)
        {
            var transportTasks = LeafTasks(job).OfType<TransportTask>().ToList();
            if (transportTasks.Count == 0) return;

            var oldTrack = Reflect.Get<Track>(transportTasks[0], "destinationTrack");
            if (oldTrack == null) return;

            var cars = transportTasks.SelectMany(t => Reflect.Get<List<Car>>(t, "cars") ?? new List<Car>()).Distinct().ToList();
            if (cars.Count == 0) return;
            float length = CarSpawner.Instance.GetTotalCarsLength(cars, true);

            var yto = YardTracksOrganizer.Instance;

            // eigene laufende FH/LH-Jobs zum selben Zielbahnhof
            var mine = RunningHaulsTo(oldTrack.ID.yardId, job).Where(r => r.Owner == owner).ToList();
            if (mine.Count == 0)
            {
                if (!PickFreeTrackForUnreservedJob(job, transportTasks, oldTrack, length))
                    Main.Log($"Track assignment {job.ID} ({owner}): keeps {oldTrack.ID.FullDisplayID} (no own job to {oldTrack.ID.yardId}).");
                return;
            }

            foreach (var r in mine)
            {
                if (r.Track == oldTrack)
                {
                    Main.Log($"Track assignment {job.ID} ({owner}): already on the same track as {r.JobId} ({oldTrack.ID.FullDisplayID}).");
                    return;
                }

                // FH und LH (auch gemischt) teilen sich ein Gleis - aber nie ein S-Gleis (FH/LH enden nie auf S)
                var ry = StationController.GetStationByYardID(r.Track.ID.yardId)?.logicStation?.yard;
                if (ry?.StorageTracks != null && ry.StorageTracks.Contains(r.Track))
                {
                    Main.Log($"Track assignment {job.ID} ({owner}): {r.JobId} ends on storage track {r.Track.ID.FullDisplayID} - not used for bundling.");
                    continue;
                }

                // freier Platz (Wagen + Reservierungen, inkl. der des eigenen ersten Jobs) muss fuer den neuen Zug reichen
                if (yto.GetFreeSpaceOnTrack(r.Track) >= length)
                {
                    ApplyNewDestination(job, transportTasks, oldTrack, r.Track, length);
                    Main.Log($"Track assignment {job.ID} ({job.jobType}, {owner}): {oldTrack.ID.FullDisplayID} -> {r.Track.ID.FullDisplayID} (bundled with {r.JobId} {r.Type}), {length:F0} m.");
                    DebugHud.Notify(Loc.F("n_bundled", job.ID, r.JobId, r.Track.ID.FullDisplayID));
                    return;
                }
            }

            if (!PickFreeTrackForUnreservedJob(job, transportTasks, oldTrack, length))
                Main.Log($"Track assignment {job.ID} ({owner}): keeps {oldTrack.ID.FullDisplayID} (track of own job too short for another {length:F0} m).");
        }

        /// <summary>
        /// Folgeauftrag einer Kette (noch ohne Reservierung): Ist sein Zielgleis jetzt zu voll, ein freies Gueter-Gleis
        /// im selben Bahnhof waehlen - zuerst gleiche Gleisart und Gruppe, dann gleiche Gleisart, dann I/O (nie S).
        /// </summary>
        private static bool PickFreeTrackForUnreservedJob(Job job, List<TransportTask> tasks, Track oldTrack, float length)
        {
            var chain = Reservations.FindChain(job);
            var def = chain != null ? ChainDefinitions(chain).FirstOrDefault(d => d != null && d.job == job) : null;
            if (chain == null || def == null || Reservations.HasReservation(chain, def)) return false; // reserviert -> Platz ist sicher

            var yto = YardTracksOrganizer.Instance;
            if (yto.GetFreeSpaceOnTrack(oldTrack) >= length) return false; // passt noch

            var yard = StationController.GetStationByYardID(oldTrack.ID.yardId)?.logicStation?.yard;
            if (yard == null) return false;
            // FH/LH duerfen nie auf S-Gleisen enden -> nur I und O als Ersatz
            var lists = new[] { yard.TransferInTracks, yard.TransferOutTracks }.Where(l => l != null).ToList();
            var sameType = lists.FirstOrDefault(l => l.Contains(oldTrack)) ?? yard.TransferInTracks ?? new List<Track>();
            string group = AlternativeTracksGroup(oldTrack);

            Track pick = sameType.Where(t => t != oldTrack && yto.GetFreeSpaceOnTrack(t) >= length)
                                 .OrderByDescending(t => string.Equals(AlternativeTracksGroup(t), group, StringComparison.OrdinalIgnoreCase))
                                 .FirstOrDefault()
                         ;   // nur dieselbe Gleisart: Ankunft bleibt I, Abfahrt bleibt O (andere Art nur beim Ausweichen mit Abzug)

            if (pick == null)
            {
                Main.Log($"Track assignment {job.ID}: {oldTrack.ID.FullDisplayID} is full and no other track fits {length:F0} m - alternative tracks will be accepted on arrival.");
                DebugHud.Notify(Loc.F("n_full", job.ID, oldTrack.ID.yardId));
                return false;
            }

            ApplyNewDestination(job, tasks, oldTrack, pick, length);
            Main.Log($"Track assignment {job.ID}: {oldTrack.ID.FullDisplayID} is full -> {pick.ID.FullDisplayID} ({length:F0} m).");
            return true;
        }

        /// <summary>
        /// Zielgleis in Job, Jobketten-Definition und Folgejobs der Kette umstellen und an Clients senden.
        /// Passiert vor dem Booklet-Druck - danach wird der Job nie mehr veraendert.
        /// </summary>
        private static void ApplyNewDestination(Job job, List<TransportTask> tasks, Track oldTrack, Track newTrack, float length)
        {
            var changed = new List<Task>();
            foreach (var t in tasks)
            {
                if (Reflect.Get<Track>(t, "destinationTrack") == oldTrack && Reflect.Set(t, "destinationTrack", newTrack))
                    changed.Add(t);
            }

            var chain = Reservations.FindChain(job);
            if (chain != null)
            {
                bool afterCurrent = false;
                foreach (var def in ChainDefinitions(chain))
                {
                    if (def == null) continue;
                    if (def.job == job)
                    {
                        if (Reflect.Get<Track>(def, "destinationTrack") == oldTrack) Reflect.Set(def, "destinationTrack", newTrack);
                        afterCurrent = true;
                        continue;
                    }
                    // Folgejobs (z. B. Entladen am Ziel) holen die Wagen jetzt am neuen Gleis ab
                    if (afterCurrent && Reflect.Get<Track>(def, "startingTrack") == oldTrack)
                        Reflect.Set(def, "startingTrack", newTrack);
                }
            }

            // Reservierung vom alten aufs neue Gleis umziehen (nur wenn sie schon bei der Erzeugung angelegt wurde)
            if (chain != null && !Reservations.Active) MoveReservation(chain, job, oldTrack, newTrack, length);

            Mp.SendJobTrack(job, changed, newTrack);
        }

        private static void MoveReservation(JobChainController chain, Job job, Track oldTrack, Track newTrack, float length)
        {
            var yto = YardTracksOrganizer.Instance;
            try
            {
                var dict = Reflect.Get<Dictionary<StaticJobDefinition, List<TrackReservation>>>(chain, "jobDefToCurrentlyReservedTracks");
                var def = ChainDefinitions(chain).FirstOrDefault(d => d != null && d.job == job);
                if (dict != null && def != null && dict.TryGetValue(def, out var list) && list != null)
                {
                    bool moved = false;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].track != oldTrack) continue;
                        float len = list[i].reservedLength;
                        yto.ReleaseReservedSpace(oldTrack, len);
                        yto.ReserveSpace(newTrack, len, false);
                        list[i] = new TrackReservation(newTrack, len);
                        moved = true;
                    }
                    if (moved) return;
                }

                // noch keine Reservierung (Folgeauftrag) -> neues Gleis reservieren und eintragen,
                // damit die Reservierung bei Annahme nicht doppelt passiert und am Jobende wieder frei wird
                yto.ReserveSpace(newTrack, length, false);
                if (dict != null && def != null)
                {
                    if (!dict.TryGetValue(def, out var l2) || l2 == null) dict[def] = l2 = new List<TrackReservation>();
                    l2.Add(new TrackReservation(newTrack, length));
                }
            }
            catch (Exception ex)
            {
                Main.Warning($"Moving the reservation of {job.ID} failed: {ex.Message}");
            }
        }

        /// <summary>Nach einer Abgabe auf einem Ausweichgleis: Folgejobs der Kette starten dort, wo die Wagen wirklich stehen.</summary>
        public static void RedirectFollowUpStart(Job completedJob, Track assigned, Track used)
        {
            if (completedJob == null || assigned == null || used == null || assigned == used) return;
            var chain = Reservations.FindChain(completedJob);
            if (chain == null) return;

            bool afterCurrent = false;
            foreach (var def in ChainDefinitions(chain))
            {
                if (def == null) continue;
                if (def.job == completedJob) { afterCurrent = true; continue; }
                if (afterCurrent && Reflect.Get<Track>(def, "startingTrack") == assigned)
                {
                    Reflect.Set(def, "startingTrack", used);
                    Main.Log($"Follow-up job of {completedJob.ID} now starts at {used.ID.FullDisplayID} (instead of {assigned.ID.FullDisplayID}).");
                }
            }
        }

        // ================================================================== Hilfen

        private struct RunningHaul
        {
            public JobType Type;
            public string JobId;
            public string Owner;
            public Track Track;
        }

        private static List<RunningHaul> RunningHaulsTo(string yardId, Job exclude)
        {
            var result = new List<RunningHaul>();
            var stations = StationController.allStations;
            if (stations == null) return result;

            foreach (var sc in stations)
            {
                var chains = sc != null ? sc.ProceduralJobsController?.GetCurrentJobChains() : null;
                if (chains == null) continue;
                foreach (var c in chains)
                {
                    var j = c?.currentJobInChain;
                    if (j == null || j == exclude || j.State != JobState.InProgress) continue;
                    if (j.jobType != JobType.Transport && j.jobType != JobType.EmptyHaul) continue;

                    var tt = LeafTasks(j).OfType<TransportTask>().FirstOrDefault();
                    var dest = tt != null ? Reflect.Get<Track>(tt, "destinationTrack") : null;
                    if (dest == null || dest.ID.yardId != yardId) continue;

                    result.Add(new RunningHaul { JobId = j.ID, Owner = Owners.OwnerOf(j.ID) ?? "?", Track = dest, Type = j.jobType });
                }
            }
            return result;
        }

        /// <summary>Zielgleise aller angenommenen FH/LH-Jobs in diesem Bahnhof.</summary>
        internal static IEnumerable<Track> ClaimedTracks(string yardId)
        {
            try { return RunningHaulsTo(yardId, null).Select(r => r.Track).ToList(); }
            catch { return Enumerable.Empty<Track>(); }
        }

        internal static IEnumerable<StaticJobDefinition> ChainDefinitions(JobChainController chain)
        {
            if (Reflect.Get<object>(chain, "jobChain") is IEnumerable list)
                foreach (var o in list)
                    if (o is StaticJobDefinition d) yield return d;
        }

        internal static IEnumerable<Task> LeafTasks(Job job)
        {
            if (job?.tasks == null) yield break;
            foreach (var t in job.tasks)
                foreach (var leaf in Leaves(t))
                    yield return leaf;
        }

        private static IEnumerable<Task> Leaves(Task t)
        {
            if (t == null) yield break;
            if (Reflect.Get<object>(t, "tasks") is IEnumerable children && (t is ParallelTasks || t is SequentialTasks))
            {
                foreach (var c in children)
                    if (c is Task ct)
                        foreach (var l in Leaves(ct)) yield return l;
                yield break;
            }
            yield return t;
        }

        private static string AlternativeTracksGroup(Track t) => AlternativeTracks.GroupOf(t);
    }
}
