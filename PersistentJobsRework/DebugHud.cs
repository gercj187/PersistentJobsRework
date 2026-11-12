using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>FPS-Anzeige + optionales CSV-Protokoll fuer den Performance-Test.</summary>
    internal sealed class DebugHud : MonoBehaviour
    {
        private const int HistorySeconds = 60;

        private float _accTime;
        private int _accFrames;
        private float _currentFps;
        private float _worstFrameThisSecond;
        private readonly float[] _fpsHistory = new float[HistorySeconds];
        private readonly float[] _worstFrameHistory = new float[HistorySeconds];
        private int _historyIndex;

        /// <summary>Keine Texteinblendungen im Bild mehr (Wunsch) - Hinweise stehen nur noch im Log.</summary>
        public static void Notify(string text)
        {
            Main.Log("[Note] " + text);
        }

        private GUIStyle _style;
        private string _text = "";
        private string _logPath;
        private bool _logHeaderWritten;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _accTime += dt;
            _accFrames++;
            if (dt > _worstFrameThisSecond) _worstFrameThisSecond = dt;

            if (_accTime < 1f) return;

            _currentFps = _accFrames / _accTime;

            // Ladebildschirme/Hauptmenue nicht in die Statistik nehmen (ein 4-s-Ladeframe verfaelscht sonst alles)
            bool inWorld = WorldStreamingInit.IsLoaded && PlayerManager.PlayerTransform != null;
            if (inWorld && !_wasInWorld) ClearHistory();
            _wasInWorld = inWorld;
            if (inWorld)
            {
                _fpsHistory[_historyIndex] = _currentFps;
                _worstFrameHistory[_historyIndex] = _worstFrameThisSecond;
                _historyIndex = (_historyIndex + 1) % HistorySeconds;
            }
            _accTime = 0f;
            _accFrames = 0;

            if (Main.Settings != null && Main.Settings.DebugMode) BuildText();
            if (Main.Settings != null && Main.Settings.PerfLog) WriteLogLine();

            _worstFrameThisSecond = 0f;
        }

        private bool _wasInWorld;

        private void ClearHistory()
        {
            Array.Clear(_fpsHistory, 0, _fpsHistory.Length);
            Array.Clear(_worstFrameHistory, 0, _worstFrameHistory.Length);
            _historyIndex = 0;
        }

        private float MinFps()
        {
            float min = float.MaxValue;
            foreach (var f in _fpsHistory)
                if (f > 0f && f < min) min = f;
            return min == float.MaxValue ? 0f : min;
        }

        private float WorstFrameMs()
        {
            float max = 0f;
            foreach (var f in _worstFrameHistory)
                if (f > max) max = f;
            return max * 1000f;
        }

        private void BuildText()
        {
            var fr = Runtime.Freezer;
            var sb = new StringBuilder();
            sb.Append($"<b>Persistent Jobs Rework (TEST)</b>\n");
            sb.Append($"FPS {_currentFps:F0}   {Loc.T("ov_lowest")} {MinFps():F0}   {Loc.T("ov_slowest")} {WorstFrameMs():F0} ms\n");

            if (Main.ConflictWithPersistentJobs)
            {
                sb.Append($"<color=red>{Loc.T("ov_inactive")}</color>");
            }
            else if (Mp.IsClient)
            {
                sb.Append(Loc.T("ov_mpclient"));
            }
            else if (fr != null)
            {
                int awake = Math.Max(0, fr.TotalCars - fr.FrozenCount);
                sb.Append(Loc.F("ov_cars", fr.TotalCars, fr.TotalSets, fr.FrozenCount, awake)).Append('\n');
                sb.Append(Loc.F("ov_scan", fr.LastScanCpuMs.ToString("F1"), Loc.T(fr.IsRunning ? "ov_on" : "ov_paused")));
                if (Main.Settings.DebugIgnoreDistance) sb.Append("  <color=yellow>[ignore distance]</color>");
                if (Main.Settings.FreezeLevel2) sb.Append("  <color=yellow>[level 2]</color>");
                sb.Append('\n');
                if (WorldSpawner.Running || Time.realtimeSinceStartup - WorldSpawner.FinishedAt < 60f)
                {
                    sb.Append(WorldSpawner.Running
                        ? $"<color=yellow>{Loc.F("ov_ws", WorldSpawner.StationsDone, WorldSpawner.StationsTotal)}{(WorldSpawner.Passes > 1 ? " (" + Loc.F("ov_pass", WorldSpawner.Pass, WorldSpawner.Passes) + ")" : "")}</color>"
                        : Loc.F("ov_ws_done", WorldSpawner.StationsTotal, WorldSpawner.StationsTotal));
                    sb.Append('\n');
                    AppendStationTable(sb);
                }
                if (WorldSpawner.Running) { }
                else if (Time.realtimeSinceStartup < CarFreezer.SuspendedUntil)
                    sb.Append($"<color=yellow>{Loc.F("ov_freeze_in", (CarFreezer.SuspendedUntil - Time.realtimeSinceStartup).ToString("F0"))}</color>\n");
                sb.Append(Loc.F("ov_deleted", Stats.CarsDeleted, Stats.DeleteChecksBlocked, Stats.GenerationsBlocked, Stats.ExpiriesBlocked)).Append('\n');
                sb.Append(Loc.F("ov_res", Stats.ReservationsDeferred));
            }

            _text = sb.ToString();
        }

        /// <summary>
        /// Eine Zeile pro Bahnhof: neue Jobketten je Durchlauf und Summe, z. B.
        /// "HB     2 + 2 + 2 + 1 =  7/8". Gruen = Ketten erzeugt, rot = 0, grau = uebersprungen, gelb = laeuft gerade.
        /// </summary>
        private static void AppendStationTable(StringBuilder sb)
        {
            int limit = Main.Settings.MaxJobsPerStation;
            string lim = limit > 0 ? limit.ToString() : "-";
            var progress = new Dictionary<string, int>();
            foreach (var kv in WorldSpawner.Progress) progress[kv.Key] = kv.Value;

            foreach (var id in WorldSpawner.StationOrder)
            {
                WorldSpawner.PassChains.TryGetValue(id, out var list);
                int total = WorldSpawner.Totals.TryGetValue(id, out int t) ? t : 0;
                bool current = WorldSpawner.Running && progress.TryGetValue(id, out int st) && st == 0
                               && (list?.Count ?? 0) < WorldSpawner.Pass;

                string nameColor = total == 0 ? "#FF4C4C" : (limit > 0 && total >= limit) ? "#4CFF4C" : "white";
                sb.Append($"<color={nameColor}>{id,-5}</color>");

                var parts = new List<string>();
                if (list != null)
                    foreach (int v in list)
                        parts.Add(v < 0 ? "<color=#9A9A9A>-</color>"
                                 : v == 0 ? "<color=#FF4C4C>0</color>"
                                 : $"<color=#4CFF4C>{v}</color>");
                if (current) parts.Add("<color=yellow>.</color>");

                sb.Append(' ').Append(parts.Count > 0 ? string.Join(" + ", parts) : "");
                sb.Append($" = {total,2}/{lim}\n");
            }
        }

        private void OnGUI()
        {
            if (Main.Settings == null || !Main.Settings.DebugMode || !Main.Settings.ShowOverlay || !Main.Enabled) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    richText = true,
                    fontSize = 13,
                    wordWrap = false
                };
                // Festbreitenschrift, damit die Bahnhofs-Tabelle sauber untereinander steht
                try
                {
                    var mono = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New", "DejaVu Sans Mono" }, 13);
                    if (mono != null) _style.font = mono;
                }
                catch { }
                _style.normal.textColor = Color.white;
            }

            var content = new GUIContent(_text);
            var size = _style.CalcSize(content);
            GUI.Box(new Rect(10, 10, size.x + 16, size.y + 10), content, _style);
        }

        private void WriteLogLine()
        {
            try
            {
                if (_logPath == null)
                {
                    _logPath = Path.Combine(Main.Mod.Path, "perf_log.csv");
                    _logHeaderWritten = File.Exists(_logPath);
                }

                var fr = Runtime.Freezer;
                var inv = CultureInfo.InvariantCulture;
                var line = new StringBuilder();

                if (!_logHeaderWritten)
                {
                    line.AppendLine("time;fps;worst_frame_ms;cars;frozen;scan_ms;freezer_on;world_spawn;ignore_distance;level2;cars_deleted");
                    _logHeaderWritten = true;
                }

                line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append(';')
                    .Append(_currentFps.ToString("F1", inv)).Append(';')
                    .Append((_worstFrameThisSecond * 1000f).ToString("F1", inv)).Append(';')
                    .Append(fr?.TotalCars ?? 0).Append(';')
                    .Append(fr?.FrozenCount ?? 0).Append(';')
                    .Append((fr?.LastScanCpuMs ?? 0).ToString("F2", inv)).Append(';')
                    .Append(fr != null && fr.IsRunning ? 1 : 0).Append(';')
                    .Append(WorldSpawner.Running ? 1 : 0).Append(';')
                    .Append(Main.Settings.DebugIgnoreDistance ? 1 : 0).Append(';')
                    .Append(Main.Settings.FreezeLevel2 ? 1 : 0).Append(';')
                    .Append(Stats.CarsDeleted)
                    .AppendLine();

                File.AppendAllText(_logPath, line.ToString(), Utf8NoBom);
            }
            catch (Exception ex)
            {
                Main.WarningOnce("perflog", "perf_log.csv could not be written: " + ex.Message);
            }
        }
    }
}
