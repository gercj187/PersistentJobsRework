using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using DV.Logic.Job;

namespace PersistentJobsRework
{
    /// <summary>
    /// Diagnose fuer Bahnhoefe, die keine Jobs erzeugen ("Couldn't generate any player licensed job").
    /// Schreibt einmal pro Bahnhof und Sitzung das Regelwerk des Bahnhofs (Frachtgruppen, Wagenzahlen,
    /// erlaubte Auftragsarten), die dafuer noetigen Lizenzen und den freien Platz der Gleise ins Log.
    /// Rein lesend, per Reflection - Feldnamen des Spiels muessen nicht bekannt sein.
    /// </summary>
    internal static class StationDiagnostics
    {
        private static readonly HashSet<string> Dumped = new HashSet<string>();
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Reset() => Dumped.Clear();

        public static void DumpEmptyStation(StationProceduralJobsController controller, string stationId)
            => Dump(controller, stationId, "generated no jobs");

        public static void Dump(StationProceduralJobsController controller, string stationId, string reason)
        {
            if (controller == null || stationId == null || !Dumped.Add(stationId)) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[Diag] Station {stationId} {reason}. Details:");

                var station = controller.stationController;
                var ruleset = FindRuleset(controller) ?? FindRuleset(station);
                if (ruleset == null) sb.AppendLine("  ruleset: not found");
                else
                {
                    sb.AppendLine($"  ruleset ({ruleset.GetType().Name}):");
                    Describe(ruleset, sb, "    ", 0);
                    LicenseHints(ruleset, sb);
                }

                var yard = station?.logicStation?.yard;
                if (yard != null)
                {
                    TrackInfo("storage (S)", yard.StorageTracks, sb);
                    TrackInfo("inbound (I)", yard.TransferInTracks, sb);
                    TrackInfo("outbound (O)", yard.TransferOutTracks, sb);
                    var loading = Reflect.GetAny<IEnumerable>(yard, "LoadingTracks", "WarehouseTracks");
                    if (loading != null) TrackInfo("loading (L)", loading.OfType<Track>(), sb);
                }

                // Zielbahnhoefe der ausgehenden Fracht: freier Platz auf deren Eingangs- und Abstellgleisen
                var targets = new HashSet<StationController>();
                if (ruleset != null && Reflect.Get<IEnumerable>(ruleset, "outputCargoGroups") is IEnumerable groups)
                    foreach (var cg in groups)
                        if (cg != null && Reflect.Get<IEnumerable>(cg, "stations") is IEnumerable sts)
                            foreach (var st in sts.OfType<StationController>()) targets.Add(st);
                foreach (var st in targets)
                {
                    var ty = st?.logicStation?.yard;
                    if (ty == null) continue;
                    string tid = st.logicStation.ID;
                    TrackInfo($"target {tid} inbound (I)", ty.TransferInTracks, sb);
                    TrackInfo($"target {tid} storage (S)", ty.StorageTracks, sb);
                }

                Main.Log(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                Main.Warning($"[Diag] Station {stationId}: diagnostics failed: {ex.Message}");
            }
        }

        private static object FindRuleset(object owner)
        {
            if (owner == null) return null;
            for (var t = owner.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(F | BindingFlags.DeclaredOnly))
                    if (f.FieldType.Name.IndexOf("Ruleset", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var v = f.GetValue(owner);
                        if (v != null) return v;
                    }
            return null;
        }

