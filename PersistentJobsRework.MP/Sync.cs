using MPAPI;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;

namespace PersistentJobsRework.MP
{
    /// <summary>
    /// Kleine Zusatz-DLL: alles, was Typen aus MultiplayerAPI braucht (vor allem die Paketklasse).
    /// Wird von der Haupt-DLL nur geladen, wenn der Multiplayer-Mod aktiv ist.
    /// </summary>
    public static class Entry
    {
        public static void Init() => TrackSync.Register(new JobTrackSync());
    }

    /// <summary>Host -> alle Clients: "diese Tasks haben ab jetzt dieses Zielgleis".</summary>
    public class ClientBoundJobTrackPacket : IPacket
    {
        public string JobId { get; set; }
        public string TaskNetIds { get; set; }
        public string TrackId { get; set; }
    }

    internal sealed class JobTrackSync : IJobTrackSync
    {
        private IClient _registeredClient;

        public void Tick()
        {
            var client = MultiplayerAPI.Client;
            if (client == null || ReferenceEquals(client, _registeredClient)) return;
            if (MultiplayerAPI.Instance != null && MultiplayerAPI.Instance.IsHost) return;

            client.RegisterPacket<ClientBoundJobTrackPacket>(p => TrackSync.OnClientPacket(p.JobId, p.TaskNetIds, p.TrackId));
            _registeredClient = client;
        }

        public void Send(string jobId, string taskNetIds, string trackId)
        {
            MultiplayerAPI.Server?.SendPacketToAll(new ClientBoundJobTrackPacket
            {
                JobId = jobId,
                TaskNetIds = taskNetIds,
                TrackId = trackId
            }, true, true);
        }
    }
}
