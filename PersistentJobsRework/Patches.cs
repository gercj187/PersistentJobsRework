using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text;
using DV.Logic.Job;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace PersistentJobsRework
{
    /// <summary>
    /// Alle Harmony-Patches. Bewusst manuell und einzeln abgesichert:
    /// - genau ein Durchlauf pro Spielstart (kein mehrfaches PatchAll)
    /// - Methoden werden per Reflection gesucht, NICHT per AccessTools (kein "Could not find"-Log-Spam)
    /// - schlaegt ein Patch fehl, laufen alle anderen trotzdem
    /// </summary>
    internal static class Patches
    {
        public const string SaveKey = "PersistentJobsRework";
        private const string LegacyPjSaveKey = "PersistentJobsMod";
        private const string LegacyPjSpawnListKey = "SpawnBlockList#trackHash";

        private const BindingFlags AnyMember =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static Harmony _harmony;
        private static int _ok;
        private static int _failed;

        public static void ApplyAll(Harmony harmony)
        {
            _harmony = harmony;

            // 1) Wagen niemals loeschen
            PatchByName(typeof(UnusedTrainCarDeleter), "AreDeleteConditionsFulfilled", postfix: nameof(AreDeleteConditionsFulfilled_Postfix));
            PatchByName(typeof(UnusedTrainCarDeleter), "InstantConditionalDeleteOfUnusedCars", prefix: nameof(InstantConditionalDelete_Prefix));

            // Diagnose: jede tatsaechliche Loeschung zaehlen und protokollieren
            PatchByName(typeof(CarSpawner), "DeleteTrainCars", prefix: nameof(DeleteTrainCars_Prefix), finalizer: nameof(DeleteTrainCars_Finalizer));
            PatchByName(typeof(CarSpawner), "DeleteCar", prefix: nameof(DeleteCar_Prefix));

            // 2) Bahnhof spawnt seine Wagen nur einmal (Generator selbst bleibt 100 % Vanilla)
            PatchByName(typeof(StationProceduralJobsController), "TryToGenerateJobs", prefix: nameof(TryToGenerateJobs_Prefix));

            // Job-ID auf den Infotafeln der Wagen (auch fuer Ketten dieser Mod)
            foreach (var m in PlateSync.Targets())
                PatchOne(m, "JobChainController." + m.Name, null, nameof(PlateSync.Generate_Postfix), null, Priority.Normal, typeof(PlateSync));

            // 3) Angebotene Jobs verfallen nicht, wenn man den Bahnhof verlaesst
            PatchByName(typeof(StationController), "ExpireAllAvailableJobsInStation", prefix: nameof(ExpireAllAvailableJobs_Prefix));

            // 4) Weltspawn (Debug): Bahnhof waehrend der Generierung kurz "in Reichweite"
            PatchGetter(typeof(StationJobGenerationRange), "PlayerSqrDistanceFromStationCenter", nameof(ForcedStationDistance_Prefix));
            PatchGetter(typeof(StationJobGenerationRange), "PlayerSqrDistanceFromStationOffice", nameof(ForcedStationDistance_Prefix));

            // 5) Speichern / Laden der Bahnhofs-Merker
            PatchByName(typeof(SaveGameManager), "Save", prefix: nameof(SaveGameManager_Save_Prefix));
            PatchByName(typeof(CarsSaveManager), "Load", postfix: nameof(CarsSaveManager_Load_Postfix));

            // 6) Neue Karriere erkennen
            var newCareerType = FindGameType("StartGameData_NewCareer");
            if (newCareerType != null)
                PatchByName(newCareerType, "PrepareNewSaveData", postfix: nameof(NewCareer_Postfix));
            else
                Fail("type StartGameData_NewCareer not found");

            // 6b) Lizenzen: waehrend der Generierung zaehlen fehlende Spieler-Lizenzen nicht
            var lic = typeof(LicenseBypass);
            var lmType = FindGameType("LicenseManager");
            if (lmType == null) Fail("type LicenseManager not found");
            else
            {
            PatchByName(lmType, "IsJobLicenseAcquired", prefix: nameof(LicenseBypass.BoolTrue_Prefix), owner: lic);
            PatchByName(lmType, "IsLicensedForJob", prefix: nameof(LicenseBypass.BoolTrue_Prefix), owner: lic);
            PatchByName(lmType, "GetMaxNumberOfCarsPerJobWithAcquiredJobLicenses", prefix: nameof(LicenseBypass.MaxCars_Prefix), owner: lic);
            }

            // 6c) Schlaf-Optimizer und Bremssimulation: eingefrorene Wagen in Ruhe lassen
            var opt = typeof(OptimizerPatches);
            var trainsOptimizer = FindGameType("TrainsOptimizer");
            if (trainsOptimizer != null)
                PatchByName(trainsOptimizer, "ForceOptimizationStateOnCars", prefix: nameof(OptimizerPatches.ForceStateOnCars_Prefix), owner: opt);
            else Fail("type TrainsOptimizer not found");
            var brakeset = FindGameType("Brakeset");
            if (brakeset != null)
                PatchByName(brakeset, "Tick", prefix: nameof(OptimizerPatches.BrakesetTick_Prefix), owner: opt);
            else Fail("type Brakeset not found");

            // 6d) Schadensschutz beim Spawnen
            var prot = typeof(SpawnProtection);
            var carDmg = FindGameType("CarDamageModel");
            if (carDmg != null) PatchByName(carDmg, "DamageCar", prefix: nameof(SpawnProtection.Damage_Prefix), owner: prot);
            else Fail("type CarDamageModel not found");
            var cargoDmg = FindGameType("CargoDamageModel");
            if (cargoDmg != null) PatchByName(cargoDmg, "ApplyDamageToCargo", prefix: nameof(SpawnProtection.Damage_Prefix), owner: prot);
            else Fail("type CargoDamageModel not found");

            // 7) Gleisplatz erst bei Annahme reservieren (v0.02)
            var res = typeof(Reservations);
            PatchByName(typeof(JobChainController), "ReserveRequiredTracks", prefix: nameof(Reservations.ReserveRequiredTracks_Prefix), postfix: nameof(Reservations.ReserveRequiredTracks_Postfix), owner: res);
            PatchByName(typeof(JobChainController), "AddJobDefinitionToChain", postfix: nameof(Reservations.AddJobDefinitionToChain_Postfix), owner: res);
            PatchByName(typeof(DV.Logic.Job.JobsManager), "TakeJob", postfix: nameof(Reservations.TakeJob_Postfix), owner: res);

            // 7b) Zielgleis erst bei Annahme waehlen (v0.03) - nach den Pruefungen von MP/DVMultiplayerIP
            foreach (var m in typeof(JobValidator).GetMethods(AnyMember).Where(m => m.Name == "ProcessJobOverview" && !m.IsAbstract))
            {
                // vorher: Wagen noch im Bahnhof? (sonst jetzt abgelaufen) + tatsaechliches Abholgleis
                PatchOne(m, "JobValidator.ProcessJobOverview (acceptance)", nameof(JobAcceptance.ProcessJobOverview_Prefix), null, null, Priority.LowerThanNormal, typeof(JobAcceptance));
                PatchOne(m, "JobValidator.ProcessJobOverview", nameof(TrackAssignment.ProcessJobOverview_Prefix), null, null, Priority.Low, typeof(TrackAssignment));
            }
            // 7b2) Verfuegbare Jobs verfallen nicht mehr, wenn Wagen ohne Job den Bahnhof verlassen
            PatchByName(typeof(DV.Logic.Job.Job), "ExpireJob", prefix: nameof(JobAcceptance.ExpireJob_Prefix), owner: typeof(JobAcceptance));
            PatchByName(typeof(DV.Logic.Job.JobsManager), "TakeJob", prefix: nameof(JobAcceptance.TakeJob_Prefix), owner: typeof(JobAcceptance));

            // 7c) Start-Auslastung: waehrend der Generierung nur X % jedes Gleises nutzbar (v0.03)
            var cap = typeof(Capacity);
            var genType = FindGameType("StationProceduralJobGenerator");
            if (genType != null)
            {
                PatchByName(genType, "GenerateJobChain", prefix: nameof(Capacity.GenerateJobChain_Prefix), postfix: nameof(Capacity.GenerateJobChain_Postfix), owner: cap);
                // Lizenz-Umgehung NUR innerhalb dieses Aufrufs
                PatchByName(genType, "GenerateJobChain", prefix: nameof(LicenseBypass.Generator_Enter), finalizer: nameof(LicenseBypass.Generator_Exit), owner: typeof(LicenseBypass));
            }
            else
                Fail("type StationProceduralJobGenerator not found - job limit only via car spawns, licenses are NOT ignored");
            PatchByName(typeof(YardTracksOrganizer), "GetFreeSpaceOnTrack", postfix: nameof(Capacity.GetFreeSpace_Postfix), owner: cap);
            PatchByName(typeof(YardTracksOrganizer), "FilterOutTracksWithoutRequiredFreeSpace", postfix: nameof(Capacity.Filter_Postfix), owner: cap);
            foreach (var m in typeof(CarSpawner).GetMethods(AnyMember)
                         .Where(m => !m.IsAbstract && m.Name.StartsWith("SpawnCarTypesOnTrack") && m.ReturnType == typeof(System.Collections.Generic.List<TrainCar>)))
                PatchOne(m, "CarSpawner." + m.Name, nameof(Capacity.SpawnGuard_Prefix), nameof(Capacity.SpawnDone_Postfix), null, Priority.Normal, cap);
            foreach (var m in typeof(YardTracksOrganizer).GetMethods(AnyMember)
                         .Where(m => !m.IsAbstract && m.ReturnType == typeof(DV.Logic.Job.Track) && m.Name.IndexOf("FreeSpace", StringComparison.OrdinalIgnoreCase) >= 0))
                PatchOne(m, "YardTracksOrganizer." + m.Name, null, nameof(Capacity.PickTrack_Postfix), null, Priority.Normal, cap);

            // 8) Ausweichgleise mit Abzug (v0.02)
            var alt = typeof(AlternativeTracks);
            var update = typeof(DV.Logic.Job.TransportTask).GetMethods(AnyMember).FirstOrDefault(m => m.Name == "UpdateTaskState" && !m.IsAbstract);
            if (update == null)
                Fail("TransportTask.UpdateTaskState not found - alternative tracks disabled");
            else if (update.ReturnType == typeof(DV.Logic.Job.TaskState))
                PatchOne(update, "TransportTask.UpdateTaskState", null, nameof(AlternativeTracks.UpdateTaskState_Postfix_WithResult), null, Priority.Normal, alt);
            else
                PatchOne(update, "TransportTask.UpdateTaskState", null, nameof(AlternativeTracks.UpdateTaskState_Postfix_Void), null, Priority.Normal, alt);

            bool wagePatched = PatchByName(typeof(DV.Logic.Job.Job), "GetWageForTheJob", postfix: nameof(AlternativeTracks.GetWage_Postfix), owner: alt);
            if (!wagePatched)
            {
                Main.Warning("Using fallback for the payout deduction (initialWage is reduced on job completion).");
                PatchByName(typeof(DV.Logic.Job.Job), "CompleteJob", prefix: nameof(AlternativeTracks.CompleteJob_Prefix_ScaleWage), owner: alt);
            }
            PatchByName(typeof(DV.Logic.Job.Job), "CompleteJob", prefix: nameof(AlternativeTracks.CompleteJob_Prefix_Redirect), postfix: nameof(AlternativeTracks.CompleteJob_Postfix), owner: alt);

            // 9) Auftragsbericht: Abzug fuers Ausweichgleis als eigene Zeile (wie LongerLoadingDelay)
            var reportCreator = FindGameType("BookletCreator_JobReport");
            if (reportCreator != null) PatchByName(reportCreator, "GetReportTemplateData", postfix: nameof(ReportPatch.TemplateData_Postfix), owner: typeof(ReportPatch));
            else Fail("BookletCreator_JobReport not found - no deduction line in the job report");
            var reportPaper = FindGameType("JobReportPaymentTemplatePaper");
            if (reportPaper != null) PatchByName(reportPaper, "FillInData", postfix: nameof(ReportPatch.FillInData_Postfix), owner: typeof(ReportPatch));

            Main.Log($"Harmony: {_ok} patch(es) applied, {_failed} failed.");
        }

        // ================================================================== Patch-Helfer

        /// <returns>true, wenn mindestens eine Methode gepatcht wurde</returns>
        private static bool PatchByName(Type type, string methodName, string prefix = null, string postfix = null, string finalizer = null, Type owner = null)
        {
            MethodInfo[] targets;
            try
            {
                targets = type.GetMethods(AnyMember).Where(m => m.Name == methodName && !m.IsAbstract && !m.IsGenericMethodDefinition).ToArray();
            }
            catch (Exception ex)
            {
                Fail($"{type.Name}.{methodName}: {ex.Message}");
                return false;
            }

            if (targets.Length == 0)
            {
                Fail($"{type.Name}.{methodName} not found");
                return false;
            }

            bool any = false;
            foreach (var target in targets)
                any |= PatchOne(target, $"{type.Name}.{methodName}", prefix, postfix, finalizer, Priority.Normal, owner);
            return any;
        }

        private static void PatchGetter(Type type, string propertyName, string prefix)
        {
            MethodInfo getter = null;
            try
            {
                getter = type.GetProperty(propertyName, AnyMember)?.GetGetMethod(true);
            }
            catch (Exception ex)
            {
                Fail($"{type.Name}.{propertyName}: {ex.Message}");
                return;
            }

            if (getter == null)
            {
                Fail($"getter {type.Name}.{propertyName} not found");
                return;
            }

            // Priority.Last: laeuft nach dem Multiplayer-Prefix und darf dessen Ergebnis ueberschreiben
            PatchOne(getter, $"{type.Name}.{propertyName}", prefix, null, null, Priority.Last);
        }

        private static bool PatchOne(MethodInfo target, string label, string prefix, string postfix, string finalizer, int priority, Type owner = null)
        {
            try
            {
                owner ??= typeof(Patches);
                _harmony.Patch(target,
                    prefix: Hm(owner, prefix, priority),
                    postfix: Hm(owner, postfix, priority),
                    finalizer: Hm(owner, finalizer, priority));
                _ok++;
                return true;
            }
            catch (Exception ex)
            {
                Fail($"{label}: {ex.Message}");
                return false;
            }
        }

        private static HarmonyMethod Hm(Type owner, string name, int priority)
        {
            if (name == null) return null;
            var mi = owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (mi == null) throw new MissingMethodException(owner.Name, name);
            return new HarmonyMethod(mi) { priority = priority };
        }

        private static void Fail(string msg)
        {
            _failed++;
            Main.Warning("Patch skipped: " + msg);
        }

        internal static Type FindGameTypePublic(string name) => FindGameType(name);

        private static Type FindGameType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = asm.GetName().Name;
                if (asmName != "Assembly-CSharp" && !asmName.StartsWith("DV.")) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }

                foreach (var t in types)
                    if (t != null && t.Name == name) return t;
            }
            return null;
        }

        // ================================================================== 1) nie loeschen

        private static void AreDeleteConditionsFulfilled_Postfix(ref bool __result)
        {
            if (__result && Main.IsAuthority && Main.Settings.NeverDeleteCars)
            {
                __result = false;
                Stats.DeleteChecksBlocked++;
            }
        }

        private static bool InstantConditionalDelete_Prefix()
        {
            if (Main.IsAuthority && Main.Settings.NeverDeleteCars)
            {
                Stats.InstantDeletesBlocked++;
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ Diagnose

        private static bool _inDeleteBatch;
        private static int _deleteLogLines;

        private static void DeleteTrainCars_Prefix(object[] __args)
        {
            _inDeleteBatch = true;
            int count = (__args != null && __args.Length > 0 && __args[0] is ICollection col) ? col.Count : 1;
            RecordDeletion(count, "DeleteTrainCars");
        }

        private static Exception DeleteTrainCars_Finalizer(Exception __exception)
        {
            _inDeleteBatch = false;
            return __exception;
        }

        private static void DeleteCar_Prefix(object[] __args)
        {
            if (_inDeleteBatch) return;
            string id = (__args != null && __args.Length > 0 && __args[0] is TrainCar tc && tc != null) ? tc.ID : "?";
            RecordDeletion(1, "DeleteCar " + id);
        }

        private static void RecordDeletion(int count, string what)
        {
            // Kurzer Aufruf-Stack, damit man sieht, WER loescht (Spieler per Funkgeraet, Spiel, andere Mod ...)
            var sb = new StringBuilder();
            bool gameUnload = false;
            try
            {
                var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
                if (frames != null)
                    foreach (var f in frames.Take(8))
                    {
                        var m = f.GetMethod();
                        if (m == null) continue;
                        if (m.Name == "OnGameUnload" || m.Name == "OnApplicationQuit") gameUnload = true;
                        sb.Append(" <- ").Append(m.DeclaringType?.Name).Append('.').Append(m.Name);
                    }
            }
            catch { /* egal */ }

            // Beim Verlassen der Welt raeumt das Spiel alle Wagen ab - das ist keine echte Loeschung
            if (gameUnload)
            {
                Main.Log($"{count} car(s) unloaded together with the world (normal, not counted).");
                return;
            }

            Stats.CarsDeleted += count;
            if (!Main.IsActive || _deleteLogLines >= 50) return;
            _deleteLogLines++;
            Main.Log($"[Delete] {what} ({count} car(s)){sb}");
        }

        // ================================================================== 2) Einmal-Spawn

        private static bool TryToGenerateJobs_Prefix(StationProceduralJobsController __instance)
        {
            if (!Main.IsAuthority || __instance == null) return true;

            string id = __instance.stationController?.logicStation?.ID;

            // nach dem harten Reset: bis zum Neuladen gar nichts erzeugen (auch nicht Vanilla)
            if (WorldInit.ResetPending)
            {
                Stats.GenerationsBlocked++;
                return false;
            }

            if (Main.Settings.OneTimeStationSpawn && id != null)
            {
                if (SpawnRegistry.HasSpawned(id))
                {
                    if (WorldSpawner.ExtraPassStation == id)
                    {
                        Capacity.BeginGeneration(__instance); // Zusatz-Durchlauf des Weltspawns: Limit gilt weiter
                        return true;
                    }
                    Stats.GenerationsBlocked++;
                    return false;
                }

                SpawnRegistry.MarkSpawned(id);
                Main.Log($"Station {id}: first car spawn - vanilla job generation runs (max. {Main.Settings.StationFillLimitPercent:0}% of freight tracks).");

                // Hat der Bahnhof am Ende gar nichts erzeugt (z. B. fehlende Lizenzen), Merker wieder entfernen,
                // damit er spaeter noch einmal spawnen darf.
                Runtime.Spawner?.VerifyFirstGeneration(__instance, id);
            }

            // Waehrend der Generierung nur X % jedes Gleises als nutzbar melden
            Capacity.BeginGeneration(__instance);
            return true;
        }

        // ================================================================== 3) kein Verfall

        private static bool ExpireAllAvailableJobs_Prefix()
        {
            if (Main.IsAuthority && Main.Settings.KeepJobsAvailable && !WorldInit.AllowExpire)
            {
                Stats.ExpiriesBlocked++;
                return false;
            }
            return true;
        }

        // ================================================================== 4) Weltspawn

        private static bool ForcedStationDistance_Prefix(StationJobGenerationRange __instance, ref float __result)
        {
            var forced = WorldSpawner.ForcedRange;
            if (forced != null && ReferenceEquals(__instance, forced))
            {
                __result = 0f;
                return false;
            }
            return true;
        }

        // ================================================================== 5) Speichern / Laden

        private static void SaveGameManager_Save_Prefix(SaveGameManager __instance)
        {
            if (!Main.IsAuthority) return;
            try
            {
                var data = new JObject(
                    new JProperty("Version", new JValue(Main.Mod.Info.Version)),
                    new JProperty("SpawnedStations", new JArray(SpawnRegistry.GetAll().Select(s => new JValue(s)))),
                    new JProperty("JobOwners", Owners.ToJson()),
                    new JProperty(WorldInit.FlagKey, new JValue(WorldInit.Initialized)),
                    new JProperty(JobRefill.SaveKey, new JValue(double.IsNaN(JobRefill.LastRefill) ? 0.0 : JobRefill.LastRefill)));
                __instance.data.SetJObject(SaveKey, data);
            }
            catch (Exception ex)
            {
                Main.Warning("Saving mod data failed: " + ex.Message);
            }
        }

        private static void CarsSaveManager_Load_Postfix()
        {
            Reservations.Reset();
            AlternativeTracks.Reset();
            Owners.Clear();
            JobRefill.LastRefill = double.NaN;
            try
            {
                var saveData = SaveGameManager.Instance?.data;
                if (saveData == null) return;

                var own = saveData.GetJObject(SaveKey);
                if (own != null)
                {
                    var arr = own["SpawnedStations"] as JArray;
                    SpawnRegistry.SetAll(arr?.Select(t => (string)t));
                    Owners.FromJson(own["JobOwners"] as JObject);
                    var lr = own[JobRefill.SaveKey];
                    if (lr != null && (lr.Type == JTokenType.Float || lr.Type == JTokenType.Integer) && (double)lr > 0)
                        JobRefill.LastRefill = (double)lr;
                    Main.Log($"Loaded spawn flags for {SpawnRegistry.Count} station(s).");
                    bool? flag = own[WorldInit.FlagKey]?.Type == JTokenType.Boolean ? (bool?)(bool)own[WorldInit.FlagKey] : null;
                    if (Main.IsAuthority) WorldInit.OnLoaded(flag);
                    return;
                }

                // Uebernahme aus einem Spielstand mit Banjobenis PersistentJobsMod
                var legacy = saveData.GetJObject(LegacyPjSaveKey);
                if (legacy?[LegacyPjSpawnListKey] is JArray legacyArr)
                {
                    SpawnRegistry.SetAll(legacyArr.Select(t => (string)t));
                    Main.Log($"Imported spawn flags of {SpawnRegistry.Count} station(s) from a PersistentJobsMod savegame.");
                    if (Main.IsAuthority) WorldInit.OnLoaded(null);
                    return;
                }

                SpawnRegistry.Clear();
                Main.Log("No spawn flags in this savegame.");
                if (Main.IsAuthority) WorldInit.OnLoaded(null);
            }
            catch (Exception ex)
            {
                Main.Warning("Loading mod data failed: " + ex.Message);
            }
        }

        // ================================================================== 6) neue Karriere

        private static void NewCareer_Postfix()
        {
            if (!Main.IsAuthority) return;

            SpawnRegistry.Clear();
            Reservations.Reset();
            AlternativeTracks.Reset();
            Owners.Clear();
            JobRefill.LastRefill = double.NaN;
            Main.Log("New career detected - station spawn flags reset.");
            WorldInit.OnNewCareer();
        }
    }
}
