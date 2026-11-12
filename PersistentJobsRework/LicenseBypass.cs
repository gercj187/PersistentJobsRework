namespace PersistentJobsRework
{
    /// <summary>
    /// Waehrend ein Bahnhof Jobs generiert, gelten alle Job-Lizenzen als vorhanden und die Zuglaenge ist
    /// nur durch das Regelwerk des Bahnhofs begrenzt. So bekommen auch Bahnhoefe wie IMW/IME in einer neuen
    /// Karriere ihre Wagen und Jobs. Die Jobs behalten ihre echten Lizenz-Anforderungen: Annehmen kann man
    /// sie weiterhin nur mit den passenden Lizenzen (Vanilla-Regel, auch mit DVMultiplayerIP).
    /// </summary>
    internal static class LicenseBypass
    {
        private static bool _logged;

        /// <summary>
        /// Nur WAEHREND eines einzelnen Generator-Aufrufs (StationProceduralJobGenerator.GenerateJobChain, synchron)
        /// gelten fehlende Lizenzen nicht. Vorher war es die ganze Erzeugung (mehrere Frames, beim Weltspawn
        /// minutenlang) - in der Zeit hatte der Spieler in der Karriere praktisch ALLE Lizenzen. Das darf nicht sein.
        /// </summary>
        private static int _depth;

        private static bool On =>
            _depth > 0 && Main.IsActive && Main.IsAuthority && Main.Settings.IgnoreLicensesForGeneration;

        /// <summary>Prefix auf GenerateJobChain (laeuft immer, auch wenn ein anderer Prefix den Aufruf verwirft).</summary>
        public static void Generator_Enter() => _depth++;

        /// <summary>Finalizer auf GenerateJobChain - laeuft immer, auch bei Exceptions.</summary>
        public static void Generator_Exit() { if (_depth > 0) _depth--; }

        /// <summary>Prefix fuer LicenseManager.IsJobLicenseAcquired / IsLicensedForJob.</summary>
        public static bool BoolTrue_Prefix(ref bool __result)
        {
            if (!On) return true;
            __result = true;
            Note();
            return false;
        }

        /// <summary>Prefix fuer LicenseManager.GetMaxNumberOfCarsPerJobWithAcquiredJobLicenses.</summary>
        public static bool MaxCars_Prefix(ref int __result)
        {
            if (!On) return true;
            // NIE einen Fantasiewert liefern (v0.05.06 gab 1000 -> Zuege mit bis zu 1000 Wagen, nichts passte mehr).
            // Stattdessen genau das Maximum aus dem Regelwerk des generierenden Bahnhofs.
            int max = Capacity.MaxCarsOfGeneratingStations();
            if (max <= 0) return true;
            __result = max;
            Note();
            return false;
        }

        private static void Note()
        {
            if (_logged) return;
            _logged = true;
            Main.Log("Job generation ignores missing player licenses (jobs keep their license requirements for taking them).");
        }
    }
}
