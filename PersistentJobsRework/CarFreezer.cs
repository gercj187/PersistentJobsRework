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
            // Tiefschlaf: nur ausgeschaltete Darstellung/Sound - Drehgestelle, Collider und Rigidbodies bleiben,
            // damit Achszaehler/Gleisbelegung (z. B. eigene Stellwerksmod) unveraendert besetzt melden
            public bool Deep;
            public readonly List<Renderer> DeepRenderers = new List<Renderer>();
            public readonly List<Behaviour> DeepBehaviours = new List<Behaviour>();
            public readonly List<Component> DeepParticles = new List<Component>();   // ParticleSystem per Reflection (kein Modul-Verweis noetig)
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
        public int DeepCount { get; private set; }
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
            _whyPlayerCar = _whyPlayerNear = _whyMoving = _whyMovingNear = _whyDerailed = _whyCooldown = _whyPartial = _whyLoco = 0;

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

            UpdateDeepSleep();

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
                         $"moving train near {_whyMovingNear}, derailed {_whyDerailed}, just woken {_whyCooldown}, partly frozen {_whyPartial}, " +
                         $"with loco {_whyLoco}.{Mp.PlayerSummary()}");
            }
        }

        private int _lastLoggedFrozen = -1;
        private int _whyPlayerCar, _whyPlayerNear, _whyMoving, _whyMovingNear, _whyDerailed, _whyCooldown, _whyPartial, _whyLoco;

        /// <summary>Lok, Tender oder Caboose in der Garnitur = Zug eines Spielers -> nie einfrieren (auch im MP sicher).</summary>
        private static bool IsTrain(TrainCar car)
        {
            if (car == null) return false;
            if (car.IsLoco) return true;
            string n = car.name ?? "";
            return n.StartsWith("Loco", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf("Tender", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Caboose", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Schwellen: "steht" = unter 0,3 m/s, "faehrt" (weckt Nachbarn) = ueber 1 m/s. isStationary des Spiels
        // war zu empfindlich - frisch gespawnte Wagen zittern minimal und hielten sich gegenseitig wach.
        private const float StandingSpeed = 0.3f;
        private const float MovingSpeed = 1.0f;
        // Nach dem Wecken mindestens so lange wach lassen - verhindert staendiges Ein-/Ausfrieren (Ruckler)
        private const float WakeCooldown = 30f;
        private readonly Dictionary<TrainCar, float> _lastWake = new Dictionary<TrainCar, float>();
        /// <summary>Erst einfrieren, wenn eine Garnitur so lange stillsteht (fahrende/gerade abgestellte Zuege bleiben wach).</summary>
        private const float StandStillBeforeFreeze = 120f;
        private readonly Dictionary<TrainCar, float> _lastMoved = new Dictionary<TrainCar, float>();
        /// <summary>
        /// Position je Wagen beim letzten Scan (ohne Weltverschiebung). Bewegung wird zusaetzlich ueber die
        /// Positionsaenderung erkannt: Im Multiplayer kann ein Zug, den ein Client steuert, auf dem Host als
        /// Marionette ohne Geschwindigkeit laufen - dann meldet GetForwardSpeed 0 und der Zug wuerde bei voller
        /// Fahrt eingefroren.
        /// </summary>
        private readonly Dictionary<TrainCar, Vector3> _lastPos = new Dictionary<TrainCar, Vector3>();
        private const float MovedDistance = 0.5f;

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
            bool hasLoco = false;
            Vector3 originShift = WorldOrigin.Shift();
            bool recentlyMoved = false;
            float nearestPlayer = float.MaxValue;
            float nearestMoving = float.MaxValue;

            for (int i = 0; i < cars.Count; i++)
            {
                var car = cars[i];
                if (car == null) continue;

                bool isFrozen = _frozen.ContainsKey(car);
                if (isFrozen) frozenInSet++;
                else if (Speed(car) > StandingSpeed) { allStationary = false; _lastMoved[car] = Time.realtimeSinceStartup; }

                Vector3 abs = car.transform.position - originShift;
                if (_lastPos.TryGetValue(car, out var lastAbs) && (abs - lastAbs).sqrMagnitude > MovedDistance * MovedDistance)
                {
                    allStationary = false;
                    _lastMoved[car] = Time.realtimeSinceStartup;
                }
                _lastPos[car] = abs;
                if (_lastMoved.TryGetValue(car, out float moved) && Time.realtimeSinceStartup - moved < StandStillBeforeFreeze) recentlyMoved = true;
                if (!isFrozen && _lastWake.TryGetValue(car, out float woke) && Time.realtimeSinceStartup - woke < WakeCooldown) recentlyWoken = true;

                if (car.derailed) anyDerailed = true;
                if (IsTrain(car)) hasLoco = true;
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

            // Zuege mit Lok/Tender/Caboose werden nie eingefroren: Im Multiplayer erkennt der Host sonst
            // u. U. nicht, dass ein Client an seinem Zug steht -> Wagen auf dem Host fest, beim Client wildes Ruckeln.
            if (hasLoco)
            {
                if (frozenInSet > 0) WakeCars(cars);
                _whyLoco += cars.Count;
                return;
            }

            if (frozenInSet > 0)
            {
                bool wake = containsPlayerCar || recentlyMoved || movingTrainClose || (!ignoreDistance && nearestPlayer < wakeSqr);
                if (wake) { WakeCars(cars); return; }
                // von aussen geweckt (z. B. Kupplungs-Nachbar) und steht trotzdem -> wieder schlafen legen
                foreach (var c in cars)
                    if (c != null && c.rb != null && !c.rb.IsSleeping() && Speed(c) < StandingSpeed)
                        try { c.ForceOptimizationState(true); } catch { }
                return;
            }

            bool playerFar = ignoreDistance || nearestPlayer > freezeSqr;
            bool freeze = allStationary && !recentlyMoved && !anyDerailed && !containsPlayerCar && !movingTrainClose && playerFar && !recentlyWoken;
            if (freeze) { FreezeCars(cars); return; }

            // Grund, warum die Garnitur wach bleibt (fuers Log) - erster zutreffender Grund zaehlt
            int n = cars.Count;
            if (containsPlayerCar) _whyPlayerCar += n;
            else if (!playerFar) _whyPlayerNear += n;
            else if (!allStationary || recentlyMoved) _whyMoving += n;
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

            if (fc.Deep) DeepWake(fc);
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

        // ================================================================== Tiefschlaf (Debug)

        private bool _lastBogieSetting;

        private void UpdateDeepSleep()
        {
            bool on = Main.Settings.DeepSleep;
            // Drehgestell-Schalter geaendert: alle Tiefschlaefer einmal wecken, beim naechsten Scan mit neuer Einstellung
            bool bogies = on && Main.Settings.DeepSleepBogies;
            if (bogies != _lastBogieSetting)
            {
                _lastBogieSetting = bogies;
                foreach (var fc in _frozen.Values) if (fc.Deep) DeepWake(fc);
            }
            float sleepSqr = Main.Settings.DeepSleepDistance * Main.Settings.DeepSleepDistance;
            float wakeDist = Mathf.Max(0f, Main.Settings.DeepSleepDistance - 200f);
            float wakeSqr = wakeDist * wakeDist;
            int deep = 0;
            foreach (var kv in _frozen)
            {
                var car = kv.Key;
                var fc = kv.Value;
                if (car == null) continue;
                if (!on)
                {
                    if (fc.Deep) DeepWake(fc);
                    continue;
                }
                float d = Mp.NearestPlayerSqrDistance(car.transform.position);
                if (!fc.Deep && d > sleepSqr) DeepSleepCar(car, fc);
                else if (fc.Deep && d < wakeSqr) DeepWake(fc);
                if (fc.Deep) deep++;
            }
            if (deep != DeepCount && (deep == 0 || DeepCount == 0 || Math.Abs(deep - DeepCount) >= 50))
                Main.Log($"Deep sleep: {deep} far car(s) hidden and muted (beyond {Main.Settings.DeepSleepDistance:0} m).");
            DeepCount = deep;
        }

        private static void DeepSleepCar(TrainCar car, FrozenCar fc)
        {
            try
            {
                foreach (var r in car.GetComponentsInChildren<Renderer>(true))
                    if (r != null && r.enabled) { r.enabled = false; fc.DeepRenderers.Add(r); }
                // AudioSource (AudioModule) ohne Modul-Verweis: ist ein Behaviour -> ueber den Typnamen
                foreach (var b in car.GetComponentsInChildren<Behaviour>(true))
                    if (b != null && b.enabled && b.GetType().Name == "AudioSource") { b.enabled = false; fc.DeepBehaviours.Add(b); }
                // optional: Drehgestelle abschalten (spart Bogie.FixedUpdate inkl. Postfixe anderer Mods, z. B.
                // Achszaehlung von DV_Signals). Die Wagen stehen, es gibt kein Ein-/Auszaehlen; der Zustand je
                // Drehgestell bleibt in der anderen Mod erhalten und laeuft beim Aufwachen an derselben Stelle weiter.
                if (Main.Settings.DeepSleepBogies)
                    foreach (var b in car.GetComponentsInChildren<Behaviour>(true))
                        if (b != null && b.enabled && b.GetType().Name == "Bogie") { b.enabled = false; fc.DeepBehaviours.Add(b); }
                foreach (var l in car.GetComponentsInChildren<Light>(true))
                    if (l != null && l.enabled) { l.enabled = false; fc.DeepBehaviours.Add(l); }
                foreach (var c in car.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c.GetType().Name != "ParticleSystem") continue;
                    if (!Reflect.Get<bool>(c, "isPlaying")) continue;
                    CallParticle(c, "Pause");
                    fc.DeepParticles.Add(c);
                }
                fc.Deep = true;
            }
            catch (Exception ex)
            {
                Main.WarningOnce("deep-sleep", "Deep sleep failed: " + ex.Message);
                DeepWake(fc);
            }
        }

        /// <summary>ParticleSystem.Pause(true) / Play(true) per Reflection.</summary>
        private static void CallParticle(Component ps, string name)
        {
            try
            {
                var m = ps.GetType().GetMethod(name, new[] { typeof(bool) });
                m?.Invoke(ps, new object[] { true });
            }
            catch { }
        }

        private static void DeepWake(FrozenCar fc)
        {
            foreach (var r in fc.DeepRenderers) if (r != null) r.enabled = true;
            foreach (var b in fc.DeepBehaviours) if (b != null) b.enabled = true;
            foreach (var p in fc.DeepParticles) if (p != null) CallParticle(p, "Play");
            fc.DeepRenderers.Clear();
            fc.DeepBehaviours.Clear();
            fc.DeepParticles.Clear();
            fc.Deep = false;
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
            if (_lastMoved.Count > 0)
            {
                float now = Time.realtimeSinceStartup;
                var oldMoved = _lastMoved.Where(kv => kv.Key == null || now - kv.Value > StandStillBeforeFreeze).Select(kv => kv.Key).ToList();
                foreach (var k in oldMoved) _lastMoved.Remove(k);
            }
            if (_lastPos.Count > 0)
            {
                var gone = _lastPos.Keys.Where(k => k == null).ToList();
                foreach (var k in gone) _lastPos.Remove(k);
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
