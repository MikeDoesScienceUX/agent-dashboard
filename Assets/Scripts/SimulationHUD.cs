using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Runtime HUD — press H to toggle, M to toggle heatmap.
/// Shows: sim clock, speed control, live state breakdown, zone occupancy bars,
/// session schedule, and validation warnings.
///
/// Stats are cached in Update (not recalculated in OnGUI) to avoid per-frame allocations.
/// </summary>
[AddComponentMenu("Conference Sim/Simulation HUD")]
public class SimulationHUD : MonoBehaviour
{
    [Header("References (auto-found if null)")]
    public CrowdManager           crowdManager;
    public SimValidator           validator;
    public SessionScheduleLoader  scheduleLoader;
    public CongestionMonitor      congestionMonitor;

    [Header("Controls")]
    public KeyCode toggleHUD     = KeyCode.H;
    public KeyCode toggleHeatmap = KeyCode.M;
    public KeyCode pauseKey      = KeyCode.Space;
    public KeyCode screenshotKey = KeyCode.F12;
    public bool    showHUD       = true;

    [Header("Layout")]
    public float panelWidth = 310f;
    [Tooltip("0 = auto-scale to screen height. Set manually (e.g. 0.7) to override.")]
    public float uiScale = 0f;

    // ── State colours (match AgentColorizer defaults) ───────────────
    private static readonly Color[] StateColors =
    {
        new Color(0.30f, 0.65f, 1.00f), // Transit
        new Color(0.15f, 0.90f, 0.35f), // Dwelling
        new Color(1.00f, 0.85f, 0.15f), // Roaming
        new Color(1.00f, 0.45f, 0.80f), // Socializing
        new Color(0.60f, 0.35f, 1.00f), // Resting
        new Color(1.00f, 0.30f, 0.30f), // Exiting
    };

    private bool _paused;

    // ── Cached data (refreshed in Update) ──────────────────────────
    private IReadOnlyDictionary<AgentController.AgentState, int> _stats;
    private IReadOnlyDictionary<int, int>                        _personaCounts;
    private int            _totalAgents;
    private ConferenceZone[] _zones;
    private List<SimValidator.Issue> _issues;
    private float _zoneRefresh;
    private SimConfig _cfg;

    // ── IMGUI styles ────────────────────────────────────────────────
    private GUIStyle _title, _label, _subLabel, _warnLabel, _errorLabel;
    private Texture2D _white;
    private bool _stylesReady;

    private Vector2 _zoneScroll;

    // ── Lifecycle ───────────────────────────────────────────────────

