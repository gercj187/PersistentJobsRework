using System;
using UnityEngine;
using UnityModManagerNet;

namespace PersistentJobsRework
{
    public class Settings : UnityModManager.ModSettings
    {
        // ---------------------------------------------------------- Persistenz
        public bool NeverDeleteCars = true;
        public bool OneTimeStationSpawn = true;
        public bool KeepJobsAvailable = true;
        public string Language = "";           // "" = Spielsprache, sonst Code aus Loc.Codes
        public bool IgnoreLicensesForGeneration = true;
        public bool UseInboundFallback = true;
        public bool FreezeKinematic = false;           // alt: zusaetzlich Rigidbodies kinematisch
        public bool PauseBrakesOfFrozenTrains = true;
        public bool LhArriveOnInbound = true;
        public bool DeferReservationToAcceptance = false;   // false = Vanilla: Gleise werden schon bei der Erzeugung reserviert
        public bool AssignTrackOnAcceptance = true;
        public int MaxJobsPerStation = 8;      // 0 = kein Limit
        public int ExtraWorldSpawnPasses = 0;  // weitere Durchlaeufe nach den regulaeren (fest 0)
        public int JobsPerPass = 1;            // Weltspawn reihum: max. neue Jobketten pro Bahnhof und Durchlauf (0 = aus)
        public bool ReserveFirstLinkOnly = true; // nur erstes Glied einer Kette sofort reservieren
        public float StationFillLimitPercent = 100f;   // max. Anteil der Gueter-Gleise (Anzahl), die beim Erzeugen Wagen bekommen

        // ---------------------------------------------------------- Ausweichgleise
        public bool AlternativeTracks = true;
        public float PenaltySameGroup = 5f;    // Prozent pro abweichendem Ziel
        public float PenaltyOtherGroup = 10f;  // Prozent pro abweichendem Ziel

        // ---------------------------------------------------------- Einfrieren
        public bool FreezeEnabled = true;
        public float FreezeDistance = 500f;    // Meter (fest, nicht mehr einstellbar)
        public float WakeDistance = 200f;      // Meter (fest, Hysterese, immer < FreezeDistance)
        public float ScanInterval = 5f;        // Sekunden (fest)
        public bool WakeNearMovingTrains = true;
        public float MovingTrainWakeDistance = 300f;
        public bool FreezeLevel2 = false;      // experimentell: Skripte der Wagen pausieren
        public string ProtectedComponentNames = "TrainCar,Bogie,Coupler,Networked,Track";

        // ---------------------------------------------------------- Debug / Test
        public bool ShowOverlay = true;
        public bool PerfLog = false;
        public bool InitializeWorldOncePerSave = true;   // Weltspawn einmal pro Spielstand (Merker im Spielstand)
        public float FreezeDelayAfterWorldSpawn = 20f;
        public bool AwakeDuringWorldSpawn = false;   // nur fuer den FPS-Vergleichstest
        public bool DebugIgnoreDistance = false;
        public string PriorityStations = "IMW,IME,MB,MFMB,HMB";
        public bool DebugMode = false;         // Schalter unten rechts - zeigt Debug-Bereich und Overlay
        public int SettingsVersion = 0;        // fuer einmalige Umstellung alter Einstellungsdateien

        public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);

        /// <summary>
        /// Ausgeblendete Optionen haben feste Werte - egal was in einer alten Einstellungsdatei steht.
        /// </summary>
        private void EnforceFixedValues()
        {
            if (SettingsVersion < 2)
            {
                // einmalige Umstellung auf die neuen Standardwerte (v0.05.24)
                if (MaxJobsPerStation <= 0) MaxJobsPerStation = 8;
                PriorityStations = "IMW,IME,MB,MFMB,HMB";
                ShowOverlay = true;
                SettingsVersion = 2;
            }

            Language = "";                          // immer Spielsprache
            NeverDeleteCars = true;                 // Kern der Mod
            OneTimeStationSpawn = true;
            KeepJobsAvailable = true;
            IgnoreLicensesForGeneration = true;
            LhArriveOnInbound = true;
            UseInboundFallback = false;   // Ankunft immer I - O nur beim Ausweichen mit Abzug
            ReserveFirstLinkOnly = true;
            ExtraWorldSpawnPasses = 0;              // keine Zusatz-Durchlaeufe
            DeferReservationToAcceptance = false;   // experimentell -> aus
            StationFillLimitPercent = 100f;         // experimentell -> aus

            FreezeEnabled = true;
            FreezeDistance = 500f;
            WakeDistance = 200f;
            ScanInterval = 5f;
            WakeNearMovingTrains = true;
            MovingTrainWakeDistance = 300f;
            PauseBrakesOfFrozenTrains = true;
            FreezeKinematic = false;                // alte Methode -> aus
            FreezeLevel2 = false;                   // experimentell -> aus

            PerfLog = false;
            AwakeDuringWorldSpawn = false;
            DebugIgnoreDistance = false;
        }