        private static void Describe(object obj, StringBuilder sb, string indent, int depth)
        {
            if (obj == null || depth > 2) return;
            for (var t = obj.GetType(); t != null && t != typeof(object) && t.Name != "MonoBehaviour" && t.Name != "Behaviour"
                                        && t.Name != "Component" && t.Name != "Object"; t = t.BaseType)
            {
                foreach (var f in t.GetFields(F | BindingFlags.DeclaredOnly))
                {
                    if (f.IsStatic) continue;
                    object v;
                    try { v = f.GetValue(obj); } catch { continue; }

                    if (IsSimple(v)) { sb.AppendLine($"{indent}{f.Name} = {Short(v)}"); continue; }
                    if (v is IEnumerable list && !(v is string))
                    {
                        var items = list.Cast<object>().ToList();
                        if (items.All(IsSimple))
                        {
                            sb.AppendLine($"{indent}{f.Name} = [{string.Join(", ", items.Take(40).Select(Short))}]");
                            continue;
                        }
                        sb.AppendLine($"{indent}{f.Name}: {items.Count} entr(y/ies)");
                        int i = 0;
                        foreach (var it in items.Take(12))
                        {
                            sb.AppendLine($"{indent}  [{i++}] {it?.GetType().Name}");
                            Describe(it, sb, indent + "    ", depth + 1);
                        }
                    }
                }
            }
        }

        private static bool IsSimple(object v) =>
            v == null || v is string || v.GetType().IsPrimitive || v.GetType().IsEnum || v is decimal
            || v is StationController || v is Track || v is UnityEngine.Object;

        private static string Short(object v)
        {
            switch (v)
            {
                case null: return "null";
                case StationController sc: return sc.logicStation?.ID ?? sc.name;
                case Track tr: return tr.ID?.FullDisplayID ?? "track";
                case UnityEngine.Object uo: return uo ? uo.name : "null";
                case float fl: return fl.ToString("0.##");
                case double d: return d.ToString("0.##");
                default: return v.ToString();
            }
        }

        /// <summary>Fragt beim LicenseManager nach, welche Lizenzen Fracht und Wagenzahl des Bahnhofs verlangen.</summary>
        private static void LicenseHints(object ruleset, StringBuilder sb)
        {
            try
            {
                Type lmType = null;
                try { lmType = typeof(StationController).Assembly.GetTypes().FirstOrDefault(t => t.Name == "LicenseManager"); }
                catch (ReflectionTypeLoadException rtle) { lmType = rtle.Types.FirstOrDefault(t => t != null && t.Name == "LicenseManager"); }
                if (lmType == null) { sb.AppendLine("  licenses: LicenseManager not found"); return; }

                var inst = lmType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy)?.GetValue(null);
                var methods = lmType.GetMethods(F | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    .Where(m => m.Name.IndexOf("Required", StringComparison.OrdinalIgnoreCase) >= 0
                             || m.Name.IndexOf("Acquired", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                sb.AppendLine("  LicenseManager methods: " + string.Join(", ", methods.Select(m =>
                    m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")").Distinct()));

                // Wagenzahl -> noetige Lizenz
                var byCars = methods.FirstOrDefault(m => m.Name.IndexOf("NumberOfTransportedCars", StringComparison.OrdinalIgnoreCase) >= 0
                                                         && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));
                if (byCars != null)
                {
                    var parts = new List<string>();
                    foreach (int n in new[] { 1, 3, 5, 7, 10, 13, 16, 20, 25, 30 })
                    {
                        object r = null;
                        try { r = byCars.Invoke(byCars.IsStatic ? null : inst, new object[] { n }); } catch { }
                        parts.Add($"{n} cars -> {Short(r)}");
                    }
                    sb.AppendLine("  required license by car count: " + string.Join(", ", parts));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  licenses: " + ex.Message);
            }
        }

        private static void TrackInfo(string label, IEnumerable<Track> tracks, StringBuilder sb)
        {
            var list = tracks?.Where(t => t != null).ToList();
            if (list == null || list.Count == 0) { sb.AppendLine($"  {label}: none"); return; }
            var yto = YardTracksOrganizer.Instance;
            var parts = list.Select(t =>
            {
                string free = "?";
                try { if (yto != null) free = yto.GetFreeSpaceOnTrack(t).ToString("0"); } catch { }
                string len = Short(Reflect.GetAny<object>(t, "length", "Length"));
                return $"{t.ID.FullDisplayID} {len}m (free {free}m)";
            });
            sb.AppendLine($"  {label}: " + string.Join("; ", parts));
        }
    }
}
