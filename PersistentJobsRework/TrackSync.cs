using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DV;
using DV.Logic.Job;
using DV.Utils;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// Schnittstelle zur kleinen Zusatz-DLL "PersistentJobsRework.MP.dll".
    /// Nur dort stehen Typen, die von MultiplayerAPI abhaengen (Paketklasse). So laedt die Haupt-DLL
    /// auch dann fehlerfrei, wenn der Multiplayer-Mod deaktiviert oder nicht installiert ist.
    /// Nur einfache Typen, damit die Zusatz-DLL nichts vom Spiel kennen muss.
    /// </summary>
    public interface IJobTrackSync
    {
        /// <summary>Jeden Frame: Client-Paket-Handler anmelden, sobald verbunden.</summary>
        void Tick();

        /// <summary>Host: an alle Clients senden.</summary>
        void Send(string jobId, string taskNetIds, string trackId);
    }

    /// <summary>Gleis-Sync Host -> Clients.</summary>
    public static class TrackSync
    {
        private const string SyncAssemblyFile = "PersistentJobsRework.MP.dll";

        private static IJobTrackSync _impl;
        private static bool _loadTried;

        /// <summary>Wird von der Zusatz-DLL aufgerufen (Entry.Init).</summary>
        public static void Register(IJobTrackSync impl)
        {
            _impl = impl;
            Main.Log("[MP] Track sync module loaded.");
        }

        internal static void Tick()
        {
            if (!Mp.Available) return;
            EnsureLoaded();
            try { _impl?.Tick(); }
            catch (Exception ex) { Main.WarningOnce("sync-tick", "[MP] Track sync tick failed: " + ex.Message); }
        }

        private static void EnsureLoaded()
        {
            if (_impl != null || _loadTried) return;
            _loadTried = true;
            try
            {
                string path = Path.Combine(Main.Mod.Path, SyncAssemblyFile);
                if (!File.Exists(path))
                {
                    Main.Error($"[MP] {SyncAssemblyFile} is missing - destination tracks can NOT be sent to clients!");
                    return;
                }
                var asm = Assembly.LoadFrom(path);
                asm.GetType("PersistentJobsRework.MP.Entry")?.GetMethod("Init", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                if (_impl == null) Main.Error($"[MP] {SyncAssemblyFile} could not be initialised.");
            }
            catch (Exception ex)
            {
                Main.Error($"[MP] Loading {SyncAssemblyFile} failed: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        // ------------------------------------------------------------------ Host

        internal static void Send(Job job, IEnumerable<Task> tasks, Track track, string field = "destinationTrack")
        {
            if (!Mp.Available || job == null || track == null) return;
            EnsureLoaded();
            if (_impl == null)
            {
                Main.Warning($"[MP] {job.ID}: track sync module not available - clients keep the old destination.");
                return;
            }

            var ids = tasks.Select(Mp.TaskNetId).Where(id => id != 0).Select(id => id.ToString()).ToList();
            if (ids.Count == 0)
            {
                Main.Warning($"[MP] No network ids for the tasks of {job.ID} - clients may show the old track.");
                return;
            }

            // Abholgleis: Praefix "S:" vor der Gleis-ID (die Zusatz-DLL reicht nur Texte durch, keine Aenderung dort noetig)
            string trackId = field == "startingTrack" ? "S:" + track.ID.FullID : track.ID.FullID;
            try { _impl.Send(job.ID, string.Join(",", ids), trackId); }
            catch (Exception ex) { Main.Warning("[MP] Sending the destination track failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------ Client

        /// <summary>Wird von der Zusatz-DLL aufgerufen, wenn das Paket beim Client ankommt.</summary>
        public static void OnClientPacket(string jobId, string taskNetIds, string trackId)
        {
            Runtime.Spawner?.StartCoroutine(ApplyOnClient(jobId, taskNetIds, trackId));
        }

        private static IEnumerator ApplyOnClient(string jobId, string taskNetIds, string trackId)
        {
            string field = "destinationTrack";
            if (trackId != null && trackId.StartsWith("S:", StringComparison.Ordinal))
            {
                field = "startingTrack";
                trackId = trackId.Substring(2);
            }
            var track = FindTrack(trackId);
            if (track == null)
            {
                Main.Warning($"[MP] Track {trackId} for {jobId} not found on this client.");
                yield break;
            }

            var pending = (taskNetIds ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => ushort.TryParse(s, out var v) ? v : (ushort)0)
                .Where(v => v != 0)
                .ToList();

            // Job/Tasks koennen beim Client noch unterwegs sein -> bis zu 10 s erneut versuchen
            float start = Time.realtimeSinceStartup;
            while (pending.Count > 0 && Time.realtimeSinceStartup - start < 10f)
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var task = Mp.TaskFromNetId(pending[i]);
                    if (task != null)
                    {
                        Reflect.Set(task, field, track);
                        pending.RemoveAt(i);
                    }
                }
                if (pending.Count > 0) yield return null;
            }

            if (pending.Count > 0)
                Main.Warning($"[MP] {pending.Count} task(s) of {jobId} not found - destination not updated.");
            else
                Main.Log($"[MP] {jobId}: {(field == "startingTrack" ? "pickup" : "destination")} track set to {track.ID.FullDisplayID} by host.");
        }

        private static Track FindTrack(string fullId)
        {
            if (string.IsNullOrEmpty(fullId)) return null;
            foreach (var rt in RailTrackRegistry.Instance.AllTracks)
            {
                var lt = rt != null ? rt.LogicTrack() : null;
                if (lt != null && lt.ID.FullID == fullId) return lt;
            }
            return null;
        }
    }
}
