using System;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityModManagerNet;

namespace PersistentJobsRework
{
    /// <summary>
    /// Weiche Multiplayer-Anbindung. Alle Zugriffe auf MultiplayerAPI stecken in <see cref="MpInner"/>
    /// und werden nur aufgerufen, wenn der MP-Mod wirklich geladen ist. Ohne MP laeuft die Mod normal.
    /// </summary>
    internal static class Mp
    {
        private static bool _checked;
        private static bool _available;
        private static bool _compatRegistered;
        private static float _nextCompatTry;

        public static bool Available
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    _available = Detect();
                }
                return _available;
            }
        }

        private static bool Detect()
        {
            try
            {
                var mp = UnityModManager.FindMod("Multiplayer");
                if (mp == null || !mp.Enabled) return false;
                return AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "MultiplayerAPI");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True, wenn wir als MP-Client mit einem fremden Host verbunden sind.</summary>
        public static bool IsClient
        {
            get
            {
                if (!Available) return false;
                try { return MpInner.IsClient(); }
                catch { return false; }
            }
        }

        public static bool CanModifyWorld => !IsClient;

        /// <summary>
        /// Quadrierte Entfernung zum naechsten Spieler. Im MP-Host werden ALLE Spieler beruecksichtigt
        /// (Host + Clients), sonst nur der lokale Spieler. float.MaxValue = kein Spieler bekannt.
        /// </summary>
        public static float NearestPlayerSqrDistance(Vector3 position)
        {
            if (Available)
            {
                try
                {
                    if (MpInner.TryAnyPlayerSqrMag(position, out float d)) return d;
                }
                catch
                {
                    // ignorieren, Fallback auf lokalen Spieler
                }
            }

            var pt = PlayerManager.PlayerTransform;
            return pt == null ? float.MaxValue : (position - pt.position).sqrMagnitude;
        }

        /// <summary>Kennung des Spielers, der am naechsten an einer Position steht (MP-Host), sonst null.</summary>
        public static string NearestPlayerKey(Vector3 position)
        {
            if (!Available) return null;
            try { return MpInner.NearestPlayerKey(position); }
            catch { return null; }
        }

        /// <summary>Jeden Frame: Zusatz-DLL laden und Client-Paket-Handler anmelden.</summary>
        public static void Tick() => TrackSync.Tick();

        /// <summary>Host: neues Zielgleis an alle Clients schicken. Ohne MP: nichts zu tun.</summary>
        public static void SendJobTrack(DV.Logic.Job.Job job, System.Collections.Generic.IEnumerable<DV.Logic.Job.Task> tasks, DV.Logic.Job.Track track)
            => TrackSync.Send(job, tasks, track);

        /// <summary>Abholgleis (startingTrack) an die Clients - gleicher Kanal, Gleis-ID mit Praefix "S:".</summary>
        public static void SendJobStartTrack(DV.Logic.Job.Job job, System.Collections.Generic.IEnumerable<DV.Logic.Job.Task> tasks, DV.Logic.Job.Track track)
            => TrackSync.Send(job, tasks, track, "startingTrack");

        public static ushort TaskNetId(DV.Logic.Job.Task task)
        {
            if (!Available || task == null) return 0;
            try { return MpInner.TaskNetId(task); }
            catch { return 0; }
        }

        public static DV.Logic.Job.Task TaskFromNetId(ushort netId)
        {
            if (!Available || netId == 0) return null;
            try { return MpInner.TaskFromNetId(netId); }
            catch { return null; }
        }

        public static void TryRegisterCompatibility(string modId)
        {
            if (_compatRegistered || !Available) return;
            if (Time.realtimeSinceStartup < _nextCompatTry) return;
            _nextCompatTry = Time.realtimeSinceStartup + 5f;

            try
            {
                if (MpInner.RegisterCompatibility(modId))
                {
                    _compatRegistered = true;
                    Main.Log("Multiplayer compatibility registered (All).");
                }
            }
            catch (Exception ex)
            {
                _compatRegistered = true; // nicht endlos erneut versuchen
                Main.Warning("Could not register multiplayer compatibility: " + ex.Message);
            }
        }
    }

    /// <summary>Einziger Ort mit direkten MPAPI-Typen. Nie direkt aufrufen, nur ueber <see cref="Mp"/>.</summary>
    internal static class MpInner
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsClient()
        {
            var api = MPAPI.MultiplayerAPI.Instance;
            return api != null && MPAPI.MultiplayerAPI.Client != null && !api.IsHost;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryAnyPlayerSqrMag(Vector3 position, out float sqrDistance)
        {
            var server = MPAPI.MultiplayerAPI.Server;
            if (server == null)
            {
                sqrDistance = 0f;
                return false;
            }
            sqrDistance = server.AnyPlayerSqrMag(position);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ushort TaskNetId(DV.Logic.Job.Task task)
        {
            var api = MPAPI.MultiplayerAPI.Instance;
            return api != null && api.TryGetNetId<DV.Logic.Job.Task>(task, out ushort id) ? id : (ushort)0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static DV.Logic.Job.Task TaskFromNetId(ushort netId)
        {
            var api = MPAPI.MultiplayerAPI.Instance;
            return api != null && api.TryGetObjectFromNetId<DV.Logic.Job.Task>(netId, out var t) ? t : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string NearestPlayerKey(Vector3 position)
        {
            var server = MPAPI.MultiplayerAPI.Server;
            if (server?.Players == null) return null;
            MPAPI.Interfaces.IPlayer best = null;
            float bestSqr = float.MaxValue;
            foreach (var p in server.Players)
            {
                if (p == null) continue;
                float d = (p.Position - position).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; best = p; }
            }
            if (best == null) return null;
            return best.IsHost ? "host" : "mp:" + best.Username;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool RegisterCompatibility(string modId)
        {
            var api = MPAPI.MultiplayerAPI.Instance;
            if (api == null) return false;
            // Ab v0.03 empfangen Clients die Zielgleise vom Host -> Mod muss bei allen installiert sein
            api.SetModCompatibility(modId, MPAPI.Types.MultiplayerCompatibility.All);
            return true;
        }
    }
}
