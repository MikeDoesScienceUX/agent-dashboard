using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Open via: Window > Conference Sim > Zone Setup
///
/// Four-tab wizard covering the complete setup workflow:
///   [1] Wizard   — step-by-step auto-wire with one button per step
///   [2] Zones    — add/edit/remove/import ConferenceZone objects
///   [3] CSV Data — validate CSV files, preview data, check zone ID matches
///   [4] Config   — create SimConfig, set time scale, review parameters
/// </summary>
public class ConferenceSetupWindow : EditorWindow
{
    [MenuItem("Window/Conference Sim/Zone Setup %#z")]
    public static void Open() => GetWindow<ConferenceSetupWindow>("Conference Setup");

    // ── Tabs ────────────────────────────────────────────────────────
    private readonly string[] _tabs = { "① Wizard", "② Zones", "③ CSV Data", "④ Config" };
    private int _tab;
    private Vector2 _scroll;

    // ── Cached scene state ──────────────────────────────────────────
    private List<ConferenceZone> _zones = new List<ConferenceZone>();
    private CrowdManager   _cm;
    private DataLoader     _dl;
    private AnalyticsManager _am;
    private SimulationHUD  _hud;
    private SessionScheduleLoader _ssl;
    private SimValidator   _sv;
    private GameObject     _simController;

    // ── Zone tab state ──────────────────────────────────────────────
    private string _newZoneName  = "Zone_A";
    private Color  _newZoneColor = Color.cyan;
    private float  _newZoneArea  = 20f;

    // ── CSV tab state ────────────────────────────────────────────────
    private string _sensorCsvPath   = "";
    private string _scheduleCsvPath = "";
    private List<string> _csvPreview    = new List<string>();
    private List<string> _csvIssues     = new List<string>();

    // ── Colours ─────────────────────────────────────────────────────
    private static Color COK   = new Color(0.2f, 0.8f, 0.35f);
    private static Color CWARN = new Color(1.0f, 0.75f, 0.1f);
    private static Color CERR  = new Color(1.0f, 0.3f,  0.3f);
    private static Color CINFO = new Color(0.5f, 0.8f,  1.0f);

    // ── Lifecycle ───────────────────────────────────────────────────

    void OnEnable()
    {
        RefreshAll();
        EditorApplication.hierarchyChanged += OnHierarchyChanged;
    }

    void OnDisable()
    {
        EditorApplication.hierarchyChanged -= OnHierarchyChanged;
    }

    void OnHierarchyChanged() => RefreshAll();

    void RefreshAll()
    {
        _zones.Clear();
        _zones.AddRange(Object.FindObjectsByType<ConferenceZone>(FindObjectsSortMode.None)
            .OrderBy(z => z.sensorId));

        _cm  = Object.FindFirstObjectByType<CrowdManager>();
        _dl  = Object.FindFirstObjectByType<DataLoader>();
        _am  = Object.FindFirstObjectByType<AnalyticsManager>();
        _hud = Object.FindFirstObjectByType<SimulationHUD>();
        _ssl = Object.FindFirstObjectByType<SessionScheduleLoader>();
        _sv  = Object.FindFirstObjectByType<SimValidator>();

        _simController = GameObject.Find("___SimController");
        Repaint();
    }

    // ── Root GUI ────────────────────────────────────────────────────

    void OnGUI()
    {
        DrawToolbar();
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        switch (_tab)
        {
            case 0: DrawWizardTab();  break;
            case 1: DrawZonesTab();   break;
            case 2: DrawCSVTab();     break;
            case 3: DrawConfigTab();  break;
        }

        EditorGUILayout.EndScrollView();
    }

    void DrawToolbar()
    {
        EditorGUILayout.Space(4);
        _tab = GUILayout.Toolbar(_tab, _tabs, GUILayout.Height(26));
        DrawSeparator();
    }

    // ════════════════════════════════════════════════════════════════
    // TAB 1 — WIZARD
    // ════════════════════════════════════════════════════════════════

    void DrawWizardTab()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Setup Wizard", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(
            "Follow each step in order. Green = done. Red = action required.",
            EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(6);

        DrawStep(1, "Scene Controller",
            "A ___SimController GameObject with all required components.",
            _simController != null && _cm != null && _dl != null,
            "Auto-Create & Wire Scene Controller",
            AutoWireSceneController);