    void Start()
    {
        if (crowdManager      == null) crowdManager      = FindFirstObjectByType<CrowdManager>();
        if (validator         == null) validator         = FindFirstObjectByType<SimValidator>();
        if (scheduleLoader    == null) scheduleLoader    = FindFirstObjectByType<SessionScheduleLoader>();
        if (congestionMonitor == null) congestionMonitor = FindFirstObjectByType<CongestionMonitor>();
        RefreshZones();
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleHUD)) showHUD = !showHUD;

        // Pause / resume
        if (Input.GetKeyDown(pauseKey))
        {
            _paused        = !_paused;
            Time.timeScale = _paused ? 0f : 1f;
        }

        // Screenshot (saves with sim-time stamp)
        if (Input.GetKeyDown(screenshotKey))
        {
            string dir = Path.Combine(Application.dataPath, "SimOutput");
            Directory.CreateDirectory(dir);
            float sc = crowdManager != null ? crowdManager.SimClock : 0f;
            int hh = (int)(sc / 3600), mm = (int)(sc % 3600 / 60), ss2 = (int)(sc % 60);
            string snap = Path.Combine(dir, $"screenshot_{hh:D2}-{mm:D2}-{ss2:D2}.png");
            ScreenCapture.CaptureScreenshot(snap);
            Debug.Log($"[SimulationHUD] Screenshot → {snap}");
        }

        if (!showHUD || crowdManager == null) return;

        // Cache stats (CrowdManager already throttles this internally to every 0.5 s)
        _stats         = crowdManager.GetStateCounts();
        _personaCounts = crowdManager.GetPersonaCounts();
        if (_cfg == null) _cfg = crowdManager.config;
        _totalAgents = 0;
        foreach (var v in _stats.Values) _totalAgents += v;

        _issues = validator != null ? validator.ActiveIssues : null;

        _zoneRefresh -= Time.deltaTime;
        if (_zoneRefresh <= 0f) { _zoneRefresh = 2f; RefreshZones(); }
    }

    void RefreshZones()
    {
        _zones = crowdManager != null ? crowdManager.Zones : FindObjectsByType<ConferenceZone>(FindObjectsSortMode.None);
    }

    // ── OnGUI ───────────────────────────────────────────────────────

    void OnGUI()
    {
        if (!showHUD || crowdManager == null || Event.current.type == EventType.Layout)
            return;

        EnsureStyles();

        // Apply UI scale — auto fits to screen height (reference: 1080p), user can override
        float s = uiScale > 0f ? uiScale : Mathf.Clamp(Screen.height / 1080f, 0.55f, 1f);
        Matrix4x4 prevMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));

        // All layout coordinates below are in logical (1/s scaled) space
        float logW = Screen.width  / s;
        float logH = Screen.height / s;

        int zoneCount    = _zones != null ? _zones.Length : 0;
        int stateCount   = System.Enum.GetValues(typeof(AgentController.AgentState)).Length;
        int personaCount = _cfg != null && _cfg.personas != null ? _cfg.personas.Length : 0;
        int issueCount   = _issues != null ? Mathf.Min(_issues.Count, 4) : 0;
        int sessCount    = scheduleLoader != null && scheduleLoader.IsLoaded
                           ? Mathf.Min(scheduleLoader.Sessions.Count, 5) : 0;

        bool hasDanger  = congestionMonitor != null && congestionMonitor.HasDanger;
        bool hasWarning = congestionMonitor != null && congestionMonitor.HasWarning;

        // Fixed-height sections
        float fixedH = 46 + 22 + 22 + 50 + 8 + 18 + stateCount * 20 + 8
                     + (_paused || hasDanger || hasWarning ? 28f : 0f)
                     + (personaCount > 0 ? 20 + personaCount * 18 + 8 : 0)
                     + (sessCount    > 0 ? 20 + sessCount    * 18 + 8 : 0)
                     + (issueCount   > 0 ? 20 + issueCount   * 18 + 8 : 0)
                     + 22;

        // Zone section: allow up to the remaining vertical space, with scroll if needed
        float zoneInnerH    = zoneCount > 0 ? 20 + zoneCount * 22 + 8 : 0;
        float availH        = logH - 24f;
        float maxZoneViewH  = Mathf.Max(0, availH - fixedH);
        float zoneViewH     = zoneCount > 0 ? Mathf.Min(zoneInnerH, maxZoneViewH) : 0;

        float h  = fixedH + zoneViewH;
        float px = logW - panelWidth - 12f;
        float py = 12f;

        DrawPanel(px, py, panelWidth, h);

        float cx = px + 12f;
        float cy = py + 10f;
        float cw = panelWidth - 24f;

        // ── Title ─────────────────────────────────────────────────
        GUI.Label(new Rect(px, cy, panelWidth, 26f), "EASL SIMULATION", _title);
        cy += 30f;

        // ── Status banner ─────────────────────────────────────────
        if (_paused)
        {
            Color prev = GUI.color;
            GUI.color = new Color(0.85f, 0.85f, 0f, 0.90f);
            GUI.DrawTexture(new Rect(px, cy, panelWidth, 24f), _white);
            GUI.color = Color.black;
            GUI.Label(new Rect(px, cy + 3f, panelWidth, 20f), "⏸  PAUSED — [Space] to resume", _title);
            GUI.color = prev;
            cy += 28f;
        }
        else if (hasDanger && congestionMonitor.FlashOn)
        {
            Color prev = GUI.color;
            GUI.color = new Color(1f, 0.12f, 0.12f, 0.95f);
            GUI.DrawTexture(new Rect(px, cy, panelWidth, 24f), _white);
            GUI.color = Color.white;
            GUI.Label(new Rect(px, cy + 3f, panelWidth, 20f), "⚠  DANGER — CROWD DENSITY", _title);
            GUI.color = prev;
            cy += 28f;
        }
        else if (hasWarning)
        {
            Color prev = GUI.color;
            GUI.color = new Color(1f, 0.70f, 0f, 0.85f);
            GUI.DrawTexture(new Rect(px, cy, panelWidth, 24f), _white);
            GUI.color = Color.black;
            GUI.Label(new Rect(px, cy + 3f, panelWidth, 20f), "▲  WARNING — HIGH DENSITY", _title);
            GUI.color = prev;
            cy += 28f;
        }

        // ── Sim clock + day-phase ─────────────────────────────────
        float sim = crowdManager.SimClock;
        int hh = (int)(sim / 3600), mm = (int)(sim % 3600 / 60), ss = (int)(sim % 60);
        GUI.Label(new Rect(cx, cy, 150f, 20f), $"Time   {hh:D2}:{mm:D2}:{ss:D2}", _label);
        GUI.Label(new Rect(px + cw - 70f, cy, 70f, 20f), $"Agents {_totalAgents}", _label);
        cy += 22f;

        // Day-phase row
        int phaseIdx = crowdManager.CurrentDayPhaseIndex;
        string phaseName = (_cfg != null && _cfg.dayPhases != null &&
                            phaseIdx >= 0 && phaseIdx < _cfg.dayPhases.Length)
            ? _cfg.dayPhases[phaseIdx].name : "–";
        GUI.Label(new Rect(cx, cy, cw, 18f), $"Phase  {phaseName}", _subLabel);
        cy += 22f;

        // ── Speed slider ──────────────────────────────────────────
        GUI.Label(new Rect(cx, cy, 60f, 20f), "Speed ×", _label);
        float newScale = GUI.HorizontalSlider(new Rect(cx + 62f, cy + 4f, cw - 110f, 14f),
                             crowdManager.timeScaleMultiplier, 0.1f, 60f);
        crowdManager.timeScaleMultiplier = Mathf.Round(newScale * 10f) / 10f;
        GUI.Label(new Rect(cx + cw - 42f, cy, 40f, 20f),
                  $"{crowdManager.timeScaleMultiplier:F1}", _label);
        cy += 28f;

        // ── State breakdown ───────────────────────────────────────
        GUI.Label(new Rect(cx, cy, cw, 18f), "Agent States", _subLabel);
        cy += 20f;

        if (_stats != null)
        {
            int si = 0;
            foreach (AgentController.AgentState state in
                     System.Enum.GetValues(typeof(AgentController.AgentState)))
            {
                int n  = _stats.TryGetValue(state, out var v) ? v : 0;
                Color prev = GUI.color;
                GUI.color = si < StateColors.Length ? StateColors[si] : Color.white;
                GUI.Label(new Rect(cx, cy, 14f, 18f), "●", _label);
                GUI.color = Color.white;
                GUI.Label(new Rect(cx + 16f, cy, 120f, 18f), state.ToString(), _label);
                GUI.Label(new Rect(cx + 140f, cy, 40f, 18f), n.ToString(), _label);

                // Tiny bar
                if (_totalAgents > 0)
                {
                    float fill = (float)n / _totalAgents;
                    GUI.color = si < StateColors.Length ? StateColors[si] : Color.white;
                    GUI.color = new Color(GUI.color.r, GUI.color.g, GUI.color.b, 0.5f);
                    float bx = cx + 185f;
                    GUI.DrawTexture(new Rect(bx, cy + 4f, (cw - 185f) * fill, 12f), _white);
                }

                GUI.color = prev;
                cy += 20f;
                si++;
            }
        }

        cy += 8f;

        // ── Persona breakdown ─────────────────────────────────────
        if (_cfg != null && _cfg.personas != null && _cfg.personas.Length > 0 &&
            _personaCounts != null)
        {
            GUI.Label(new Rect(cx, cy, cw, 18f), "Personas", _subLabel);
            cy += 20f;

            for (int i = 0; i < _cfg.personas.Length; i++)
            {
                int n = _personaCounts.TryGetValue(i, out var pv) ? pv : 0;
                GUI.Label(new Rect(cx, cy, 120f, 18f), _cfg.personas[i].name, _label);
                GUI.Label(new Rect(cx + 125f, cy, 40f, 18f), n.ToString(), _label);

                if (_totalAgents > 0)
                {
                    float fill = (float)n / _totalAgents;
                    Color prev2 = GUI.color;
                    GUI.color = new Color(0.55f, 0.75f, 1f, 0.45f);
                    GUI.DrawTexture(new Rect(cx + 168f, cy + 4f, (cw - 168f) * fill, 11f), _white);
                    GUI.color = prev2;
                }
                cy += 18f;
            }
            cy += 8f;
        }

        // ── Zone occupancy (scrollable) ───────────────────────────
        if (_zones != null && _zones.Length > 0 && zoneViewH > 0f)
        {
            GUI.Label(new Rect(cx, cy, cw, 18f), "Zone Occupancy", _subLabel);
            cy += 20f;

            Rect viewRect  = new Rect(cx, cy, cw, zoneViewH - 20f);
            Rect innerRect = new Rect(0, 0, cw - 16f, zoneCount * 22f);
            _zoneScroll = GUI.BeginScrollView(viewRect, _zoneScroll, innerRect, false, false);

            float zy = 0f;
            float zwi = innerRect.width;
            foreach (var z in _zones)
            {
                int   n       = crowdManager.GetZoneCount(z.sensorId);
                float density = crowdManager.GetBoothDensity(z.sensorId);
                float fill    = Mathf.Clamp01(density / 3f);

                DataLoader dl = crowdManager.dataLoader;
                int gt = dl != null && dl.IsLoaded
                    ? dl.GetGroundTruth(z.sensorId, crowdManager.SimClock) : -1;

                GUI.Label(new Rect(0, zy, 88f, 18f), z.displayName, _label);

                if (congestionMonitor != null)
                {
                    Color sc    = congestionMonitor.GetStatusColor(z.sensorId);
                    string lbl  = congestionMonitor.GetStatusLabel(z.sensorId);
                    Color prev2 = GUI.color;
                    GUI.color = sc;
                    GUI.Label(new Rect(90f, zy, 40f, 18f), lbl, _label);
                    GUI.color = prev2;
                }

                float bx = 132f;
                float bw = zwi - 196f;

                Color prev = GUI.color;
                GUI.color  = new Color(0.12f, 0.12f, 0.12f, 0.9f);
                GUI.DrawTexture(new Rect(bx, zy + 3f, bw, 13f), _white);

                if (fill > 0f)
                {
                    GUI.color = Color.Lerp(new Color(0.2f, 0.9f, 0.35f),
                                           new Color(1f, 0.25f, 0.25f), fill);
                    GUI.DrawTexture(new Rect(bx, zy + 3f, bw * fill, 13f), _white);
                }

                GUI.color = Color.white;
                if (gt >= 0)
                {
                    float err = gt > 0 ? Mathf.Abs(n - gt) / (float)gt : (n > 0 ? 1f : 0f);
                    GUI.color = err < 0.10f ? new Color(0.25f, 1.0f, 0.45f)
                              : err < 0.25f ? new Color(1.0f,  0.85f, 0.15f)
                                            : new Color(1.0f,  0.30f, 0.30f);
                }
                GUI.Label(new Rect(bx + bw + 4f, zy, 28f, 18f), n.ToString(), _label);

                if (gt >= 0)
                {
                    GUI.color = new Color(0.65f, 0.65f, 0.65f);
                    GUI.Label(new Rect(bx + bw + 32f, zy, 34f, 18f), $"/{gt}", _label);
                }

                GUI.color = prev;
                zy += 22f;
            }

            GUI.EndScrollView();
            cy += zoneViewH - 20f + 8f;
        }

        // ── Session schedule ──────────────────────────────────────
        if (scheduleLoader != null && scheduleLoader.IsLoaded && scheduleLoader.Sessions.Count > 0)
        {
            GUI.Label(new Rect(cx, cy, cw, 18f), "Sessions", _subLabel);
            cy += 20f;

            for (int i = 0; i < Mathf.Min(scheduleLoader.Sessions.Count, 5); i++)
            {
                var ses  = scheduleLoader.Sessions[i];
                bool now = sim >= ses.startSimTime && sim < ses.endSimTime;
                Color prev = GUI.color;
                GUI.color = now ? new Color(0.2f, 1f, 0.4f) : new Color(0.6f, 0.6f, 0.6f);
                GUI.Label(new Rect(cx, cy, cw, 18f),
                    $"{(now ? "▶ " : "  ")}{ses.roomId}  {ses.sessionId}  ({ses.expectedAttendance})", _label);
                GUI.color = prev;
                cy += 18f;
            }

            cy += 8f;
        }

        // ── Validation warnings ───────────────────────────────────
        if (_issues != null && _issues.Count > 0)
        {
            GUI.Label(new Rect(cx, cy, cw, 18f), "⚠ Setup Issues", _subLabel);
            cy += 20f;

            for (int i = 0; i < Mathf.Min(_issues.Count, 4); i++)
            {
                var iss = _issues[i];
                GUIStyle issStyle = iss.severity == SimValidator.Severity.Error ? _errorLabel : _warnLabel;
                GUI.Label(new Rect(cx, cy, cw, 18f), $"• {iss.message}", issStyle);
                cy += 18f;
            }

            cy += 8f;
        }

        // ── Hotkey hint ───────────────────────────────────────────
        GUI.Label(new Rect(cx, cy, cw, 18f),
            $"[{toggleHUD}] HUD  [{toggleHeatmap}] heat  [{pauseKey}] pause  [{screenshotKey}] snap  [Tab] halls", _subLabel);

        GUI.matrix = prevMatrix;
    }

    // ── Drawing Helpers ─────────────────────────────────────────────

    private void DrawPanel(float x, float y, float w, float h)
    {
        Color prev = GUI.color;
        GUI.color = new Color(0.03f, 0.03f, 0.09f, 0.90f);
        GUI.DrawTexture(new Rect(x, y, w, h), _white);
        GUI.color = new Color(0.25f, 0.55f, 1.00f, 0.45f);
        GUI.DrawTexture(new Rect(x,         y,         w, 1), _white);
        GUI.DrawTexture(new Rect(x,         y + h - 1, w, 1), _white);
        GUI.DrawTexture(new Rect(x,         y,     1,     h), _white);
        GUI.DrawTexture(new Rect(x + w - 1, y,     1,     h), _white);
        GUI.color = prev;
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return;
        _stylesReady = true;

        _white = new Texture2D(1, 1);
        _white.SetPixel(0, 0, Color.white);
        _white.Apply();

        _title = new GUIStyle(GUI.skin.label)
            { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
              normal = { textColor = new Color(0.7f, 0.9f, 1f) } };

        _label = new GUIStyle(GUI.skin.label)
            { fontSize = 11, normal = { textColor = new Color(0.92f, 0.92f, 0.92f) } };

        _subLabel = new GUIStyle(GUI.skin.label)
            { fontSize = 10, fontStyle = FontStyle.Bold,
              normal = { textColor = new Color(0.55f, 0.75f, 1f) } };

        _warnLabel = new GUIStyle(GUI.skin.label)
            { fontSize = 10, normal = { textColor = new Color(1f, 0.85f, 0.2f) } };

        _errorLabel = new GUIStyle(GUI.skin.label)
            { fontSize = 10, normal = { textColor = new Color(1f, 0.35f, 0.35f) } };
    }
}
