using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.Logic.Job;
using DV.ThingTypes;

namespace PersistentJobsRework
{
    /// <summary>
    /// Job-ID auf der Infotafel der Wagen ("VEHICLE: ..."). Bei Ketten, die diese Mod selbst anlegt
    /// (Nachfuellen alle 6 Spielstunden), setzt das Spiel die Tafel nicht. Deshalb: nach jedem neu erzeugten
    /// Job einer Kette die aktuelle Job-ID auf alle Wagen der Kette schreiben - auch beim Laden eines Spielstands.
    /// Laeuft auf Host und Clients (reine Anzeige, die Tafel wird nicht ueber das Netz synchronisiert).
    /// </summary>
    internal static class PlateSync
    {
        /// <summary>Postfix auf alle JobChainController.Generate*Job*-Methoden (erster und folgende Jobs).</summary>
        public static void Generate_Postfix(JobChainController __instance)
        {
            try { Update(__instance); }
            catch (Exception ex) { Main.WarningOnce("plates", "Updating car plates failed: " + ex.Message); }
        }

        public static void Update(JobChainController chain)
        {
            var job = chain?.currentJobInChain;
            var cars = chain?.carsForJobChain;
            if (job == null || cars == null || cars.Count == 0) return;
            if (job.State != JobState.Available && job.State != JobState.InProgress) return;

            var wanted = new HashSet<Car>(cars.Where(c => c != null));
            foreach (var tc in CarSpawner.Instance.AllCars)
                if (tc != null && tc.logicCar != null && wanted.Contains(tc.logicCar))
                    tc.UpdateJobIdOnCarPlates(job.ID);
        }

        /// <summary>Ziel-Methoden: JobChainController-Methoden, deren Name mit "Generate" beginnt und "Job" enthaelt.</summary>
        public static IEnumerable<MethodInfo> Targets()
        {
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            return typeof(JobChainController).GetMethods(f)
                .Where(m => m.Name.StartsWith("Generate", StringComparison.Ordinal) && m.Name.Contains("Job")
                            && !m.IsAbstract && !m.IsGenericMethodDefinition);
        }
    }
}
