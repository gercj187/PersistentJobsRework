using System;
using System.Linq;
using DV.Logic.Job;
using DV.ThingTypes;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// Ausliegende (noch nicht angenommene) Jobs in den Papierkorb werfen: Der Job verfaellt, die Jobkette
    /// endet, die Wagen bleiben stehen und sind jobfrei - mit der naechsten vollen Spielstunde bekommen sie eine
    /// neue Jobkette (Nachfuellen). Angenommene Jobs (Auftragsbuch) behandelt das Spiel wie bisher.
    /// </summary>
    internal static class TrashAvailableJobs
    {
        /// <summary>Postfix auf JobAbandoner.OnTriggerEnter(Collider).</summary>
        public static void OnTriggerEnter_Postfix(Collider other)
        {
            if (!Main.IsActive || other == null) return;
            try
            {
                var overview = other.GetComponent<JobOverview>();
                if (overview == null) return;
                var job = overview.job;
                if (job == null || job.State != JobState.Available) return;

                // Client: der Host entscheidet ueber Jobs -> Anfrage schicken, Uebersicht lokal entfernen
                if (!Main.IsAuthority)
                {
                    if (TrackSync.SendToServer("TRASH", job.ID))
                    {
                        Destroy(overview);
                        Main.Log($"Job {job.ID} thrown away - request sent to the host.");
                    }
                    return;
                }

                string id = job.ID;
                JobAcceptance.WithExpireAllowed(() => job.ExpireJob());
                Destroy(overview);
                Main.Log($"Job {id} thrown away (not taken) - its cars are jobless and get a new job chain with the next full game hour.");
            }
            catch (Exception ex) { Main.WarningOnce("trash-overview", "Throwing away an available job failed: " + ex.Message); }
        }

        private static void Destroy(JobOverview overview)
        {
            var destroy = typeof(JobOverview).GetMethod("DestroyJobOverview",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (destroy != null) destroy.Invoke(overview, null);
            else UnityEngine.Object.Destroy(overview.gameObject);
        }

        /// <summary>Host: ein Client hat einen ausliegenden Job weggeworfen.</summary>
        public static void OnClientRequest(string jobId, string sender)
        {
            if (string.IsNullOrEmpty(jobId)) return;
            try
            {
                Job job = null;
                foreach (var sc in StationController.allStations)
                {
                    var jobs = sc?.logicStation?.availableJobs;
                    if (jobs == null) continue;
                    job = jobs.FirstOrDefault(j => j != null && j.ID == jobId);
                    if (job != null) break;
                }
                if (job == null || job.State != JobState.Available)
                {
                    Main.Log($"Job {jobId}: throw-away request from {sender ?? "client"} ignored (job not available anymore).");
                    return;
                }
                JobAcceptance.WithExpireAllowed(() => job.ExpireJob());
                // gedruckte Uebersicht beim Host ebenfalls entfernen
                foreach (var ov in UnityEngine.Object.FindObjectsOfType<JobOverview>())
                    if (ov != null && ov.job == job) Destroy(ov);
                Main.Log($"Job {jobId} thrown away by {sender ?? "a client"} - its cars are jobless and get a new job chain with the next full game hour.");
            }
            catch (Exception ex) { Main.WarningOnce("trash-client", "Throwing away a job for a client failed: " + ex.Message); }
        }
    }
}
