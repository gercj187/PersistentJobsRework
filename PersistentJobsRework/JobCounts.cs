using System.Collections;
using System.Linq;
using DV.Logic.Job;
using DV.ThingTypes;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>Job-Zaehler fuers Debug-Overlay (hoechstens einmal pro Sekunde neu gezaehlt).</summary>
    internal static class JobCounts
    {
        private static float _next;
        private static int _available, _taken, _shunting;

        public static int Available { get { Update(); return _available; } }
        public static int Taken { get { Update(); return _taken; } }
        public static int Shunting { get { Update(); return _shunting; } }

        private static void Update()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1f;
            int avail = 0, shunt = 0, taken = 0;
            try
            {
                if (StationController.allStations != null)
                    foreach (var sc in StationController.allStations)
                    {
                        var jobs = sc != null && sc.logicStation != null ? sc.logicStation.availableJobs : null;
                        if (jobs == null) continue;
                        foreach (var j in jobs)
                        {
                            if (j == null) continue;
                            avail++;
                            if (j.jobType == JobType.ShuntingLoad || j.jobType == JobType.ShuntingUnload) shunt++;
                        }
                    }
                // angenommene Jobs: JobsManager.currentJobs (per Reflection, Name je nach Spielversion)
                var jm = JobsManager.Instance;
                if (jm != null && Reflect.GetAny<object>(jm, "currentJobs", "CurrentJobs", "takenJobs") is ICollection list) taken = list.Count;
            }
            catch { /* Anzeige ist nur Debug - lieber 0 als eine Exception pro Sekunde */ }
            _available = avail;
            _shunting = shunt;
            _taken = taken;
        }
    }
}