        DrawStep(2, "Spawn Point",
            "At least one Transform marking where agents enter the venue.",
            _cm != null && _cm.spawnPoints != null && _cm.spawnPoints.Length > 0,
            "Auto-Find StartPoint",
            AutoFindSpawnPoints);

        DrawStep(3, "Exit Points",
            "One or more Transforms marking venue exits.",
            _cm != null && _cm.exitPoints != null && _cm.exitPoints.Length > 0,
            "Auto-Find Exit Points",
            AutoFindExitPoints);

        DrawStep(4, "Agent Prefab",
            "A prefab with NavMeshAgent + AgentController + AgentColorizer.",
            _cm != null && _cm.agentPrefab != null,
            "Create Default Agent Prefab",
            CreateAgentPrefab);

        DrawStep(5, "ConferenceZones",
            "At least one ConferenceZone component placed in the scene.",
            _zones.Count > 0,
            "Open Zones Tab →",
            () => _tab = 1);

        DrawStep(6, "Sensor CSV",
            "sensor_data.csv present in StreamingAssets and loaded.",
            _dl != null && _dl.IsLoaded,
            "Open CSV Tab →",
            () => _tab = 2);

        DrawStep(7, "NavMesh",
            "NavMesh baked on floor so agents can pathfind.",
            IsNavMeshBaked(),
            "Open Navigation Window",
            () => EditorApplication.ExecuteMenuItem("Window/AI/Navigation (Obsolete)"));

        DrawSeparator();

