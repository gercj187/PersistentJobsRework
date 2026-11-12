using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityModManagerNet;

namespace PersistentJobsRework
{
    /// <summary>
    /// Wer hat welchen Job angenommen? Wird fuer die Gleiswahl gebraucht:
    /// gleicher Spieler -> Jobs buendeln, anderer Spieler -> anderes Gleis.
    ///
    /// Quelle 1: DVMultiplayerIP (per Reflection, keine harte Abhaengigkeit):
    ///           ActorScope.SenderPersonal = Spieler, dessen Paket der Host gerade verarbeitet,
    ///           AccountStore.JobTakers   = Job-ID -> persoenliche ID des Annehmenden.
    /// Quelle 2: ohne DVMultiplayerIP der Spieler, der am naechsten am Jobdrucker steht.
    /// Singleplayer: immer "local".
    /// </summary>
    internal static class Owners
    {
        public const string Local = "local";

        private static readonly Dictionary<string, string> JobOwner = new Dictionary<string, string>();

        // ---- DVMultiplayerIP-Bruecke
        private static bool _dvmipChecked;
        private static FieldInfo _senderPersonal;      // static string ActorScope.SenderPersonal
        private static FieldInfo _jobTakers;           // static Dictionary<string,string> AccountStore.JobTakers
        private static string _dvmipHostId = "host";

        public static bool DvmipAvailable
        {
            get
            {
                InitDvmip();
                return _senderPersonal != null;
            }
        }

        private static void InitDvmip()
        {
            if (_dvmipChecked) return;
            _dvmipChecked = true;
            try
            {
                var mod = UnityModManager.FindMod("DVMultiplayerIP");
                if (mod == null || !mod.Enabled) return;

                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DVMultiplayerIP");
                if (asm == null) return;

                const BindingFlags st = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                _senderPersonal = asm.GetType("DVMultiplayerIP.ActorScope")?.GetField("SenderPersonal", st);
                var store = asm.GetType("DVMultiplayerIP.AccountStore");
                _jobTakers = store?.GetField("JobTakers", st);
                if (store?.GetField("HostId", st)?.GetValue(null) is string hostId) _dvmipHostId = hostId;

                Main.Log(_senderPersonal != null
                    ? "DVMultiplayerIP found - job owners are taken from its accounts."
                    : "DVMultiplayerIP found, but its ActorScope could not be read - using nearest player instead.");
            }
            catch (Exception ex)
            {
                Main.Warning("DVMultiplayerIP bridge failed: " + ex.Message);
                _senderPersonal = null;
            }
        }

        // ------------------------------------------------------------------ Abfragen

        /// <summary>Wer nimmt gerade (in diesem Moment, auf dem Host) einen Job an diesem Drucker an?</summary>
        public static string CurrentAcceptor(Vector3 validatorPosition)
        {
            if (!Mp.Available) return Local;

            if (DvmipAvailable)
            {
                try { return (_senderPersonal.GetValue(null) as string) ?? _dvmipHostId; }
                catch { /* Rueckfall unten */ }
            }

            return Mp.NearestPlayerKey(validatorPosition) ?? Local;
        }

        /// <summary>Besitzer eines bereits angenommenen Jobs (null = unbekannt).</summary>
        public static string OwnerOf(string jobId)
        {
            if (jobId == null) return null;
            if (JobOwner.TryGetValue(jobId, out var o)) return o;

            if (DvmipAvailable && _jobTakers?.GetValue(null) is Dictionary<string, string> takers &&
                takers.TryGetValue(jobId, out var t))
                return t;

            return Mp.Available ? null : Local;
        }

        public static void Set(string jobId, string owner)
        {
            if (jobId != null && owner != null) JobOwner[jobId] = owner;
        }

        public static void Remove(string jobId)
        {
            if (jobId != null) JobOwner.Remove(jobId);
        }

        // ------------------------------------------------------------------ Speichern

        public static void Clear() => JobOwner.Clear();

        public static JObject ToJson()
        {
            var o = new JObject();
            foreach (var kv in JobOwner) o[kv.Key] = kv.Value;
            return o;
        }

        public static void FromJson(JObject o)
        {
            JobOwner.Clear();
            if (o == null) return;
            foreach (var p in o.Properties())
                if (p.Value.Type == JTokenType.String) JobOwner[p.Name] = (string)p.Value;
        }
    }
}