        public void Sanitize()
        {
            EnforceFixedValues();
            FreezeDistance = Mathf.Clamp(FreezeDistance, 200f, 10000f);
            WakeDistance = Mathf.Clamp(WakeDistance, 100f, FreezeDistance - 50f);
            ScanInterval = Mathf.Clamp(ScanInterval, 0.5f, 30f);
            MovingTrainWakeDistance = Mathf.Clamp(MovingTrainWakeDistance, 50f, 2000f);
            FreezeDelayAfterWorldSpawn = Mathf.Clamp(FreezeDelayAfterWorldSpawn, 0f, 300f);
            if (ProtectedComponentNames == null) ProtectedComponentNames = "";
            if (PriorityStations == null) PriorityStations = "";
            StationFillLimitPercent = Mathf.Clamp(StationFillLimitPercent, 10f, 100f);
            MaxJobsPerStation = Mathf.Clamp(MaxJobsPerStation, 0, 20);
            ExtraWorldSpawnPasses = Mathf.Clamp(ExtraWorldSpawnPasses, 0, 10);
            JobsPerPass = Mathf.Clamp(JobsPerPass, 0, 5);
            PenaltySameGroup = Mathf.Clamp(PenaltySameGroup, 0f, 50f);
            PenaltyOtherGroup = Mathf.Clamp(PenaltyOtherGroup, 0f, 50f);
        }

        // ------------------------------------------------------------------ GUI

        public void Draw()
        {
            if (Main.ConflictWithPersistentJobs)
            {
                GUILayout.Label($"<color=red><b>{Loc.T("conflict")}</b></color>");
                return;
            }

            // Clients koennen nichts aendern - alles kommt vom Host
            if (Mp.IsClient)
            {
                GUILayout.Label($"<b>{Loc.T("mp_client_title")}</b>");
                GUILayout.Label(Loc.T("mp_client_text"));
                return;
            }

            Header(Loc.T("h_jobgen"));
            float sliderLabelWidth = CalculateSliderLabelWidth(
                Loc.T("max_jobs"),
                Loc.T("per_pass"),
                "   " + Loc.T("pen_same"),
                "   " + Loc.T("pen_other"));
            InitializeWorldOncePerSave = Toggle(InitializeWorldOncePerSave, "world_init");
            MaxJobsPerStation = Mathf.RoundToInt(Slider(Loc.T("max_jobs"), MaxJobsPerStation, 0f, 20f, "", 0, "max_jobs", sliderLabelWidth));
            JobsPerPass = Mathf.RoundToInt(Slider(Loc.T("per_pass"), JobsPerPass, 0f, 5f, "", 0, "per_pass", sliderLabelWidth));
            AssignTrackOnAcceptance = Toggle(AssignTrackOnAcceptance, "bundle");

            GUILayout.Space(5);
            AlternativeTracks = Toggle(AlternativeTracks, "alt_toggle");
            if (AlternativeTracks)
            {
                PenaltySameGroup = Slider("   " + Loc.T("pen_same"), PenaltySameGroup, 0f, 50f, "%", 0, "pen_same", sliderLabelWidth);
                PenaltyOtherGroup = Slider("   " + Loc.T("pen_other"), PenaltyOtherGroup, 0f, 50f, "%", 0, "pen_other", sliderLabelWidth);
            }

            if (DebugMode) DrawDebug();

            // Debug-Schalter ganz unten rechts
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            DebugMode = GUILayout.Toggle(DebugMode, "", GUILayout.Width(20));
            if (GUILayout.Button(Loc.T("debug_toggle"), GUI.skin.label)) DebugMode = !DebugMode;
            Help("debug_toggle");
            GUILayout.EndHorizontal();

            DrawTooltip();
        }

