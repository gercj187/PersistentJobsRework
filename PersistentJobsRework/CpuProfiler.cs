using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// Debug-Messung: Wie viel CPU-Zeit pro Frame kosten die Skripte der Gueterwagen und die zentralen Systeme
    /// (Bremsen, Optimizer ...)? Patcht fuer 30 s Update/FixedUpdate/LateUpdate aller Komponenten-Typen, die an
    /// Wagen haengen, misst die Zeit und entfernt die Patches danach wieder. Ergebnis im Log.
    /// Damit sieht man, ob Einfrieren ueberhaupt an der richtigen Stelle spart.
    /// </summary>
    internal static class CpuProfiler
    {
        private const string HarmonyId = "PersistentJobsRework.CpuProfiler";
        private static readonly Dictionary<MethodBase, long> Ticks = new Dictionary<MethodBase, long>();
        private static readonly Dictionary<MethodBase, int> Calls = new Dictionary<MethodBase, int>();
        public static bool Running { get; private set; }

        private static readonly string[][] SystemMethods =
        {
            new[] { "DV.Simulation.Brake.Brakeset", "TickAll" },
            new[] { "DV.Simulation.Brake.HoseSeparationChecker", "Update" },
            new[] { "DV.Optimizers.TrainsOptimizer", "Update" },
            new[] { "TrainCar", "Update" },
            new[] { "TrainCar", "FixedUpdate" },
            new[] { "Bogie", "FixedUpdate" },
            new[] { "Bogie", "Update" },
        };

        public static void Start(float seconds = 30f)
        {
            if (Running || Runtime.Spawner == null) return;
            Runtime.Spawner.StartCoroutine(Run(seconds));
        }

        private static IEnumerator Run(float seconds)
        {
            Running = true;
            var harmony = new Harmony(HarmonyId);
            int patched = 0;
            try
            {
                var targets = new HashSet<MethodInfo>();

                // 1) Komponenten-Typen an allen Wagen (ohne Unity-eigene Typen)
                var types = new HashSet<Type>();
                foreach (var car in CarSpawner.Instance.AllCars)
                {
                    if (car == null) continue;
                    foreach (var b in car.GetComponentsInChildren<Behaviour>(true))
                        if (b != null) types.Add(b.GetType());
                }
                foreach (var t in types)
                {
                    string asm = t.Assembly.GetName().Name;
                    if (asm.StartsWith("UnityEngine") || asm.StartsWith("Unity.")) continue;
                    foreach (var name in new[] { "Update", "FixedUpdate", "LateUpdate" })
                    {
                        var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        if (m != null && !m.IsAbstract && !m.IsGenericMethodDefinition) targets.Add(m);
                    }
                }

                // 2) zentrale Systeme
                foreach (var sm in SystemMethods)
                {
                    var t = AccessToolsLite(sm[0]);
                    var m = t?.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                              .FirstOrDefault(x => x.Name == sm[1] && !x.IsAbstract && !x.IsGenericMethodDefinition);
                    if (m != null) targets.Add(m);
                }

                var pre = new HarmonyMethod(typeof(CpuProfiler).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
                var post = new HarmonyMethod(typeof(CpuProfiler).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var m in targets)
                {
                    try { harmony.Patch(m, pre, post); patched++; }
                    catch { /* manche Methoden lassen sich nicht patchen - egal */ }
                }
                Ticks.Clear();
                Calls.Clear();
                Main.Log($"CPU profiler: measuring {patched} method(s) for {seconds:0} s ...");
            }
            catch (Exception ex)
            {
                Main.Warning("CPU profiler could not start: " + ex.Message);
                harmony.UnpatchAll(HarmonyId);
                Running = false;
                yield break;
            }

            int frames = 0;
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < seconds)
            {
                frames++;
                yield return null;
            }
            float elapsed = Time.realtimeSinceStartup - start;

            harmony.UnpatchAll(HarmonyId);
            Running = false;

            try
            {
                double tickMs = 1000.0 / Stopwatch.Frequency;
                var fr = Runtime.Freezer;
                Main.Log($"CPU profiler result: {frames} frames in {elapsed:F1} s ({frames / Math.Max(0.01f, elapsed):F0} FPS, {elapsed * 1000f / Math.Max(1, frames):F1} ms per frame). " +
                         $"Cars {fr?.TotalCars} total, {fr?.FrozenCount} frozen. Cost per frame:");
                foreach (var kv in Ticks.OrderByDescending(k => k.Value).Take(30))
                {
                    double msPerFrame = kv.Value * tickMs / Math.Max(1, frames);
                    int calls = Calls.TryGetValue(kv.Key, out int c) ? c : 0;
                    Main.Log($"  {msPerFrame,7:F3} ms  {calls / Math.Max(1, frames),6} calls/frame  {kv.Key.DeclaringType?.FullName}.{kv.Key.Name}");
                }
                DebugHud.Notify(Loc.T("n_profile_done"));
            }
            catch (Exception ex)
            {
                Main.Warning("CPU profiler result failed: " + ex.Message);
            }
        }

        private static Type AccessToolsLite(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Postfix(long __state, MethodBase __originalMethod)
        {
            long d = Stopwatch.GetTimestamp() - __state;
            Ticks[__originalMethod] = Ticks.TryGetValue(__originalMethod, out long t) ? t + d : d;
            Calls[__originalMethod] = Calls.TryGetValue(__originalMethod, out int c) ? c + 1 : 1;
        }
    }
}
