using System.Collections;
using System.Collections.Generic;
using DV.Logic.Job;

namespace PersistentJobsRework
{
    /// <summary>
    /// Zusammenspiel mit dem Schlaf-Optimizer und der Bremssimulation des Spiels.
    ///
    /// TrainsOptimizer (Vanilla) laeuft jeden Frame ueber ALLE Wagen: Faehrt auf einem Gleis irgendetwas, weckt er alle
    /// Wagen auf allen damit verbundenen, belegten Gleisen - in einem vollen Bahnhof praktisch den ganzen Bahnhof.
    /// Wagen, die wir eingefroren haben, nimmt dieser Patch aus der Weck-Liste. Geweckt werden sie nur noch durch
    /// unseren Freezer (Spieler / fahrender Zug in der Naehe) oder echte Ereignisse (Kuppeln, Gleis geaendert ...).
    ///
    /// Brakeset.Tick (Vanilla) rechnet jeden Frame die Luftbremse aller Garnituren. Besteht eine Garnitur nur aus
    /// eingefrorenen Wagen, wird ihr Tick uebersprungen - beim Aufwecken laeuft sie mit dem alten Zustand weiter.
    /// </summary>
    internal static class OptimizerPatches
    {
        /// <summary>Prefix auf TrainsOptimizer.ForceOptimizationStateOnCars(HashSet&lt;Car&gt;, bool forceSleep, bool ...).</summary>
        public static void ForceStateOnCars_Prefix(object[] __args)
        {
            if (__args == null || __args.Length < 2 || CarFreezer.FrozenLogicCars.Count == 0) return;
            if (!(__args[1] is bool forceSleep) || forceSleep) return;          // nur das Wecken filtern
            if (__args[0] is HashSet<Car> set && set.Count > 0)
                set.ExceptWith(CarFreezer.FrozenLogicCars);
        }

        private static int _optFrame;

        /// <summary>
        /// Prefix auf TrainsOptimizer.Update: laeuft im Vanilla-Spiel jeden Frame ueber ALLE Wagen (Profil: 0,8 ms bei
        /// 750 Wagen). Ab 300 Wagen nur noch jeden 2.-4. Frame (je 250 Wagen ein Frame Abstand). Schlafen/Wecken
        /// reagiert damit hoechstens 3 Frames spaeter - Kollisionen wecken Rigidbodies ohnehin sofort.
        /// </summary>
        public static bool TrainsOptimizerUpdate_Prefix()
        {
            if (!Main.IsActive || !Main.Settings.ThrottleTrainsOptimizer) return true;
            int cars = CarSpawner.Instance != null ? CarSpawner.Instance.AllCars.Count : 0;
            if (cars < 300) return true;
            int every = UnityEngine.Mathf.Clamp(cars / 250, 2, 4);
            _optFrame++;
            if (_optFrame % every == 0) return true;
            Stats.OptimizerFramesSkipped++;
            return false;
        }

        /// <summary>Prefix auf Brakeset.Tick(float): ganze Garnitur eingefroren -> Bremssimulation pausieren.</summary>
        public static bool BrakesetTick_Prefix(object __instance)
        {
            if (!Main.Settings.PauseBrakesOfFrozenTrains || CarFreezer.FrozenBrakeSystems.Count == 0) return true;
            if (!(Reflect.Get<IList>(__instance, "cars") is IList cars) || cars.Count == 0) return true;
            for (int i = 0; i < cars.Count; i++)
                if (!CarFreezer.FrozenBrakeSystems.Contains(cars[i])) return true;
            Stats.BrakeTicksSkipped++;
            return false;
        }
    }
}
