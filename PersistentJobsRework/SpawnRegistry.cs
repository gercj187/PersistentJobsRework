using System.Collections.Generic;
using System.Linq;

namespace PersistentJobsRework
{
    /// <summary>Merkt sich, welche Bahnhoefe ihre Wagen bereits gespawnt haben (wird im Spielstand gespeichert).</summary>
    internal static class SpawnRegistry
    {
        private static readonly HashSet<string> _spawned = new HashSet<string>();

        public static int Count => _spawned.Count;

        public static bool HasSpawned(string stationId) => stationId != null && _spawned.Contains(stationId);

        public static void MarkSpawned(string stationId)
        {
            if (stationId != null) _spawned.Add(stationId);
        }

        public static void Unmark(string stationId)
        {
            if (stationId != null) _spawned.Remove(stationId);
        }

        public static void Clear() => _spawned.Clear();

        public static void SetAll(IEnumerable<string> ids)
        {
            _spawned.Clear();
            if (ids == null) return;
            foreach (var id in ids)
                if (!string.IsNullOrEmpty(id)) _spawned.Add(id);
        }

        public static List<string> GetAll() => _spawned.OrderBy(s => s).ToList();
    }
}
