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

    /// <summary>Client -> Host: Anfrage (z. B. "TRASH" = ausliegenden Job wegwerfen).</summary>
    public class ServerBoundJobRequestPacket : IPacket
    {
        public string Kind { get; set; }
        public string JobId { get; set; }
        public string Data { get; set; }
    }

    internal sealed class JobTrackSync : IJobTrackSync
    {
        private IClient _registeredClient;
        private IServer _registeredServer;

        public void Tick()
        {
            // Host: Anfragen der Clients annehmen
            var server = MultiplayerAPI.Server;
            if (server != null && !ReferenceEquals(server, _registeredServer))
            {
                server.RegisterPacket<ServerBoundJobRequestPacket>((p, sender) =>
                    TrackSync.OnServerPacket(p?.Kind, p?.JobId, p?.Data, sender?.Username));
                _registeredServer = server;
            }

            var client = MultiplayerAPI.Client;
            if (client == null || ReferenceEquals(client, _registeredClient)) return;
            if (MultiplayerAPI.Instance != null && MultiplayerAPI.Instance.IsHost) return;

            client.RegisterPacket<ClientBoundJobTrackPacket>(p => TrackSync.OnClientPacket(p.JobId, p.TaskNetIds, p.TrackId));
            _registeredClient = client;
        }

        public void SendToServer(string kind, string jobId, string data)
        {
            MultiplayerAPI.Client?.SendPacketToServer(new ServerBoundJobRequestPacket
            {
                Kind = kind,
                JobId = jobId,
                Data = data
            }, reliable: true);
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
