using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DV;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// Friert stehende Zuggarnituren ein, wenn kein Spieler in der Naehe ist.
    /// Die Wagen bleiben IMMER in der Welt (Gleisbelegung bleibt fuer andere Mods sichtbar),
    /// nur ihre Physik (Stufe 1) bzw. zusaetzlich ihre Skripte (Stufe 2, experimentell) werden pausiert.
    /// Laeuft nur im Singleplayer bzw. beim MP-Host.
    /// </summary>
    internal sealed class CarFreezer : MonoBehaviour
    {
        private sealed class FrozenCar
        {
            public readonly List<KeyValuePair<Rigidbody, bool>> Bodies = new List<KeyValuePair<Rigidbody, bool>>();
            public readonly List<Behaviour> DisabledBehaviours = new List<Behaviour>();
            public bool OptimizedByUs;
            public object BrakeSystem;
            public Car LogicCar;
        }

        private readonly Dictionary<TrainCar, FrozenCar> _frozen = new Dictionary<TrainCar, FrozenCar>();

        /// <summary>Logik-Wagen, die wir eingefroren haben - der Optimizer des Spiels darf sie nicht wecken.</summary>
        public static readonly HashSet<Car> FrozenLogicCars = new HashSet<Car>();
        /// <summary>BrakeSystem-Objekte eingefrorener Wagen - ihre Bremssimulation wird pausiert.</summary>
        public static readonly HashSet<object> FrozenBrakeSystems = new HashSet<object>();
        private readonly List<Vector3> _movingAnchors = new List<Vector3>();
        private readonly Dictionary<Type, bool> _protectedTypeCache = new Dictionary<Type, bool>();
        private string _protectedCacheKey;
        private string[] _protectedParts = new string[0];

        private bool _scanRequested;
        private float _nextScan;

        // ---- Werte fuer HUD / Log
        public int FrozenCount => _frozen.Count;
        public int TotalCars { get; private set; }
        public int TotalSets { get; private set; }
        public double LastScanCpuMs { get; private set; }
        public bool IsRunning { get; private set; }

        /// <summary>Bis zu diesem Zeitpunkt (realtimeSinceStartup) wird nichts eingefroren (z. B. nach Weltspawn).</summary>
        public static float SuspendedUntil;

        public void RequestScan() => _scanRequested = true;

        private void Start()
        {
            StartCoroutine(Loop());
        }

        // ================================================================== Hauptschleife

        private IEnumerator Loop()
        {
            while (true)
            {
                yield return null;

                bool shouldRun = ShouldRun();
                IsRunning = shouldRun;

                if (!shouldRun)
                {
                    if (_frozen.Count > 0) WakeAll("freezer inactive");
                    continue;
                }

                if (!_scanRequested && Time.realtimeSinceStartup < _nextScan) continue;
                _scanRequested = false;
                _nextScan = Time.realtimeSinceStartup + Main.Settings.ScanInterval;

                IEnumerator scan = Scan();
                while (true)
                {
                    bool more;
                    try
                    {
                        more = scan.MoveNext();
                    }
                    catch (Exception ex)
                    {
                        Main.Error("Freeze scan failed: " + ex);
                        break;
                    }
                    if (!more) break;
                    yield return scan.Current;
                }
            }
        }

        private static bool ShouldRun()
        {
            if (!Main.IsAuthority || !Main.Settings.FreezeEnabled) return false;
            if (!WorldStreamingInit.IsLoaded || PlayerManager.PlayerTransform == null) return false;
            if (WorldSpawner.Running && Main.Settings.AwakeDuringWorldSpawn) return false;
            if (Time.realtimeSinceStartup < SuspendedUntil) return false;
            return true;
        }

        // ================================================================== Scan

        private IEnumerator Scan()
        {
            var cpu = new Stopwatch();
            var slice = Stopwatch.StartNew();
            const double frameBudgetMs = 1.0;

            cpu.Start();
            RemoveDestroyedCars();

            var sets = Trainset.allSets?.ToArray();
            if (sets == null)
            {
                cpu.Stop();
                yield break;
            }

            // Positionen fahrender, nicht eingefrorener Wagen (z. B. wegrollende Zuege)
            _movingAnchors.Clear();
            if (Main.Settings.WakeNearMovingTrains)
            {
                foreach (var set in sets)
                {
                    if (set?.cars == null) continue;
                    foreach (var car in set.cars)
                        if (car != null && !_frozen.ContainsKey(car) && Speed(car) > MovingSpeed)
                            _movingAnchors.Add(car.transform.position);
                }
            }

            float freezeSqr = Main.Settings.FreezeDistance * Main.Settings.FreezeDistance;
            float wakeSqr = Main.Settings.WakeDistance * Main.Settings.WakeDistance;
            float movingSqr = Main.Settings.MovingTrainWakeDistance * Main.Settings.MovingTrainWakeDistance;
            bool ignoreDistance = Main.Settings.DebugIgnoreDistance;
            TrainCar playerCar = PlayerManager.Car;

            int totalCars = 0;
            _whyPlayerCar = _whyPlayerNear = _whyMoving = _whyMovingNear = _whyDerailed = _whyCooldown = _whyPartial = 0;

            foreach (var set in sets)
            {
                var cars = set?.cars;
                if (cars != null && cars.Count > 0)
                {
                    totalCars += cars.Count;
                    EvaluateSet(cars, freezeSqr, wakeSqr, movingSqr, ignoreDistance, playerCar);
                }

                if (slice.Elapsed.TotalMilliseconds > frameBudgetMs)
                {
                    cpu.Stop();
                    yield return null;
                    slice.Reset();
                    slice.Start();
                    cpu.Start();
                }
            }

            cpu.Stop();
            TotalCars = totalCars;
            TotalSets = sets.Length;
            LastScanCpuMs = cpu.Elapsed.TotalMilliseconds;

            // Zustand ins Log, aber nur bei Aenderung und hoechstens alle 10 s
            if (_frozen.Count != _lastLoggedFrozen && Time.realtimeSinceStartup >= _nextStatusLog)
            {
                _lastLoggedFrozen = _frozen.Count;
                _nextStatusLog = Time.realtimeSinceStartup + 10f;
                Main.Log($"Freezer: {_frozen.Count} of {totalCars} cars frozen ({sets.Length} sets), scan {LastScanCpuMs:F1} ms CPU. " +
                         $"Awake because: player near {_whyPlayerNear}, player's train {_whyPlayerCar}, moving {_whyMoving}, " +
                         $"moving train near {_whyMovingNear}, derailed {_whyDerailed}, just woken {_whyCooldown}, partly frozen {_whyPartial}.");
            }
        }

        private int _lastLoggedFrozen = -1;
        private int _whyPlayerCar, _whyPlayerNear, _whyMoving, _whyMovingNear, _whyDerailed, _whyCooldown, _whyPartial;

        // Schwellen: "steht" = unter 0,3 m/s, "faehrt" (weckt Nachbarn) = ueber 1 m/s. isStationary des Spiels
        // war zu empfindlich - frisch gespawnte Wagen zittern minimal und hielten sich gegenseitig wach.
        private const float StandingSpeed = 0.3f;
        private const float MovingSpeed = 1.0f;
        // Nach dem Wecken mindestens so lange wach lassen - verhindert staendiges Ein-/Ausfrieren (Ruckler)
        private const float WakeCooldown = 30f;
        private readonly Dictionary<TrainCar, float> _lastWake = new Dictionary<TrainCar, float>();

        private static float Speed(TrainCar car)
        {
            try { return Mathf.Abs(car.GetForwardSpeed()); }
            catch { return car.isStationary ? 0f : 10f; }
        }
        private float _nextStatusLog;

        private void EvaluateSet(List<TrainCar> cars, float freezeSqr, float wakeSqr, float movingSqr, bool ignoreDistance, TrainCar playerCar)
        {
            int frozenInSet = 0;
            bool allStationary = true;
            bool anyDerailed = false;
            bool containsPlayerCar = false;
            bool recentlyWoken = false;
            float nearestPlayer = float.MaxValue;
            float nearestMoving = float.MaxValue;

            for (int i = 0; i < cars.Count; i++)
            {
                var car = cars[i];
                if (car == null) continue;

                bool isFrozen = _frozen.ContainsKey(car);
                if (isFrozen) frozenInSet++;
                else if (Speed(car) > StandingSpeed) allStationary = false;
                if (!isFrozen && _lastWake.TryGetValue(car, out float woke) && Time.realtimeSinceStartup - woke < WakeCooldown) recentlyWoken = true;

                if (car.derailed) anyDerailed = true;
                if (playerCar != null && car == playerCar) containsPlayerCar = true;

                Vector3 pos = car.transform.position;
                float d = Mp.NearestPlayerSqrDistance(pos);
                if (d < nearestPlayer) nearestPlayer = d;

                for (int a = 0; a < _movingAnchors.Count; a++)
                {
                    float m = (_movingAnchors[a] - pos).sqrMagnitude;
                    if (m < nearestMoving) nearestMoving = m;
                }
            }

            bool movingTrainClose = nearestMoving < movingSqr;

            // Garnitur teils eingefroren, teils nicht (z. B. neu gekuppelt) -> sicherheitshalber alles wecken
            if (frozenInSet > 0 && frozenInSet < cars.Count)
            {
                WakeCars(cars);
                _whyPartial += cars.Count;
                return;
            }

            if (frozenInSet > 0)
            {
                bool wake = containsPlayerCar || movingTrainClose || (!ignoreDistance && nearestPlayer < wakeSqr);
                if (wake) { WakeCars(cars); return; }
                // von aussen geweckt (z. B. Kupplungs-Nachbar) und steht trotzdem -> wieder schlafen legen
                foreach (var c in cars)
                    if (c != null && c.rb != null && !c.rb.IsSleeping() && Speed(c) < StandingSpeed)
                        try { c.ForceOptimizationState(true); } catch { }
                return;
            }

            bool playerFar = ignoreDistance || nearestPlayer > freezeSqr;
            bool freeze = allStationary && !anyDerailed && !containsPlayerCar && !movingTrainClose && playerFar && !recentlyWoken;
            if (freeze) { FreezeCars(cars); return; }

            // Grund, warum die Garnitur wach bleibt (fuers Log) - erster zutreffender Grund zaehlt
            int n = cars.Count;
            if (containsPlayerCar) _whyPlayerCar += n;
            else if (!playerFar) _whyPlayerNear += n;
            else if (!allStationary) _whyMoving += n;
            else if (movingTrainClose) _whyMovingNear += n;
            else if (anyDerailed) _whyDerailed += n;
            else if (recentlyWoken) _whyCooldown += n;
        }

        // ================================================================== Einfrieren / Wecken

        private void FreezeCars(List<TrainCar> cars)
        {
            foreach (var car in cars)
                if (car != null && !_frozen.ContainsKey(car))
                    FreezeCar(car);
        }

        private void WakeCars(List<TrainCar> cars)
        {
            foreach (var car in cars)
                if (car != null) WakeCar(car);
        }

        private void FreezeCar(TrainCar car)
        {
            var fc = new FrozenCar();

            // a) Schlafzustand des Spiels (Rigidbodies schlafen, Schwerkraft aus). Die Drehgestelle brechen ihr
            //    FixedUpdate bei schlafendem Rigidbody sofort ab. Damit der Optimizer des Spiels den Wagen nicht
            //    wieder weckt (er weckt ganze Gleisgruppen, sobald irgendwo darin etwas faehrt), steht er in
            //    FrozenLogicCars - siehe Patch auf TrainsOptimizer.ForceOptimizationStateOnCars.
            try
            {
                car.ForceOptimizationState(true);
                fc.OptimizedByUs = true;
            }
            catch (Exception ex)
            {
                Main.WarningOnce("opt-sleep", "ForceOptimizationState(true) failed: " + ex.Message);
            }

            // b) optional (alt): Rigidbodies zusaetzlich kinematisch. Standard aus - kinematische Koerper "schlafen"
            //    nicht, dadurch liefen die Drehgestell-Skripte voll weiter und schalteten die Schwerkraft wieder ein.
            if (Main.Settings.FreezeKinematic)
            {
                foreach (var rb in car.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb == null) continue;
                    bool wasKinematic = rb.isKinematic;
                    if (!wasKinematic)
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        rb.isKinematic = true;
                    }
                    fc.Bodies.Add(new KeyValuePair<Rigidbody, bool>(rb, wasKinematic));
                }
            }

            // c) Stufe 2 (experimentell): Skripte normaler Gueterwagen pausieren
            if (Main.Settings.FreezeLevel2 && CarTypes.IsRegularCar(car.carLivery))
            {
                foreach (var b in car.GetComponentsInChildren<Behaviour>(true))
                {
                    if (b == null || !b.enabled || IsProtected(b)) continue;
                    b.enabled = false;
                    fc.DisabledBehaviours.Add(b);
                }
            }

            _frozen[car] = fc;
            fc.LogicCar = car.logicCar;
            if (car.logicCar != null) FrozenLogicCars.Add(car.logicCar);
            var bs = Reflect.Get<object>(car, "brakeSystem");
            if (bs != null) { FrozenBrakeSystems.Add(bs); fc.BrakeSystem = bs; }
        }

        private void WakeCar(TrainCar car)
        {
            if (!_frozen.TryGetValue(car, out var fc)) return;
            _frozen.Remove(car);
            _lastWake[car] = Time.realtimeSinceStartup;

            foreach (var b in fc.DisabledBehaviours)
                if (b != null) b.enabled = true;

            foreach (var kv in fc.Bodies)
                if (kv.Key != null && !kv.Value) kv.Key.isKinematic = false;

            if (fc.LogicCar != null) FrozenLogicCars.Remove(fc.LogicCar);
            if (fc.BrakeSystem != null) FrozenBrakeSystems.Remove(fc.BrakeSystem);

            if (fc.OptimizedByUs)
            {
                try
                {
                    car.ForceOptimizationState(false);
                }
                catch (Exception ex)
                {
                    Main.WarningOnce("opt-wake", "ForceOptimizationState(false) failed: " + ex.Message);
                }
            }
        }

        public void WakeAll(string reason)
        {
            if (_frozen.Count == 0) return;
            int n = _frozen.Count;
            foreach (var car in _frozen.Keys.ToList())
            {
                if (car != null) WakeCar(car);
                else _frozen.Remove(car);
            }
            _frozen.Clear();
            FrozenLogicCars.Clear();
            FrozenBrakeSystems.Clear();
            Main.Log($"Woke up {n} frozen car(s) ({reason}).");
        }

        private void RemoveDestroyedCars()
        {
            if (_lastWake.Count > 0)
            {
                float now = Time.realtimeSinceStartup;
                var old = _lastWake.Where(kv => kv.Key == null || now - kv.Value > WakeCooldown).Select(kv => kv.Key).ToList();
                foreach (var k in old) _lastWake.Remove(k);
            }
            List<TrainCar> dead = null;
            foreach (var car in _frozen.Keys)
                if (car == null) (dead ??= new List<TrainCar>()).Add(car);
            if (dead != null)
                foreach (var car in dead)
                {
                    var fc = _frozen[car];
                    if (fc.LogicCar != null) FrozenLogicCars.Remove(fc.LogicCar);
                    if (fc.BrakeSystem != null) FrozenBrakeSystems.Remove(fc.BrakeSystem);
                    _frozen.Remove(car);
                }
        }

        // ================================================================== Stufe 2: geschuetzte Komponenten

        private bool IsProtected(Behaviour b)
        {
            string key = Main.Settings.ProtectedComponentNames ?? "";
            if (key != _protectedCacheKey)
            {
                _protectedCacheKey = key;
                _protectedParts = key.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                _protectedTypeCache.Clear();
            }

            var type = b.GetType();
            if (_protectedTypeCache.TryGetValue(type, out bool cached)) return cached;

            bool result;
            string asm = type.Assembly.GetName().Name;
            // Nur Spiel-Komponenten anfassen - niemals Unity-, Multiplayer- oder andere Mod-Komponenten
            if (asm != "Assembly-CSharp" && !asm.StartsWith("DV."))
                result = true;
            else
                result = _protectedParts.Any(p => type.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

            _protectedTypeCache[type] = result;
            return result;
        }

        /// <summary>Schreibt die haeufigsten Komponenten-Typen auf wachen Gueterwagen ins Log (Entscheidungshilfe fuer Stufe 2).</summary>
        public void LogComponentStatistics()
        {
            try
            {
                var counts = new Dictionary<string, int>();
                int carCount = 0;
                foreach (var car in CarSpawner.Instance.AllCars)
                {
                    if (car == null || _frozen.ContainsKey(car) || !CarTypes.IsRegularCar(car.carLivery)) continue;
                    carCount++;
                    foreach (var b in car.GetComponentsInChildren<Behaviour>(true))
                    {
                        if (b == null) continue;
                        var t = b.GetType();
                        string name = $"{t.FullName} [{t.Assembly.GetName().Name}]{(b.enabled ? "" : " (disabled)")}";
                        counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;
                    }
                }

                Main.Log($"Component statistics over {carCount} awake freight car(s):");
                foreach (var kv in counts.OrderByDescending(k => k.Value).Take(40))
                    Main.Log($"  {kv.Value,6}x  {kv.Key}");
            }
            catch (Exception ex)
            {
                Main.Warning("Component statistics failed: " + ex.Message);
            }
        }
    }
}