        // Quick validate
        if (GUILayout.Button("▶  Run Full Validation", GUILayout.Height(32)))
            RunValidation();

        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox(
            "Keyboard shortcut: Ctrl+Shift+Z  |  Press Play once all steps are green.",
            MessageType.Info);
    }

    void DrawStep(int num, string title, string desc, bool done, string btnLabel, System.Action action)
    {
        Color prev = GUI.backgroundColor;
        GUI.backgroundColor = done ? new Color(0.15f, 0.4f, 0.15f) : new Color(0.4f, 0.15f, 0.15f);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        GUI.backgroundColor = prev;

        EditorGUILayout.BeginHorizontal();
        string icon = done ? "✓" : "✗";
        Color c = done ? COK : CERR;
        var style = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = c } };
        GUILayout.Label($"{icon}  Step {num}: {title}", style, GUILayout.ExpandWidth(true));

        if (!done)
        {
            if (GUILayout.Button(btnLabel, GUILayout.Width(220), GUILayout.Height(20)))
            {
                action?.Invoke();
                RefreshAll();
            }
        }

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField(desc, EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(2);
    }

    // ── Wizard Actions ───────────────────────────────────────────────

    void AutoWireSceneController()
    {
        // Get or create ___SimController
        if (_simController == null)
        {
            _simController = new GameObject("___SimController");
            Undo.RegisterCreatedObjectUndo(_simController, "Create SimController");
        }

        EnsureComponent<CrowdManager>(_simController);
        EnsureComponent<DataLoader>(_simController);
        EnsureComponent<AnalyticsManager>(_simController);
        EnsureComponent<SimulationHUD>(_simController);
        EnsureComponent<SessionScheduleLoader>(_simController);
        EnsureComponent<SimValidator>(_simController);

        // Wire DataLoader reference
        var cm = _simController.GetComponent<CrowdManager>();
        var dl = _simController.GetComponent<DataLoader>();
        var am = _simController.GetComponent<AnalyticsManager>();
        var hud = _simController.GetComponent<SimulationHUD>();
        var ssl = _simController.GetComponent<SessionScheduleLoader>();

        if (cm.dataLoader == null) { cm.dataLoader = dl; EditorUtility.SetDirty(cm); }
        if (am.dataLoader == null) { am.dataLoader = dl; EditorUtility.SetDirty(am); }
        if (am.crowdManager == null) { am.crowdManager = cm; EditorUtility.SetDirty(am); }
        if (hud.crowdManager == null) { hud.crowdManager = cm; EditorUtility.SetDirty(hud); }
        if (ssl.crowdManager == null) { ssl.crowdManager = cm; EditorUtility.SetDirty(ssl); }
        if (ssl.dataLoader   == null) { ssl.dataLoader   = dl; EditorUtility.SetDirty(ssl); }

        Selection.activeGameObject = _simController;
        Debug.Log("[ConferenceSetup] ___SimController created and wired.");
    }

    void AutoFindSpawnPoints()
    {
        var cm = Object.FindFirstObjectByType<CrowdManager>();
        if (cm == null) { AutoWireSceneController(); cm = Object.FindFirstObjectByType<CrowdManager>(); }

        // Look for GameObjects named StartPoint / Entrance / Spawn
        var candidates = new List<Transform>();
        string[] names = { "startpoint", "entrance", "spawn", "entry" };
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            string lower = go.name.ToLower();
            if (names.Any(n => lower.Contains(n)) && !lower.Contains("exit"))
                candidates.Add(go.transform);
        }

        if (candidates.Count > 0)
        {
            Undo.RecordObject(cm, "Assign Spawn Points");
            cm.spawnPoints = candidates.ToArray();
            EditorUtility.SetDirty(cm);
            Debug.Log($"[ConferenceSetup] Assigned {candidates.Count} spawn point(s): " +
                      string.Join(", ", candidates.Select(t => t.name)));
        }
        else
        {
            // Create a placeholder
            var go = new GameObject("StartPoint");
            Undo.RegisterCreatedObjectUndo(go, "Create StartPoint");
            go.transform.position = Vector3.zero;
            Undo.RecordObject(cm, "Assign Spawn Points");
            cm.spawnPoints = new[] { go.transform };
            EditorUtility.SetDirty(cm);
            Selection.activeGameObject = go;
            Debug.Log("[ConferenceSetup] Created StartPoint placeholder — move it to the venue entrance.");
        }
    }

    void AutoFindExitPoints()
    {
        var cm = Object.FindFirstObjectByType<CrowdManager>();
        if (cm == null) return;

        string[] names = { "door", "exit", "out", "gate" };
        var candidates = new List<Transform>();
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            string lower = go.name.ToLower();
            if (names.Any(n => lower.Contains(n)))
                candidates.Add(go.transform);
        }

        if (candidates.Count > 0)
        {
            Undo.RecordObject(cm, "Assign Exit Points");
            cm.exitPoints = candidates.ToArray();
            EditorUtility.SetDirty(cm);
            Debug.Log($"[ConferenceSetup] Assigned {candidates.Count} exit point(s).");
        }
        else
        {
            var go = new GameObject("ExitPoint");
            Undo.RegisterCreatedObjectUndo(go, "Create ExitPoint");
            go.transform.position = new Vector3(2f, 0, 0);
            Undo.RecordObject(cm, "Assign Exit Points");
            cm.exitPoints = new[] { go.transform };
            EditorUtility.SetDirty(cm);
            Selection.activeGameObject = go;
            Debug.Log("[ConferenceSetup] Created ExitPoint placeholder — move it to a venue door.");
        }
    }

    void CreateAgentPrefab()
    {
        // Make sure Prefabs folder exists
        if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
            AssetDatabase.CreateFolder("Assets", "Prefabs");

        // Build the agent GameObject
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "AgentPrefab";
        go.transform.localScale = new Vector3(0.6f, 1.0f, 0.6f);

        // Remove unwanted collider (NavMeshAgent handles avoidance)
        Object.DestroyImmediate(go.GetComponent<CapsuleCollider>());

        var nav = go.AddComponent<NavMeshAgent>();
        nav.radius      = 0.35f;
        nav.height      = 1.7f;
        nav.speed       = 1.34f;
        nav.angularSpeed = 0f;
        nav.updateRotation = false;

        go.AddComponent<AgentController>();
        go.AddComponent<AgentColorizer>();

        string path = "Assets/Prefabs/AgentPrefab.prefab";
        bool success;
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, path, out success);
        Object.DestroyImmediate(go);

        if (success && _cm != null)
        {
            Undo.RecordObject(_cm, "Assign Agent Prefab");
            _cm.agentPrefab = prefab;
            EditorUtility.SetDirty(_cm);
            Debug.Log($"[ConferenceSetup] Agent prefab created at {path}");
        }
    }

    bool IsNavMeshBaked()
    {
        NavMeshHit hit;
        return NavMesh.SamplePosition(Vector3.zero, out hit, 500f, NavMesh.AllAreas);
    }

    void RunValidation()
    {
        var v = Object.FindFirstObjectByType<SimValidator>();
        if (v == null)
        {
            EditorUtility.DisplayDialog("Validator Not Found",
                "Add a SimValidator component to ___SimController first (Step 1).", "OK");
            return;
        }

        var issues = v.Validate();
        int errors   = issues.Count(i => i.severity == SimValidator.Severity.Error);
        int warnings = issues.Count(i => i.severity == SimValidator.Severity.Warning);

        string msg = issues.Count == 0
            ? "✓ No issues found — ready to Play!"
            : string.Join("\n\n", issues.Select(i =>
                $"[{i.severity.ToString().ToUpper()}] {i.message}\n→ {i.fix}"));

        EditorUtility.DisplayDialog(
            $"Validation: {errors} error(s), {warnings} warning(s)", msg, "OK");
    }

    T EnsureComponent<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c == null) { c = Undo.AddComponent<T>(go); }
        return c;
    }

    // ════════════════════════════════════════════════════════════════
    // TAB 2 — ZONES
    // ════════════════════════════════════════════════════════════════

    void DrawZonesTab()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Conference Zones", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(
            "Each ConferenceZone marks a room or area. sensorId must match zone_id in your CSV.",
            EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(4);

        // Summary bar
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        EditorGUILayout.LabelField($"{_zones.Count} zone(s) in scene", EditorStyles.miniLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
            RefreshAll();
        EditorGUILayout.EndHorizontal();

        // Table header
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("Sensor ID",    GUILayout.Width(110));
        GUILayout.Label("Display Name", GUILayout.Width(140));
        GUILayout.Label("Area m²",      GUILayout.Width(60));
        GUILayout.Label("Goal Set",     GUILayout.Width(60));
        GUILayout.FlexibleSpace();
        GUILayout.Label("Actions",      GUILayout.Width(90));
        EditorGUILayout.EndHorizontal();

        // Zone rows
        foreach (var zone in _zones)
        {
            if (zone == null) continue;
            DrawZoneRow(zone);
        }

        if (_zones.Count == 0)
            EditorGUILayout.HelpBox("No zones found. Import from CSV or create one below.", MessageType.Info);

        DrawSeparator();

        // Add new zone
        EditorGUILayout.LabelField("Create New Zone", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Name / ID", GUILayout.Width(70));
        _newZoneName  = EditorGUILayout.TextField(_newZoneName, GUILayout.Width(120));
        _newZoneArea  = EditorGUILayout.FloatField(_newZoneArea, GUILayout.Width(50));
        _newZoneColor = EditorGUILayout.ColorField(_newZoneColor, GUILayout.Width(50));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("+ Create Zone", GUILayout.Width(110)))
            CreateZone(_newZoneName, _newZoneColor, _newZoneArea);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        // Import from CSV
        EditorGUILayout.LabelField("Import Zone IDs from CSV", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        _sensorCsvPath = EditorGUILayout.TextField(_sensorCsvPath);
        if (GUILayout.Button("Browse", GUILayout.Width(65)))
        {
            string p = EditorUtility.OpenFilePanel("Select sensor_data.csv", Application.streamingAssetsPath, "csv");
            if (!string.IsNullOrEmpty(p)) _sensorCsvPath = p;
        }
        EditorGUILayout.EndHorizontal();

        if (GUILayout.Button("Import Zone IDs from CSV"))
            ImportZoneIds(_sensorCsvPath);
    }

    void DrawZoneRow(ConferenceZone zone)
    {
        bool selected = Selection.activeGameObject == zone.gameObject;
        Color prev = GUI.backgroundColor;
        if (selected) GUI.backgroundColor = new Color(0.24f, 0.45f, 0.85f, 0.35f);

        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
        GUI.backgroundColor = prev;

        EditorGUI.BeginChangeCheck();
        zone.sensorId    = EditorGUILayout.TextField(zone.sensorId,    GUILayout.Width(110));
        zone.displayName = EditorGUILayout.TextField(zone.displayName, GUILayout.Width(140));
        zone.areaM2      = EditorGUILayout.FloatField(zone.areaM2,     GUILayout.Width(60));

        bool goalSet = zone.goalPoint != null;
        var  col     = goalSet ? COK : CWARN;
        GUIStyle gs  = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = col } };
        GUILayout.Label(goalSet ? "✓ Set" : "This obj", gs, GUILayout.Width(60));

        if (EditorGUI.EndChangeCheck()) EditorUtility.SetDirty(zone);

        GUILayout.FlexibleSpace();

        if (GUILayout.Button("Select", EditorStyles.miniButtonLeft, GUILayout.Width(50)))
        {
            Selection.activeGameObject = zone.gameObject;
            EditorGUIUtility.PingObject(zone.gameObject);
            SceneView.FrameLastActiveSceneView();
        }

        Color prevBg = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.8f, 0.2f, 0.2f);
        if (GUILayout.Button("✕", EditorStyles.miniButtonRight, GUILayout.Width(22)))
        {
            if (EditorUtility.DisplayDialog("Remove Zone",
                $"Delete ConferenceZone '{zone.displayName}'?", "Delete", "Cancel"))
            {
                Undo.DestroyObjectImmediate(zone.gameObject);
                RefreshAll();
                GUI.backgroundColor = prevBg;
                EditorGUILayout.EndHorizontal();
                return;
            }
        }
        GUI.backgroundColor = prevBg;

        EditorGUILayout.EndHorizontal();
    }

    void CreateZone(string id, Color color, float area)
    {
        var go = new GameObject(id);
        Undo.RegisterCreatedObjectUndo(go, "Create Zone");
        var z = go.AddComponent<ConferenceZone>();
        z.sensorId   = id;
        z.displayName = id;
        z.zoneColor  = color;
        z.areaM2     = area;
        Selection.activeGameObject = go;
        RefreshAll();
    }

    void ImportZoneIds(string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            EditorUtility.DisplayDialog("File Not Found", $"Cannot find:\n{csvPath}", "OK");
            return;
        }

        string[] lines = File.ReadAllLines(csvPath);
        if (lines.Length < 2) return;

        int col = -1;
        string[] hdr = lines[0].Split(',');
        for (int i = 0; i < hdr.Length; i++)
            if (hdr[i].Trim().ToLower() == "zone_id") { col = i; break; }

        if (col < 0)
        {
            EditorUtility.DisplayDialog("Column Not Found",
                "CSV must have a 'zone_id' column.", "OK");
            return;
        }

        var existing = new HashSet<string>(_zones.Select(z => z.sensorId));
        var ids      = new HashSet<string>();
        for (int i = 1; i < lines.Length; i++)
        {
            string[] c = lines[i].Split(',');
            if (c.Length > col) ids.Add(c[col].Trim());
        }

        int created = 0;
        foreach (var id in ids)
        {
            if (existing.Contains(id)) continue;
            Color color = Color.HSVToRGB((created * 0.17f) % 1f, 0.65f, 0.9f);
            CreateZone(id, color, 20f);
            created++;
        }

        RefreshAll();
        EditorUtility.DisplayDialog("Import Complete",
            $"Created {created} new zone(s).\n" +
            $"{ids.Count - created} zone(s) already existed.\n\n" +
            "Move each zone to its room centre and set gizmoBounds to room size.", "OK");
    }

    // ════════════════════════════════════════════════════════════════
    // TAB 3 — CSV DATA
    // ════════════════════════════════════════════════════════════════

    void DrawCSVTab()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("CSV Data Files", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        // ── Sensor data CSV ────────────────────────────────────────
        DrawSectionHeader("sensor_data.csv  (required)");
        EditorGUILayout.BeginHorizontal();
        _sensorCsvPath = EditorGUILayout.TextField(_sensorCsvPath);
        if (GUILayout.Button("Browse", GUILayout.Width(65)))
        {
            string p = EditorUtility.OpenFilePanel("Select sensor_data.csv",
                Application.streamingAssetsPath, "csv");
            if (!string.IsNullOrEmpty(p)) _sensorCsvPath = p;
        }
        if (GUILayout.Button("Streaming", GUILayout.Width(75)))
            _sensorCsvPath = Path.Combine(Application.streamingAssetsPath, "sensor_data.csv");
        EditorGUILayout.EndHorizontal();

        if (GUILayout.Button("Validate sensor_data.csv"))
            ValidateSensorCSV(_sensorCsvPath);

        DrawCSVIssues();
        DrawCSVPreview();

        DrawSeparator();

        // ── Session schedule CSV ───────────────────────────────────
        DrawSectionHeader("session_schedule.csv  (optional)");
        EditorGUILayout.BeginHorizontal();
        _scheduleCsvPath = EditorGUILayout.TextField(_scheduleCsvPath);
        if (GUILayout.Button("Browse", GUILayout.Width(65)))
        {
            string p = EditorUtility.OpenFilePanel("Select session_schedule.csv",
                Application.streamingAssetsPath, "csv");
            if (!string.IsNullOrEmpty(p)) _scheduleCsvPath = p;
        }
        if (GUILayout.Button("Streaming", GUILayout.Width(75)))
            _scheduleCsvPath = Path.Combine(Application.streamingAssetsPath, "session_schedule.csv");
        EditorGUILayout.EndHorizontal();

        if (GUILayout.Button("Validate session_schedule.csv"))
            ValidateScheduleCSV(_scheduleCsvPath);

        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox(
            "Required columns in sensor_data.csv:\n" +
            "  timestamp · zone_id · enters · exits · occupancy_snapshot\n\n" +
            "Required columns in session_schedule.csv:\n" +
            "  session_id · room_id · start_time · end_time · type · expected_attendance",
            MessageType.Info);
    }

    void ValidateSensorCSV(string path)
    {
        _csvIssues.Clear();
        _csvPreview.Clear();

        if (!File.Exists(path)) { _csvIssues.Add($"ERROR: File not found: {path}"); return; }

        string[] lines = File.ReadAllLines(path);
        string[] required = { "timestamp", "zone_id", "enters", "exits" };
        string[] hdr = lines[0].Split(',');

        foreach (var req in required)
            if (!hdr.Any(h => h.Trim().ToLower() == req))
                _csvIssues.Add($"ERROR: Missing column '{req}'");

        // Zone ID check vs scene
        var sceneIds = new HashSet<string>(_zones.Select(z => z.sensorId));
        int zoneCol = -1;
        for (int i = 0; i < hdr.Length; i++) if (hdr[i].Trim().ToLower() == "zone_id") { zoneCol = i; break; }

        if (zoneCol >= 0)
        {
            var csvZones = new HashSet<string>();
            for (int i = 1; i < lines.Length; i++)
            {
                var c = lines[i].Split(',');
                if (c.Length > zoneCol) csvZones.Add(c[zoneCol].Trim());
            }
            foreach (var z in csvZones)
                if (!sceneIds.Contains(z))
                    _csvIssues.Add($"WARN: zone_id '{z}' has no ConferenceZone in scene.");
            foreach (var z in sceneIds)
                if (!csvZones.Contains(z))
                    _csvIssues.Add($"INFO: ConferenceZone '{z}' has no rows in this CSV.");
        }

        if (_csvIssues.Count == 0) _csvIssues.Add("✓ CSV looks valid.");

        // Preview first 8 data rows
        _csvPreview.Add(lines[0]);
        for (int i = 1; i < Mathf.Min(lines.Length, 9); i++) _csvPreview.Add(lines[i]);
    }

    void ValidateScheduleCSV(string path)
    {
        _csvIssues.Clear();
        _csvPreview.Clear();

        if (!File.Exists(path)) { _csvIssues.Add($"WARN: File not found (optional): {path}"); return; }

        string[] lines = File.ReadAllLines(path);
        string[] required = { "session_id", "room_id", "start_time", "end_time" };
        string[] hdr = lines[0].Split(',');

        foreach (var req in required)
            if (!hdr.Any(h => h.Trim().ToLower() == req))
                _csvIssues.Add($"ERROR: Missing column '{req}'");

        // Check room_id vs scene zones
        var sceneIds = new HashSet<string>(_zones.Select(z => z.sensorId));
        int roomCol = -1;
        for (int i = 0; i < hdr.Length; i++) if (hdr[i].Trim().ToLower() == "room_id") { roomCol = i; break; }

        if (roomCol >= 0)
            for (int i = 1; i < lines.Length; i++)
            {
                var c = lines[i].Split(',');
                if (c.Length > roomCol && !sceneIds.Contains(c[roomCol].Trim()))
                    _csvIssues.Add($"WARN: room_id '{c[roomCol].Trim()}' row {i} has no ConferenceZone.");
            }

        if (_csvIssues.Count == 0) _csvIssues.Add("✓ Schedule CSV looks valid.");
        _csvPreview.Add(lines[0]);
        for (int i = 1; i < Mathf.Min(lines.Length, 9); i++) _csvPreview.Add(lines[i]);
    }

    void DrawCSVIssues()
    {
        foreach (var issue in _csvIssues)
        {
            MessageType mt = issue.StartsWith("ERROR") ? MessageType.Error
                           : issue.StartsWith("WARN")  ? MessageType.Warning
                           : issue.StartsWith("✓")     ? MessageType.Info
                           : MessageType.None;
            EditorGUILayout.HelpBox(issue, mt);
        }
    }

    void DrawCSVPreview()
    {
        if (_csvPreview.Count == 0) return;
        EditorGUILayout.LabelField("Preview (first 8 rows):", EditorStyles.boldLabel);
        foreach (var line in _csvPreview)
            EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
    }

    // ════════════════════════════════════════════════════════════════
    // TAB 4 — CONFIG
    // ════════════════════════════════════════════════════════════════

    void DrawConfigTab()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Simulation Config", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        // ── SimConfig asset ────────────────────────────────────────
        DrawSectionHeader("SimConfig Asset");
        EditorGUILayout.LabelField(
            "ScriptableObject holding all physics & behaviour parameters.",
            EditorStyles.wordWrappedMiniLabel);

        if (_cm != null)
        {
            _cm.config = (SimConfig)EditorGUILayout.ObjectField(
                "Config", _cm.config, typeof(SimConfig), false);
            if (_cm.config == null)
                EditorGUILayout.HelpBox("No SimConfig assigned — defaults will be used.", MessageType.Warning);
        }

        if (GUILayout.Button("Create New SimConfig Asset"))
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Save SimConfig", "SimConfig", "asset", "Save");
            if (!string.IsNullOrEmpty(path))
            {
                var asset = ScriptableObject.CreateInstance<SimConfig>();
                AssetDatabase.CreateAsset(asset, path);
                AssetDatabase.SaveAssets();
                if (_cm != null) { _cm.config = asset; EditorUtility.SetDirty(_cm); }
                Selection.activeObject = asset;
            }
        }

        DrawSeparator();

        // ── Time scale ────────────────────────────────────────────
        DrawSectionHeader("Runtime Controls");

        if (_cm != null)
        {
            EditorGUI.BeginChangeCheck();
            float newScale = EditorGUILayout.Slider("Time Scale Multiplier",
                _cm.timeScaleMultiplier, 0.1f, 120f);
            int pool = EditorGUILayout.IntField("Pool Warmup Size", _cm.poolWarmupSize);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_cm, "Edit CrowdManager");
                _cm.timeScaleMultiplier = newScale;
                _cm.poolWarmupSize      = pool;
                EditorUtility.SetDirty(_cm);
            }

            EditorGUILayout.HelpBox(
                $"At ×{newScale:F0}: 1 hour of sim = {3600f/newScale:F0} real seconds " +
                $"({3600f/newScale/60f:F1} real minutes).", MessageType.Info);
        }

        DrawSeparator();

        // ── Analytics paths ────────────────────────────────────────
        DrawSectionHeader("Output Paths");
        EditorGUILayout.LabelField("All outputs written to:", EditorStyles.miniLabel);
        EditorGUILayout.SelectableLabel(
            Path.Combine(Application.dataPath, "SimOutput"), EditorStyles.miniLabel, GUILayout.Height(16));

        if (GUILayout.Button("Open Output Folder"))
            EditorUtility.RevealInFinder(Path.Combine(Application.dataPath, "SimOutput"));

        DrawSeparator();

        // ── Key bindings reference ─────────────────────────────────
        DrawSectionHeader("Runtime Hotkeys");
        DrawKeyBinding("H", "Toggle HUD panel");
        DrawKeyBinding("M", "Toggle heatmap overlay");
    }

    void DrawKeyBinding(string key, string action)
    {
        EditorGUILayout.BeginHorizontal();
        var s = new GUIStyle(EditorStyles.miniLabel)
            { normal = { textColor = new Color(0.5f, 0.9f, 1f) }, fontStyle = FontStyle.Bold };
        GUILayout.Label($"[{key}]", s, GUILayout.Width(32));
        GUILayout.Label(action, EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
    }

    // ── Shared Helpers ───────────────────────────────────────────────

    void DrawSeparator()
    {
        EditorGUILayout.Space(4);
        Rect r = EditorGUILayout.GetControlRect(false, 1);
        EditorGUI.DrawRect(r, new Color(0.4f, 0.4f, 0.4f, 0.5f));
        EditorGUILayout.Space(4);
    }

    void DrawSectionHeader(string label)
    {
        EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
    }
}
