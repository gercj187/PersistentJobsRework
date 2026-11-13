using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using UnityEngine;
using Random = System.Random;

namespace PersistentJobsRework
{
    /// <summary>
    /// Mindestbestand je Wagentyp (Standard 20): Der Vanilla-Generator wuerfelt die Wagentypen ueber die Frachten der
    /// Bahnhoefe - dadurch gab es z. B. ueber 300 Flachwagen (CFF), aber nur 3 Rungenwagen (CFS) und gar keine
    /// Chemie-Kesselwagen (CCH). Am Ende des Weltspawns zaehlt diese Klasse alle Wagentypen, die irgendein Bahnhof
    /// beladen kann, und spawnt fehlende Wagen LEER auf S-Gleisen eines Bahnhofs, der sie beladen kann. Danach
    /// vergibt das Nachfuellen (JobRefill) ihnen Beladejobs. Alles darueber bleibt dem Zufall des Generators.
    /// Personen- und Lokomotiven zaehlen nicht (keine Frachtjobs).
    /// </summary>
    internal static class MinStock
    {
        private static readonly Random Rng = new Random();
        public static bool Running { get; private set; }

        /// <summary>Wagentyp -> Bahnhoefe, die eine darauf ladbare Fracht ausgeben.</summary>
        private static Dictionary<TrainCarType_v2, List<StationController>> LoadableTypes()
        {
            var map = new Dictionary<TrainCarType_v2, List<StationController>>();
            var cargoToCars = CargoToLoadableCarTypes();
            if (cargoToCars == null) { Main.WarningOnce("minstock-types", "Minimum stock: cargo/car type table not found."); return map; }
            foreach (var st in StationController.allStations)
            {
                var groups = st?.proceduralJobsRuleset?.outputCargoGroups;
                if (groups == null) continue;
                foreach (var cg in groups)
                    foreach (var ct in cg.cargoTypes ?? new List<CargoType>())
                    {
                        if (ct == CargoType.None) continue;
                        if (!(cargoToCars[ct.ToV2()] is IEnumerable carTypes)) continue;
                        foreach (var o in carTypes)
                        {
                            if (!(o is TrainCarType_v2 t) || t.liveries == null || t.liveries.Count == 0) continue;
                            if (!map.TryGetValue(t, out var list)) map[t] = list = new List<StationController>();
                            if (!list.Contains(st)) list.Add(st);
                        }
                    }
            }
            return map;
        }

        /// <summary>Globals.G.Types.CargoToLoadableCarTypes per Reflection (Fracht -> ladbare Wagentypen).</summary>
        private static IDictionary CargoToLoadableCarTypes()
        {
            try
            {
                const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var globals = Patches.FindGameTypePublic("Globals");
                object g = globals?.GetProperty("G", any)?.GetValue(null, null) ?? globals?.GetField("G", any)?.GetValue(null);
                object types = Reflect.GetAny<object>(g, "Types", "types");
                return Reflect.GetAny<object>(types, "CargoToLoadableCarTypes", "cargoToLoadableCarTypes") as IDictionary;
            }
            catch { return null; }
        }

        /// <summary>Fehlende Wagen spawnen. Laeuft als Coroutine-Schritt im Weltspawn oder ueber den Debug-Button.</summary>
        public static IEnumerator Fill(int minPerType)
        {
            if (Running || minPerType <= 0 || !Main.IsAuthority) yield break;
            Running = true;
            int spawnedTotal = 0;
            var summary = new List<string>();
            try
            {
                var loadable = LoadableTypes();
                var counts = new Dictionary<TrainCarType_v2, int>();
                foreach (var car in CarSpawner.Instance.AllCars)
                {
                    var t = car?.carLivery?.parentType;
                    if (t != null) counts[t] = (counts.TryGetValue(t, out int n) ? n : 0) + 1;
                }

                foreach (var kv in loadable.OrderBy(k => counts.TryGetValue(k.Key, out int c) ? c : 0))
                {
                    var type = kv.Key;
                    int have = counts.TryGetValue(type, out int h) ? h : 0;
                    int missing = minPerType - have;
                    if (missing <= 0) continue;

                    // alle passenden Bahnhoefe in zufaelliger Reihenfolge; ist ein Gleis zu kurz, kleinere Pakete (bis 1 Wagen)
                    int spawned = 0;
                    foreach (var station in kv.Value.OrderBy(_ => Rng.Next()))
                    {
                        if (spawned >= missing) break;
                        var rs = station.proceduralJobsRuleset;
                        int min = Math.Max(1, rs != null ? rs.minCarsPerJob : 3);
                        int max = Math.Max(min, rs != null ? rs.maxCarsPerJob : 8);
                        int cap = Main.Settings.InitialMaxCarsPerJob;   // erster Spawn: kurze Gruppen
                        if (cap > 0) { max = Math.Min(max, cap); min = Math.Min(min, max); }
                        int batch = Math.Min(missing - spawned, Rng.Next(min, max + 1));
                        while (spawned < missing && batch >= 1)
                        {
                            batch = Math.Min(batch, missing - spawned);
                            var liveries = Enumerable.Range(0, batch).Select(_ => type.liveries[Rng.Next(type.liveries.Count)]).ToList();
                            var track = FreeStorageTrack(station, liveries);
                            if (track == null) { batch--; continue; }   // kuerzer probieren, bei 0 naechster Bahnhof
                            var cars = Spawn(liveries, track);
                            if (cars == null || cars.Count == 0) break;
                            spawned += cars.Count;
                            yield return null;   // ein Spawn pro Frame
                        }
                    }

                    spawnedTotal += spawned;
                    summary.Add($"{type.name} {have}->{have + spawned}{(spawned < missing ? $" (no free storage track at {string.Join("/", kv.Value.Select(x => x.logicStation.ID))} for the rest)" : "")}");
                }
            }
            finally
            {
                Running = false;
                SpawnProtection.GenerationEnded();   // noch 20 s Schadensschutz nach dem letzten Spawn
                Main.Log(summary.Count > 0
                    ? $"Minimum stock ({minPerType} per car type): spawned {spawnedTotal} empty car(s) - {string.Join(", ", summary)}."
                    : $"Minimum stock ({minPerType} per car type): every loadable car type is present often enough.");
            }

            // die neuen leeren Wagen bekommen sofort Beladejobs
            // (waehrend des Weltspawns erst am Ende - das macht WorldSpawner)
            if (spawnedTotal > 0 && !WorldSpawner.Running) JobRefill.Start();
        }

