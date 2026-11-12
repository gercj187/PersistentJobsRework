using System;
using System.Collections.Generic;
using System.Linq;
using DV.Logic.Job;
using DV.ThingTypes;

namespace PersistentJobsRework
{
    /// <summary>
    /// Leerwagenfahrten (LH) enden im Vanilla-Spiel auf einem S-Gleis. Gewuenscht: Ankunft auf einem I-Gleis,
    /// Ausweichen auf O erlaubt, nie S. Umgestellt wird direkt bei der Erzeugung der Kette (bevor ein Booklet
    /// existiert), die Reservierung zieht mit um. Laufende (angenommene) Jobs werden nie veraendert.
    /// Ausserdem: Entladen (SU), auf das ein Leerzug folgt, stellt die Wagen auf O (Ausweichen I) statt S -
    /// Leerzuege starten nie von S, S-Gleise bekommen nur Rangierjobs.
    /// </summary>
    internal static class LhRedirect
    {
        private static int _logged;

        private static bool On => Main.IsAuthority && Main.Settings.LhArriveOnInbound;

        /// <summary>Aus Reservations.ReserveRequiredTracks_Postfix: alle LH-Glieder einer neuen/geladenen Kette pruefen.</summary>
        public static void OnChainCreated(JobChainController chain)
        {
            if (!On || chain == null) return;
            try
            {
                var defs = TrackAssignment.ChainDefinitions(chain).Where(d => d != null).ToList();
                foreach (var def in defs.OfType<StaticEmptyHaulJobDefinition>().ToList())
                {
                    if (def.job != null && def.job.State != JobState.Available) continue; // angenommen/fertig -> nie anfassen
                    Redirect(chain, def);
                }

                // Entladen, auf das ein Leerzug folgt: Wagen nach O (Ausweichen I) statt S stellen.
                // Leerzuege duerfen nie von S starten - S-Gleise bekommen nur Rangierjobs.
                bool vanillaLhAfterUnload = chain.GetType().Name.IndexOf("EmptyHaul", StringComparison.OrdinalIgnoreCase) >= 0;
                for (int i = 0; i < defs.Count; i++)
                {
                    if (!(defs[i] is StaticShuntingUnloadJobDefinition su)) continue;
                    bool lhFollows = i + 1 < defs.Count ? defs[i + 1] is StaticEmptyHaulJobDefinition : vanillaLhAfterUnload;
                    if (!lhFollows) continue;
                    if (su.job != null && su.job.State != JobState.Available) continue;
                    RedirectUnload(chain, su);
                }
            }
            catch (Exception ex)
            {
                Main.WarningOnce("lh-redirect", "Redirecting empty hauls to inbound tracks failed: " + ex.Message);
            }
        }

        private static void RedirectUnload(JobChainController chain, StaticShuntingUnloadJobDefinition def)
        {
            var list = def.carsPerDestinationTrack;
            if (list == null || list.Count == 0) return;
            var yto = YardTracksOrganizer.Instance;
            var dict = Reservations.ReservationDict(chain);
            List<TrackReservation> reserved = null;
            dict?.TryGetValue(def, out reserved);

            for (int n = 0; n < list.Count; n++)
            {
                var cpt = list[n];
                var oldTrack = cpt?.track;
                if (oldTrack == null || cpt.cars == null || cpt.cars.Count == 0) continue;
                var yard = StationController.GetStationByYardID(oldTrack.ID.yardId)?.logicStation?.yard;
                if (yard?.StorageTracks == null || !yard.StorageTracks.Contains(oldTrack)) continue; // schon I/O

                float reservedOnOld = reserved?.Where(r => r.track == oldTrack).Sum(r => r.reservedLength) ?? 0f;
                float length = CarSpawner.Instance.GetTotalCarsLength(cpt.cars, true);
                // reservierter Platz auf dem alten Gleis zaehlt fuer das neue nicht mit
                Track Pick(List<Track> tracks) => tracks?
                    .Where(t => t != null && t != oldTrack && yto.GetFreeSpaceOnTrack(t) >= length)
                    .OrderBy(t => yto.GetFreeSpaceOnTrack(t))
                    .FirstOrDefault();
                var newTrack = Pick(yard.TransferOutTracks);   // verlaesst den Bahnhof (Leerzug folgt): immer O
                if (newTrack == null)
                {
                    if (_logged++ < 40)
                        Main.Log($"Unload before empty haul to {oldTrack.ID.FullDisplayID}: no O track at {oldTrack.ID.yardId} fits {length:0} m - keeps the S track.");
                    continue;
                }

                list[n] = new CarsPerTrack(newTrack, cpt.cars);

                if (reservedOnOld > 0f && reserved != null)
                {
                    float move = Math.Min(reservedOnOld, length);
                    for (int i = 0; i < reserved.Count && move > 0f; i++)
                    {
                        if (reserved[i].track != oldTrack) continue;
                        float len = Math.Min(reserved[i].reservedLength, move);
                        yto.ReleaseReservedSpace(oldTrack, len);
                        yto.ReserveSpace(newTrack, len, false);
                        reserved[i] = new TrackReservation(newTrack, len);
                        move -= len;
                    }
                }

                if (def.job != null)
                {
                    var changed = new List<Task>();
                    foreach (var t in TrackAssignment.LeafTasks(def.job).OfType<TransportTask>())
                        if (Reflect.Get<Track>(t, "destinationTrack") == oldTrack && Reflect.Set(t, "destinationTrack", newTrack))
                            changed.Add(t);
                    if (changed.Count > 0) Mp.SendJobTrack(def.job, changed, newTrack);
                }

                if (_logged++ < 40)
                    Main.Log($"Unload{(def.job != null ? " " + def.job.ID : "")} before empty haul: {oldTrack.ID.FullDisplayID} -> {newTrack.ID.FullDisplayID} ({length:0} m, empty haul must not start on storage).");
            }
        }

