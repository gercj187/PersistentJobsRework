using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// Schutz beim Spawnen: Waehrend des Weltspawns, waehrend jeder Job-Erzeugung und bis 20 s danach nehmen
    /// abgestellte Wagen (Garnituren ohne Lok) und ihre Ladung keinen Schaden. Frisch gespawnte Wagen setzen
    /// sich kurz und koennen dabei an Nachbarn stossen - das darf keine Schaeden und damit keine Gebuehren erzeugen.
    /// Zuege mit Lok (der Zug des Spielers) werden nie geschuetzt.
    /// </summary>
    internal static class SpawnProtection
    {
        private const float AfterGenerationSeconds = 20f;
        private static float _protectUntil;
        public static long Blocked;

        /// <summary>Von Capacity aufgerufen, wenn eine Generierung endet.</summary>
        public static void GenerationEnded() => _protectUntil = Time.realtimeSinceStartup + AfterGenerationSeconds;

        public static bool Active =>
            Main.IsActive && Main.IsAuthority &&
            (WorldSpawner.Running || Capacity.IsGenerating || Time.realtimeSinceStartup < _protectUntil);

        /// <summary>Prefix auf CarDamageModel.DamageCar und CargoDamageModel.ApplyDamageToCargo.</summary>
        public static bool Damage_Prefix(object __instance)
        {
            if (!Active) return true;
            var car = Reflect.Get<TrainCar>(__instance, "trainCar");
            if (car == null || HasLoco(car)) return true;
            Blocked++;
            if (Blocked == 1 || Blocked % 50 == 0)
                Main.Log($"Spawn protection: damage to {car.ID} blocked ({Blocked} in total).");
            return false;
        }

        /// <summary>
        /// Nach dem Weltspawn zur Sicherheit: alle abgestellten Wagen (ohne Lok) und ihre Ladung voll reparieren.
        /// Nutzt per Reflection SetHealth / LoadCargoDamageState und das jeweilige Maximum (Feldname wird gesucht).
        /// </summary>
        public static void RepairParkedCars()
        {
            if (!Main.IsAuthority || CarSpawner.Instance == null) return;
            int carsFixed = 0, cargoFixed = 0;
            bool loggedMissing = false;
            foreach (var car in CarSpawner.Instance.AllCars)
            {
                if (car == null || HasLoco(car)) continue;
                try
                {
                    var dm = Reflect.Get<object>(car, "CarDamage");
                    if (dm != null && Repair(dm, "SetHealth")) carsFixed++;
                    var cm = Reflect.Get<object>(car, "CargoDamage");
                    if (cm != null && Repair(cm, "LoadCargoDamageState")) cargoFixed++;
                }
                catch (System.Exception ex)
                {
                    if (!loggedMissing) { loggedMissing = true; Main.Warning("Spawn protection: repair failed: " + ex.Message); }
                }
            }
            Main.Log($"Spawn protection: repaired {carsFixed} damaged car(s) and {cargoFixed} damaged cargo load(s) after the world spawn.");
        }

        private static bool Repair(object model, string setter)
        {
            float cur = Reflect.GetAny<float>(model, "currentHealth", "CurrentHealth");
            float max = Reflect.GetAny<float>(model, "maxHealth", "MaxHealth", "fullHealth", "startingHealth");
            if (max <= 0f || cur >= max - 0.01f) return false;
            var m = model.GetType().GetMethod(setter, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                                              null, new[] { typeof(float) }, null);
            if (m == null) return false;
            m.Invoke(model, new object[] { max });
            return true;
        }

        private static bool HasLoco(TrainCar car)
        {
            var set = car.trainset;
            if (set == null) return car.IsLoco;
            foreach (var c in set.cars)
                if (c != null && c.IsLoco) return true;
            return false;
        }
    }
}
