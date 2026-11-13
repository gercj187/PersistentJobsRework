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
        public int MaxJobsPerStation = 12;     // 0 = kein Limit
        public int ExtraWorldSpawnPasses = 0;  // weitere Durchlaeufe nach den regulaeren (fest 0)
        public int MinFpsWorldSpawn = 30;
        public int MinCarsPerType = 20;
        public int InitialMaxCarsPerJob = 5;   // erster Spawn: hoechstens so viele Wagen je Job (0 = Bahnhofsregel)
        public int MinShuntingJobsPerStation = 4; // erster Spawn: zuerst so viele Rangierjobs je Bahnhof        // Mindestbestand je ladbarem Wagentyp beim Weltspawn (0 = aus)      // Weltspawn stoppt, sobald die FPS darunter fallen (0 = aus)
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
        public bool DeepSleep = true;          // Debug/experimentell: weit entfernte eingefrorene Wagen unsichtbar + stumm
        public float DeepSleepDistance = 1500f;
        public bool DeepSleepBogies = false;
        public bool ThrottleTrainsOptimizer = true;   // Debug: Vanilla-TrainsOptimizer ab 300 Wagen gedrosselt   // Debug/experimentell: im Tiefschlaf auch die Drehgestelle (Bogie) abschalten
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
            if (SettingsVersion < 3)
            {
                // einmalige Umstellung (v1.00.10): 12 Jobs je Bahnhof, Tiefschlaf an (1500 m)
                if (MaxJobsPerStation == 8) MaxJobsPerStation = 12;
                DeepSleep = true;
                DeepSleepDistance = 1500f;
                SettingsVersion = 3;
            }
            DeepSleepBogies = false;                // ausgeblendet, aus

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

            // FreezeEnabled: Debug-Schalter (zum Eingrenzen von MP-Problemen), Standard an
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
            MinFpsWorldSpawn = Mathf.Clamp(MinFpsWorldSpawn, 0, 90);
            MinCarsPerType = Mathf.Clamp(MinCarsPerType, 0, 50);
            InitialMaxCarsPerJob = Mathf.Clamp(InitialMaxCarsPerJob, 0, 20);
            MinShuntingJobsPerStation = Mathf.Clamp(MinShuntingJobsPerStation, 0, 10);
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
            InitializeWorldOncePerSave = Toggle(InitializeWorldOncePerSave, "world_init");
            MaxJobsPerStation = Mathf.RoundToInt(Slider(Loc.T("max_jobs"), MaxJobsPerStation, 0f, 20f, "", 0, "max_jobs"));
            JobsPerPass = Mathf.RoundToInt(Slider(Loc.T("per_pass"), JobsPerPass, 0f, 5f, "", 0, "per_pass"));
            InitialMaxCarsPerJob = Mathf.RoundToInt(Slider(Loc.T("init_cars"), InitialMaxCarsPerJob, 0f, 20f, InitialMaxCarsPerJob == 0 ? Loc.T("off") : "", 0, "init_cars"));
            MinShuntingJobsPerStation = Mathf.RoundToInt(Slider(Loc.T("min_shunt"), MinShuntingJobsPerStation, 0f, 10f, "", 0, "min_shunt"));
            MinFpsWorldSpawn = Mathf.RoundToInt(Slider(Loc.T("min_fps"), MinFpsWorldSpawn, 0f, 90f, MinFpsWorldSpawn == 0 ? Loc.T("off") : "FPS", 0, "min_fps"));
            AssignTrackOnAcceptance = Toggle(AssignTrackOnAcceptance, "bundle");

            GUILayout.Space(5);
            AlternativeTracks = Toggle(AlternativeTracks, "alt_toggle");
            if (AlternativeTracks)
            {
                PenaltySameGroup = Slider("   " + Loc.T("pen_same"), PenaltySameGroup, 0f, 50f, "%", 0, "pen_same");
                PenaltyOtherGroup = Slider("   " + Loc.T("pen_other"), PenaltyOtherGroup, 0f, 50f, "%", 0, "pen_other");
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
            // Slider-Spalte: breitester Text dieses Durchlaufs gilt im naechsten Frame fuer alle Slider
            if (Event.current.type == EventType.Repaint) { _sliderColumn = _sliderColumnNext; _sliderColumnNext = 0f; }
        }

        [NonSerialized] private static float _sliderColumn, _sliderColumnNext;

        private void DrawDebug()
        {
            Header(Loc.T("h_debug"));
            ShowOverlay = Toggle(ShowOverlay, "show_overlay");
            FreezeEnabled = Toggle(FreezeEnabled, "dbg_freeze");
            ThrottleTrainsOptimizer = Toggle(ThrottleTrainsOptimizer, "dbg_throttle");
            DeepSleep = Toggle(DeepSleep, "deep_sleep");
            if (DeepSleep)
            {
                DeepSleepDistance = Slider("   " + Loc.T("deep_dist"), DeepSleepDistance, 700f, 5000f, "m", 0, "deep_dist");
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("   " + Loc.T("priority"), GUILayout.ExpandWidth(false));
            PriorityStations = GUILayout.TextField(PriorityStations, GUILayout.Width(250));
            Help("priority");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(5);
            // Buttons untereinander, Breite nach Text (nichts abgeschnitten)
            if (DebugButton("btn_stats", Loc.T("btn_stats"))) Runtime.Freezer?.LogComponentStatistics();
            if (DebugButton("btn_profile", CpuProfiler.Running ? Loc.T("profile_running") : Loc.T("btn_profile")) && !CpuProfiler.Running) CpuProfiler.Start();
            if (DebugButton("btn_spawn", Loc.T("btn_spawn"))) Runtime.Spawner?.StartWorldSpawn(false);
            if (DebugButton("btn_reset_init", Loc.T("btn_reset_init"))) WorldInit.ResetFlagAndRespawn();
            if (DebugButton("btn_refill", Loc.T("btn_refill"))) JobRefill.Start();
            if (DebugButton("btn_minstock", Loc.T("btn_minstock")) && !MinStock.Running && Runtime.Spawner != null)
                Runtime.Spawner.StartCoroutine(MinStock.Fill(Math.Max(1, MinCarsPerType)));

            // TOTAL RESET mit Sicherheitsabfrage (zweimal klicken innerhalb von 5 s)
            GUILayout.BeginHorizontal();
            bool armed = Time.realtimeSinceStartup < _hardArmedUntil;
            string hardText = WorldInit.HardResetRunning ? Loc.T("hard_running")
                            : armed ? "!!! " + Loc.T("btn_hard_confirm") + " !!!" : Loc.T("btn_hard");
            if (GUILayout.Button(Tip("btn_hard", hardText), GUILayout.Width(ButtonWidth(hardText))) && !WorldInit.HardResetRunning)
            {
                if (armed) { _hardArmedUntil = 0f; WorldInit.HardReset(); }
                else _hardArmedUntil = Time.realtimeSinceStartup + 5f;
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.F("spawned_count", SpawnRegistry.Count));
        }

        [NonSerialized] private float _hardArmedUntil;

        private static float ButtonWidth(string text)
            => Mathf.Max(320f, GUI.skin.button.CalcSize(new GUIContent(text)).x + 30f);

        /// <summary>Ein Debug-Button pro Zeile, so breit wie sein Text.</summary>
        private static bool DebugButton(string key, string text)
        {
            GUILayout.BeginHorizontal();
            bool clicked = GUILayout.Button(Tip(key, text), GUILayout.Width(ButtonWidth(text)));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return clicked;
        }

        private static void Header(string text)
        {
            GUILayout.Space(10);
            GUILayout.Label($"<b>{text}</b>");
        }

        private static float Slider(string label, float value, float min, float max, string unit, int decimals = 0, string helpKey = null)
        {
            GUILayout.BeginHorizontal();
            string text = $"{label}: {Math.Round(value, decimals)} {unit}";
            GUILayout.Label(text, GUILayout.ExpandWidth(false));
            if (helpKey != null) Help(helpKey);
            // alle Slider buendig in einer Spalte direkt hinter dem laengsten Text (statt am rechten Rand)
            float w = GUI.skin.label.CalcSize(new GUIContent(text)).x;
            _sliderColumnNext = Mathf.Max(_sliderColumnNext, w);
            GUILayout.Space(Mathf.Max(0f, _sliderColumn - w) + 12f);
            float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(250));
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