        private void DrawDebug()
        {
            Header(Loc.T("h_debug"));
            ShowOverlay = Toggle(ShowOverlay, "show_overlay");
            GUILayout.BeginHorizontal();
            GUILayout.Label("   " + Loc.T("priority"), GUILayout.Width(420));
            PriorityStations = GUILayout.TextField(PriorityStations, GUILayout.Width(250));
            Help("priority");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Tip("btn_stats", Loc.T("btn_stats")), GUILayout.Width(300))) Runtime.Freezer?.LogComponentStatistics();
            if (GUILayout.Button(Tip("btn_profile", CpuProfiler.Running ? Loc.T("profile_running") : Loc.T("btn_profile")), GUILayout.Width(300)) && !CpuProfiler.Running)
                CpuProfiler.Start();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Tip("btn_spawn", Loc.T("btn_spawn")), GUILayout.Width(300))) Runtime.Spawner?.StartWorldSpawn(false);
            if (GUILayout.Button(Tip("btn_reset_init", Loc.T("btn_reset_init")), GUILayout.Width(300))) WorldInit.ResetFlagAndRespawn();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Tip("btn_refill", Loc.T("btn_refill")), GUILayout.Width(300))) JobRefill.Start();
            GUILayout.EndHorizontal();

            // TOTAL RESET mit Sicherheitsabfrage (zweimal klicken innerhalb von 5 s)
            GUILayout.BeginHorizontal();
            bool armed = Time.realtimeSinceStartup < _hardArmedUntil;
            string hardText = WorldInit.HardResetRunning ? Loc.T("hard_running")
                            : armed ? "!!! " + Loc.T("btn_hard_confirm") + " !!!" : Loc.T("btn_hard");
            if (GUILayout.Button(Tip("btn_hard", hardText), GUILayout.Width(604)) && !WorldInit.HardResetRunning)
            {
                if (armed) { _hardArmedUntil = 0f; WorldInit.HardReset(); }
                else _hardArmedUntil = Time.realtimeSinceStartup + 5f;
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.F("spawned_count", SpawnRegistry.Count));
        }

        [NonSerialized] private float _hardArmedUntil;

        private static void Header(string text)
        {
            GUILayout.Space(10);
            GUILayout.Label($"<b>{text}</b>");
        }

        private const float SliderControlWidth = 250f;
        private const float SliderHelpWidth = 24f;
        private const float SliderValueWidth = 70f;
        private const float SliderLabelPadding = 8f;

        private static float CalculateSliderLabelWidth(params string[] labels)
        {
            float width = 0f;
            GUIStyle style = GUI.skin.label;
            foreach (string label in labels)
                width = Mathf.Max(width, style.CalcSize(new GUIContent(label)).x);

            return Mathf.Ceil(width) + SliderLabelPadding;
        }

        private static float Slider(string label, float value, float min, float max, string unit, int decimals = 0, string helpKey = null, float labelWidth = 0f)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(labelWidth));
            GUILayout.BeginHorizontal(GUILayout.Width(SliderHelpWidth));
            if (helpKey != null) Help(helpKey);
            GUILayout.EndHorizontal();
            float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(SliderControlWidth));
            GUILayout.Label($"{Math.Round(value, decimals)} {unit}".TrimEnd(), GUILayout.Width(SliderValueWidth));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return decimals == 0 ? Mathf.Round(v) : (float)Math.Round(v, decimals);
        }

        /// <summary>Schalter mit "(?)"-Tooltip daneben.</summary>
        private static bool Toggle(bool value, string key)
        {
            GUILayout.BeginHorizontal();
            bool v = GUILayout.Toggle(value, "", GUILayout.Width(20));
            // Text als eigenes Label: im Toggle-Stil von UMM werden Unterlaengen (g, j, p) abgeschnitten
            if (GUILayout.Button(Loc.T(key), GUI.skin.label)) v = !v;
            Help(key);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return v;
        }

        /// <summary>Gelbes, fettes " ?" wie in LongerLoadingDelay - Tooltip: fetter Titel + ausfuehrliche Beschreibung.</summary>
        private static void Help(string key)
        {
            if (_helpStyle == null)
            {
                _helpStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                _helpStyle.normal.textColor = new Color(1f, 0.8f, 0.3f);
            }
            GUILayout.Label(new GUIContent(" ?", TipText(key)), _helpStyle, GUILayout.ExpandWidth(false));
        }

        private static string TipText(string key) => $"<b>{Loc.T(key)}</b>\n{Loc.T(key + "_tip")}";

        private static GUIContent Tip(string key, string text) => new GUIContent(text, $"<b>{text}</b>\n{Loc.T(key + "_tip")}");

        private static GUIStyle _helpStyle;

        /// <summary>
        /// Tooltip wie in LongerLoadingDelay (460 px, schwarz 92 %, 2 px gelbe Linie oben, fetter Titel, an der Maus).
        /// Gezeichnet wird er von TooltipOverlay ueber dem Mod-Manager-Fenster - innerhalb von Draw wuerde er von den
        /// Zeilen der nachfolgenden Mods ueberdeckt. Danach GUI.tooltip leeren, sonst zeigt UMM ihn doppelt.
        /// </summary>
        private static void DrawTooltip()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!string.IsNullOrEmpty(GUI.tooltip))
                TooltipOverlay.Show(GUI.tooltip, GUIUtility.GUIToScreenPoint(Event.current.mousePosition));
            GUI.tooltip = "";
        }
    }
}
