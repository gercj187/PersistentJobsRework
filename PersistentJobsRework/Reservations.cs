using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.Logic.Job;
using DV.ThingTypes;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// Gleisreservierungen von Jobketten.
    ///
    /// Standard (ab v0.05): Bei der Erzeugung reserviert eine Kette nur das Zielgleis ihres ERSTEN Auftrags
    /// (z. B. beim Beladen das Ausfahrgleis im eigenen Bahnhof). Jeder Folgeauftrag (z. B. der Gueterzug zum Ziel)
    /// reserviert erst bei seiner Annahme. So blockieren nie angenommene Ketten nicht mehr auf Vorrat die
    /// Eingangsgleise fremder Bahnhoefe, und auch kleine Bahnhoefe bekommen wieder Jobs.
    ///
    /// Experimentell: alles erst bei Annahme (v0.02/v0.03 - stellt Bahnhoefe voll, nicht empfohlen).
    /// Vanilla: alles sofort (beide Schalter aus).
    /// </summary>
    internal static class Reservations
    {
        private static bool _allowVanillaReserve;
        private static MethodInfo _reserveMethod;
        private static readonly HashSet<JobChainController> ReservedChains = new HashSet<JobChainController>();

        /// <summary>Experimenteller Modus: ganze Kette erst bei Annahme.</summary>
        public static bool Active => Main.IsAuthority && Main.Settings.DeferReservationToAcceptance;

        /// <summary>Standardmodus: nur erstes Glied sofort, Folgeglieder bei Annahme.</summary>
        public static bool FirstLinkMode => Main.IsAuthority && !Main.Settings.DeferReservationToAcceptance && Main.Settings.ReserveFirstLinkOnly;

        public static void Reset() { ReservedChains.Clear(); KnownChains.Clear(); }

        /// <summary>Alle Ketten, die seit dem Laden reserviert haben (erzeugt oder geladen) - fuer die Rangier-Prioritaet.</summary>
        private static readonly HashSet<JobChainController> KnownChains = new HashSet<JobChainController>();

        /// <summary>
        /// Reservierte Meter je Gleis, die nur von Jobs stammen, die noch NICHT angenommen sind (Available).
        /// Rangierjobs duerfen diesen Platz nutzen: sonst blockieren verfuegbare Gueterzuege (z. B. 30 Jobs mit
        /// Ziel HB) alle O/I-Gleise, obwohl dort nichts steht, und nichts kann mehr abgearbeitet werden.
        /// </summary>
        internal static Dictionary<Track, float> ReservedByNotTakenJobs()
        {
            var result = new Dictionary<Track, float>();
            foreach (var chain in KnownChains.ToList())
            {
                var dict = chain != null ? ReservationDict(chain) : null;
                if (dict == null) { KnownChains.Remove(chain); continue; }
                bool any = false;
                foreach (var kv in dict)
                {
                    if (kv.Value == null || kv.Value.Count == 0) continue;
                    any = true;
                    var job = kv.Key != null ? kv.Key.job : null;
                    if (job != null && job.State != JobState.Available) continue;   // angenommen -> Platz bleibt gesperrt
                    foreach (var r in kv.Value)
                        if (r.track != null) result[r.track] = (result.TryGetValue(r.track, out float v) ? v : 0f) + r.reservedLength;
                }
                if (!any && (chain.currentJobInChain == null || chain.currentJobInChain.State != JobState.Available)) KnownChains.Remove(chain);
            }
            return result;
        }

        // ------------------------------------------------------------------ Patches (in Patches.ApplyAll registriert)

        /// <summary>Prefix auf JobChainController.ReserveRequiredTracks(bool ...).</summary>
        public static bool ReserveRequiredTracks_Prefix()
        {
            if (_allowVanillaReserve) return true;
            if (Active)
            {
                Stats.ReservationsDeferred++;
                return false;
            }
            return true; // Vanilla reserviert die ganze Kette, der Postfix gibt danach alles ausser dem ersten Glied frei
        }

        /// <summary>
        /// Postfix auf JobChainController.ReserveRequiredTracks: Im Standardmodus nur die Reservierung des
        /// ersten (gerade anstehenden) Glieds behalten, alle Folgeglieder wieder freigeben. Entladeauftraege
        /// behalten gar keine Reservierung. Die Berechnung selbst bleibt die des Spiels.
        /// </summary>
        public static void ReserveRequiredTracks_Postfix(JobChainController __instance)
        {
            if (__instance != null) KnownChains.Add(__instance);
            if (!_allowVanillaReserve && __instance != null)
            {
                Capacity.NoteChainCreated();   // neue Kette waehrend der Erzeugung -> fuers Job-Limit mitzaehlen
                LhRedirect.OnChainCreated(__instance); // LH: Ankunft auf I (Ausweichen O) statt S - vor dem Kuerzen der Reservierungen
            }

            if (_allowVanillaReserve || !FirstLinkMode || __instance == null) return;
            try
            {
                var dict = ReservationDict(__instance);
                if (dict == null)
                {
                    Main.WarningOnce("res-dict-type", "Reservation table of job chains not readable - keeping vanilla reservations.");
                    return;
                }

                var keep = CurrentDefinition(__instance);
                if (keep is StaticShuntingUnloadJobDefinition) keep = null;

                var yto = YardTracksOrganizer.Instance;
                var kept = new List<string>();
                int released = 0;

                foreach (var def in dict.Keys.ToList())
                {
                    var list = dict[def];
                    if (list == null || list.Count == 0) continue;

                    if (def == keep)
                    {
                        kept.AddRange(list.Where(r => r.track != null).Select(r => $"{r.track.ID.FullDisplayID} {r.reservedLength:F0} m"));
                        continue;
                    }

                    foreach (var r in list)
                        if (r.track != null) { yto.ReleaseReservedSpace(r.track, r.reservedLength); released++; }
                    dict[def] = new List<TrackReservation>();
                }

                Stats.ReservationsDeferred += released;
                Main.Log($"Reservation (chain created/loaded): {(keep?.job?.ID ?? "new chain")} keeps [{string.Join(", ", kept)}], {released} follow-up reservation(s) released.");
            }
            catch (Exception ex)
            {
                Main.WarningOnce("res-first", "Trimming reservations to the first job failed, vanilla reservations stay: " + ex.Message);
            }
        }

        /// <summary>
        /// Postfix auf JobChainController.AddJobDefinitionToChain: leeren Reservierungs-Eintrag anlegen,
        /// damit die Vanilla-Freigabe am Jobende nicht ins Leere greift, wenn (noch) nicht reserviert wurde.
        /// </summary>
        public static void AddJobDefinitionToChain_Postfix(JobChainController __instance, object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0 || __args[0] == null) return;
                if (!(Reflect.Get<object>(__instance, "jobDefToCurrentlyReservedTracks") is IDictionary dict)) return;
                if (dict.Contains(__args[0])) return;

                var valueType = dict.GetType().GetGenericArguments().ElementAtOrDefault(1);
                if (valueType != null) dict[__args[0]] = Activator.CreateInstance(valueType);
            }
            catch (Exception ex)
            {
                Main.WarningOnce("res-dict", "Could not prepare reservation entry: " + ex.Message);
            }
        }

        /// <summary>Postfix auf JobsManager.TakeJob(Job job, bool takenViaLoadGame).</summary>
        public static void TakeJob_Postfix(object[] __args)
        {
            if (__args == null || __args.Length == 0 || !(__args[0] is Job job)) return;
            if (!Active && !FirstLinkMode) return;
            Runtime.Spawner?.StartCoroutine(ReserveWhenChainKnown(job));
        }

        // ------------------------------------------------------------------ Logik

        private static IEnumerator ReserveWhenChainKnown(Job job)
        {
            // Beim Laden eines Spielstands ist die Kette evtl. noch nicht beim Bahnhof registriert -> kurz warten
            float start = Time.realtimeSinceStartup;
            JobChainController chain = null;
            while (chain == null && Time.realtimeSinceStartup - start < 10f)
            {
                chain = FindChain(job);
                if (chain == null) yield return null;
            }

            if (chain == null)
            {
                Main.Warning($"Reservation: no job chain found for {job.ID} - no track space reserved.");
                yield break;
            }

            if (FirstLinkMode)
            {
                // nur das jetzt anstehende Glied - ist es schon reserviert (erstes Glied oder Buendelung), passiert nichts
                var def = TrackAssignment.ChainDefinitions(chain).FirstOrDefault(d => d != null && d.job == job);
                if (def != null) ReserveDefinition(chain, def, false, "job accepted");
                yield break;
            }

            if (!ReservedChains.Add(chain)) yield break; // Folgejob derselben Kette: schon reserviert
            DoReserveWholeChain(chain, job);
        }

        /// <summary>Glied der Kette, das jetzt ansteht (aktueller Job, sonst das erste).</summary>
        private static StaticJobDefinition CurrentDefinition(JobChainController chain)
        {
            var defs = TrackAssignment.ChainDefinitions(chain).Where(d => d != null).ToList();
            if (defs.Count == 0) return null;
            var current = chain.currentJobInChain;
            return (current != null ? defs.FirstOrDefault(d => d.job == current) : null) ?? defs[0];
        }

        internal static Dictionary<StaticJobDefinition, List<TrackReservation>> ReservationDict(JobChainController chain)
            => Reflect.Get<Dictionary<StaticJobDefinition, List<TrackReservation>>>(chain, "jobDefToCurrentlyReservedTracks");

        internal static bool HasReservation(JobChainController chain, StaticJobDefinition def)
        {
            var dict = ReservationDict(chain);
            return dict != null && def != null && dict.TryGetValue(def, out var list) && list != null && list.Count > 0;
        }

        /// <summary>Reserviert genau ein Glied der Kette (sofern noch nicht geschehen) und traegt es fuer die Vanilla-Freigabe ein.</summary>
        private static void ReserveDefinition(JobChainController chain, StaticJobDefinition def, bool ignoreOccupied, string reason)
        {
            var dict = ReservationDict(chain);
            if (dict == null)
            {
                Main.WarningOnce("res-dict-type", "Reservation table of job chains not readable - reservations may be incomplete.");
                return;
            }
            if (dict.TryGetValue(def, out var existing) && existing != null && existing.Count > 0) return;

            // Entladeauftraege reservieren keine Gleise (Ausweichen auf andere S-Gleise ist erlaubt)
            if (def is StaticShuntingUnloadJobDefinition)
            {
                if (!dict.ContainsKey(def)) dict[def] = new List<TrackReservation>();
                return;
            }

            var wanted = def.GetRequiredTrackReservations() ?? new List<TrackReservation>();
            var yto = YardTracksOrganizer.Instance;
            foreach (var r in wanted)
                if (r.track != null) yto.ReserveSpace(r.track, r.reservedLength, ignoreOccupied);

            dict[def] = new List<TrackReservation>(wanted);

            if (wanted.Count == 0)
                Main.Log($"Reservation ({reason}): {def.job?.ID ?? "?"} - the game reports no track to reserve.");
            else
                Main.Log($"Reservation ({reason}): {def.job?.ID ?? "new chain"} -> " +
                         string.Join(", ", wanted.Select(w => $"{w.track?.ID.FullDisplayID} {w.reservedLength:F0} m")));
        }

        private static void DoReserveWholeChain(JobChainController chain, Job job)
        {
            _reserveMethod ??= Reflect.Method(typeof(JobChainController), "ReserveRequiredTracks");
            if (_reserveMethod == null)
            {
                Main.WarningOnce("res-method", "JobChainController.ReserveRequiredTracks not found - reservations disabled.");
                return;
            }

            try
            {
                var args = _reserveMethod.GetParameters()
                    .Select(p => p.ParameterType == typeof(bool) ? (object)true
                                 : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                    .ToArray();

                _allowVanillaReserve = true;
                _reserveMethod.Invoke(chain, args);
                Main.Log($"Reservation: track space reserved for {job.ID} on acceptance.");
            }
            catch (Exception ex)
            {
                Main.Warning($"Reservation for {job.ID} failed: {ex.InnerException?.Message ?? ex.Message}");
            }
            finally
            {
                _allowVanillaReserve = false;
            }
        }

        internal static JobChainController FindChain(Job job)
        {
            var stations = StationController.allStations;
            if (stations == null) return null;
            foreach (var sc in stations)
            {
                var chains = sc != null ? sc.ProceduralJobsController?.GetCurrentJobChains() : null;
                if (chains == null) continue;
                foreach (var c in chains)
                    if (c != null && c.currentJobInChain == job) return c;
            }
            return null;
        }
    }
}