        private static void Redirect(JobChainController chain, StaticEmptyHaulJobDefinition def)
        {
            var oldTrack = Reflect.Get<Track>(def, "destinationTrack");
            if (oldTrack == null) return;
            var yard = StationController.GetStationByYardID(oldTrack.ID.yardId)?.logicStation?.yard;
            if (yard?.StorageTracks == null || !yard.StorageTracks.Contains(oldTrack)) return; // endet schon auf I/O

            var dict = Reservations.ReservationDict(chain);
            List<TrackReservation> reserved = null;
            dict?.TryGetValue(def, out reserved);
            float reservedOnOld = reserved?.Where(r => r.track == oldTrack).Sum(r => r.reservedLength) ?? 0f;

            float length = reservedOnOld;
            if (length <= 0f)
            {
                var cars = Reflect.Get<List<Car>>(def, "carsToTransport");
                if (cars == null || cars.Count == 0) return;
                length = CarSpawner.Instance.GetTotalCarsLength(cars, true);
            }

            var yto = YardTracksOrganizer.Instance;
            Track Pick(List<Track> tracks) => tracks?
                .Where(t => t != null && yto.GetFreeSpaceOnTrack(t) >= length)
                .OrderBy(t => yto.GetFreeSpaceOnTrack(t))   // bestes Gleis: Zug passt gerade noch
                .FirstOrDefault();

            var newTrack = Pick(yard.TransferInTracks);    // Ankunft: immer I (O nur beim Ausweichen mit Abzug)
            if (newTrack == null)
            {
                if (_logged++ < 20)
                    Main.Log($"Empty haul to {oldTrack.ID.FullDisplayID}: no I track at {oldTrack.ID.yardId} fits {length:0} m - keeps the S track.");
                return;
            }

            if (!Reflect.Set(def, "destinationTrack", newTrack)) return;

            // Reservierung umziehen
            if (reservedOnOld > 0f && reserved != null)
            {
                for (int i = 0; i < reserved.Count; i++)
                {
                    if (reserved[i].track != oldTrack) continue;
                    float len = reserved[i].reservedLength;
                    yto.ReleaseReservedSpace(oldTrack, len);
                    yto.ReserveSpace(newTrack, len, false);
                    reserved[i] = new TrackReservation(newTrack, len);
                }
            }


            // ist der Job schon erzeugt (aber noch nicht angenommen): Aufgaben mitziehen und an Clients senden
            if (def.job != null)
            {
                var changed = new List<Task>();
                foreach (var t in TrackAssignment.LeafTasks(def.job).OfType<TransportTask>())
                    if (Reflect.Get<Track>(t, "destinationTrack") == oldTrack && Reflect.Set(t, "destinationTrack", newTrack))
                        changed.Add(t);
                if (changed.Count > 0) Mp.SendJobTrack(def.job, changed, newTrack);
            }

            if (_logged++ < 20)
                Main.Log($"Empty haul{(def.job != null ? " " + def.job.ID : "")}: {oldTrack.ID.FullDisplayID} -> {newTrack.ID.FullDisplayID} ({length:0} m, arrives on inbound/outbound instead of storage).");
        }
    }
}