        /// <summary>Freies S-Gleis des Bahnhofs, auf das die Wagen passen (kleinstes passendes zuerst).</summary>
        private static Track FreeStorageTrack(StationController station, List<TrainCarLivery> liveries)
        {
            float len = LiveriesLength(liveries);
            var yto = YardTracksOrganizer.Instance;
            return station.logicStation?.yard?.StorageTracks?
                .Where(t => t != null && yto.GetFreeSpaceOnTrack(t) >= len)
                .OrderBy(t => yto.GetFreeSpaceOnTrack(t))
                .FirstOrDefault();
        }

        private static MethodInfo _lenMethod;
        private static bool _lenLooked;

        /// <summary>Zuglaenge der Liveries: CarSpawner.GetTotalCarLiveriesLength per Reflection, sonst 20 m je Wagen.</summary>
        private static float LiveriesLength(List<TrainCarLivery> liveries)
        {
            try
            {
                if (!_lenLooked)
                {
                    _lenLooked = true;
                    _lenMethod = typeof(CarSpawner).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m => m.Name == "GetTotalCarLiveriesLength");
                }
                if (_lenMethod != null)
                {
                    var ps = _lenMethod.GetParameters();
                    var args = new object[ps.Length];
                    args[0] = liveries;
                    for (int i = 1; i < ps.Length; i++) args[i] = ps[i].ParameterType == typeof(bool) ? (object)true : (ps[i].HasDefaultValue ? ps[i].DefaultValue : null);
                    var r = _lenMethod.Invoke(_lenMethod.IsStatic ? null : CarSpawner.Instance, args);
                    if (r is float f) return f;
                    if (r is double d) return (float)d;
                }
            }
            catch { }
            return liveries.Count * 20f;
        }

        private static MethodInfo _spawn;

        /// <summary>CarSpawner.SpawnCarTypesOnTrackRandomOrientation (per Reflection, Parameter nach Typ belegt).</summary>
        private static List<TrainCar> Spawn(List<TrainCarLivery> liveries, Track track)
        {
            try
            {
                RailTrack rt = null;
                foreach (var r in RailTrackRegistry.Instance.AllTracks)
                    if (r != null && r.LogicTrack() == track) { rt = r; break; }
                if (rt == null) return null;

                if (_spawn == null)
                    _spawn = typeof(CarSpawner).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(m => m.Name == "SpawnCarTypesOnTrackRandomOrientation" && m.ReturnType == typeof(List<TrainCar>))
                        .OrderBy(m => m.GetParameters().Length).FirstOrDefault();
                if (_spawn == null) { Main.WarningOnce("minstock-spawn", "Minimum stock: CarSpawner.SpawnCarTypesOnTrackRandomOrientation not found."); return null; }

                var ps = _spawn.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    if (p.ParameterType == typeof(List<TrainCarLivery>)) args[i] = liveries;
                    else if (p.ParameterType == typeof(RailTrack)) args[i] = rt;
                    else if (p.ParameterType == typeof(bool))
                    {
                        string n = p.Name?.ToLowerInvariant() ?? "";
                        args[i] = n.Contains("couple") || n.Contains("handbrake") ? true : (p.HasDefaultValue ? p.DefaultValue : false);
                    }
                    else args[i] = p.HasDefaultValue ? p.DefaultValue : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null);
                }
                return _spawn.Invoke(CarSpawner.Instance, args) as List<TrainCar>;
            }
            catch (Exception ex)
            {
                Main.WarningOnce("minstock-spawn-ex", "Minimum stock: spawning failed: " + (ex.InnerException?.Message ?? ex.Message));
                return null;
            }
        }
    }
}
