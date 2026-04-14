using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Click-to-place Scene view toolbar for ConferenceZone, spawn points, and exit points.
///
/// Works with any floor — ProBuilder, primitive, or glTF import.
///   • If the floor has a MeshCollider, placement snaps to the mesh surface.
///   • If not (e.g. raw glTF import without a collider), placement uses the Floor Y
///     value shown in the toolbar. Press [↺] to auto-detect from existing spawn points,
///     or type the Y coordinate manually.
///
/// Toolbar (top-left of Scene view):
///   [+ Zone] [+ Spawn] [+ Exit]   Y: [field] [↺] [✕]
///   • Click a mode button → ghost preview follows mouse.
///   • Left-click in Scene → object placed and auto-wired into CrowdManager.
///   • Escape or [✕] → cancel.
///
/// Live Play overlay (always active while in Play mode):
///   Coloured outlines + occupancy labels above each ConferenceZone;
///   green/red spheres at spawn and exit points.
///
/// Keyboard shortcuts (also in Conference Sim menu):
///   Ctrl+Alt+Z → Place Zone
///   Ctrl+Alt+S → Place Spawn Point
///   Ctrl+Alt+E → Place Exit Point
/// </summary>
[InitializeOnLoad]
public static class ConferenceSceneTool
{
    // ── Placement state ──────────────────────────────────────────────

    private enum PlaceMode { None, Zone, Spawn, Exit }

    private static PlaceMode _mode      = PlaceMode.None;
    private static int       _controlId = -1;
    private static Vector3   _lastHitPos;
    private static bool      _validHit;

    private static int _zoneCounter  = 1;
    private static int _spawnCounter = 1;
    private static int _exitCounter  = 1;

    // ── Floor Y (persisted, critical for glTF floors without MeshCollider) ──

    private static float FloorY
    {
        get => EditorPrefs.GetFloat("ConferenceSceneTool_FloorY", 0f);
        set => EditorPrefs.SetFloat("ConferenceSceneTool_FloorY", value);
    }

    // ── Style cache ──────────────────────────────────────────────────

    private static GUIStyle _btnStyle;
    private static GUIStyle _labelStyle;
    private static bool     _stylesReady;

    // ── Static constructor ───────────────────────────────────────────

    static ConferenceSceneTool()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        SceneView.duringSceneGui += OnSceneGUI;

        // Cancel placement before domain reload to avoid stale state
        AssemblyReloadEvents.beforeAssemblyReload -= CancelPlacement;
        AssemblyReloadEvents.beforeAssemblyReload += CancelPlacement;
    }

    // ── Main scene GUI callback ──────────────────────────────────────

    private static void OnSceneGUI(SceneView view)
    {
        EnsureStyles();

        // IMGUI requires GetControlID to be called unconditionally every OnSceneGUI frame
        // in the same call-stack position — conditional calls break hot-control tracking
        _controlId = GUIUtility.GetControlID(FocusType.Passive);

        // Always draw live overlay during Play
        if (Application.isPlaying)
            DrawPlayOverlay();

        // Ghost preview (3D Handles, outside GUI block — works on all render pipelines)
        if (_mode != PlaceMode.None && _validHit)
            DrawGhost(_lastHitPos, _mode);

        // Toolbar overlay
        Handles.BeginGUI();
        DrawToolbar();
        Handles.EndGUI();

        // Handle keyboard/mouse input
        if (_mode != PlaceMode.None)
            HandlePlacementInput();
    }

    // ── Toolbar ──────────────────────────────────────────────────────

    private static void DrawToolbar()
    {
        // Width: 3 mode buttons (72×3) + flex + Y label + field + auto-detect + cancel + padding
        const float W = 350f, H = 26f;
        GUILayout.BeginArea(new Rect(10, 10, W, H), GUI.skin.box);
        GUILayout.BeginHorizontal();

        DrawModeButton("+ Zone",  PlaceMode.Zone,  new Color(0.3f, 0.8f, 1.0f));
        DrawModeButton("+ Spawn", PlaceMode.Spawn, new Color(0.3f, 1.0f, 0.4f));
        DrawModeButton("+ Exit",  PlaceMode.Exit,  new Color(1.0f, 0.5f, 0.4f));

        GUILayout.FlexibleSpace();

        // Floor Y — critical for glTF meshes that have no MeshCollider
        GUILayout.Label("Y:", _btnStyle, GUILayout.Width(16));
        float newY = EditorGUILayout.FloatField(FloorY, GUILayout.Width(44));
        if (!Mathf.Approximately(newY, FloorY)) FloorY = newY;

        if (GUILayout.Button(new GUIContent("↺", "Auto-detect floor Y from spawn points or zones"),
                             _btnStyle, GUILayout.Width(22)))
            AutoDetectFloorY();

        if (_mode != PlaceMode.None)
        {
            GUILayout.Space(4);
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button("✕", _btnStyle, GUILayout.Width(22)))
                CancelPlacement();
            GUI.backgroundColor = prev;
        }

        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    private static void DrawModeButton(string label, PlaceMode pm, Color activeColor)
    {
        bool active = _mode == pm;
        Color prev  = GUI.backgroundColor;
        if (active) GUI.backgroundColor = activeColor;

        if (GUILayout.Button(label, _btnStyle, GUILayout.Width(72)))
        {
            if (active) CancelPlacement();
            else        EnterMode(pm);
        }

        GUI.backgroundColor = prev;
    }

    // ── Ghost preview (Handles-based — no GameObjects, no shader issues) ──

    private static void DrawGhost(Vector3 pos, PlaceMode pm)
    {
        switch (pm)
        {
            case PlaceMode.Zone:
            {
                float hx = 2f, hz = 2f;
                float cy = pos.y + 0.01f;
                var verts = new Vector3[]
                {
                    new Vector3(pos.x - hx, cy, pos.z - hz),
                    new Vector3(pos.x + hx, cy, pos.z - hz),
                    new Vector3(pos.x + hx, cy, pos.z + hz),
                    new Vector3(pos.x - hx, cy, pos.z + hz),
                };
                Handles.DrawSolidRectangleWithOutline(verts,
                    new Color(0.30f, 0.80f, 1.00f, 0.18f),
                    new Color(0.30f, 0.80f, 1.00f, 0.90f));

                // Volume wireframe
                Handles.color = new Color(0.30f, 0.80f, 1.00f, 0.55f);
                Handles.DrawWireCube(pos + Vector3.up * 0.25f, new Vector3(4f, 0.5f, 4f));

                // Label below ghost
                Handles.Label(pos + Vector3.up * 0.7f, "  Zone (4×4 m)", EnsureLabelStyle(Color.cyan));
                break;
            }

            case PlaceMode.Spawn:
            {
                Handles.color = new Color(0.30f, 1.00f, 0.40f, 0.90f);
                float sz = HandleUtility.GetHandleSize(pos) * 0.35f;
                Handles.SphereHandleCap(0, pos + Vector3.up * sz * 0.5f,
                    Quaternion.identity, sz, EventType.Repaint);
                Handles.DrawDottedLine(pos, pos + Vector3.up * sz * 2.5f, 4f);
                Handles.Label(pos + Vector3.up * sz * 3f, "  Spawn", EnsureLabelStyle(Color.green));
                break;
            }

            case PlaceMode.Exit:
            {
                Handles.color = new Color(1.00f, 0.40f, 0.40f, 0.90f);
                float sz = HandleUtility.GetHandleSize(pos) * 0.35f;
                Handles.SphereHandleCap(0, pos + Vector3.up * sz * 0.5f,
                    Quaternion.identity, sz, EventType.Repaint);
                Handles.DrawDottedLine(pos, pos + Vector3.up * sz * 2.5f, 4f);
                Handles.Label(pos + Vector3.up * sz * 3f, "  Exit", EnsureLabelStyle(Color.red));
                break;
            }
        }
    }

    // ── Placement flow ───────────────────────────────────────────────

    private static void EnterMode(PlaceMode pm)
    {
        _mode     = pm;
        _validHit = false;
        SceneView.RepaintAll();
    }

    private static void CancelPlacement()
    {
        _mode     = PlaceMode.None;
        _validHit = false;
        SceneView.RepaintAll();
    }

    private static void HandlePlacementInput()
    {
        Event e = Event.current;

        // Escape cancels
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            CancelPlacement();
            e.Use();
            return;
        }

        // Block Unity's default object-selection click while we own the mouse
        HandleUtility.AddDefaultControl(_controlId);

        // Only sample the floor ray on actual mouse events —
        // mousePosition is undefined during Repaint/Layout passes
        bool isMouse = e.type == EventType.MouseMove  ||
                       e.type == EventType.MouseDrag  ||
                       e.type == EventType.MouseDown  ||
                       e.type == EventType.MouseUp;
        if (isMouse)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            _lastHitPos = RaycastFloor(ray);
            _validHit   = true;
        }

        // Left mouse button — place on MouseDown, consume MouseUp too to prevent selection
        if (e.button == 0 && !e.alt)
        {
            if (e.type == EventType.MouseDown)
            {
                Place(_lastHitPos);
                e.Use();
            }
            else if (e.type == EventType.MouseUp)
            {
                e.Use();
            }
        }

        // Keep ghost moving with the mouse
        if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            SceneView.RepaintAll();
    }

    /// <summary>
    /// Floor raycast strategy:
    ///   1. Physics.Raycast against scene colliders — works if the floor has a MeshCollider.
    ///      For glTF imports: select the floor mesh in the Inspector and add a MeshCollider,
    ///      OR just set the Floor Y field in the toolbar to your floor height.
    ///   2. Intersect with a horizontal plane at Floor Y — reliable fallback for any floor.
    /// </summary>
    private static Vector3 RaycastFloor(Ray ray)
    {
        if (Physics.Raycast(ray, out RaycastHit hit, 500f))
            return hit.point;

        // Fallback: plane at Floor Y
        float denom = ray.direction.y;
        if (Mathf.Abs(denom) > 0.0001f)
        {
            float t = (FloorY - ray.origin.y) / denom;
            if (t > 0f) return ray.origin + ray.direction * t;
        }

        return ray.origin + ray.direction * 10f;
    }

    private static void Place(Vector3 pos)
    {
        switch (_mode)
        {
            case PlaceMode.Zone:  PlaceZone(pos);  break;
            case PlaceMode.Spawn: PlaceSpawn(pos); break;
            case PlaceMode.Exit:  PlaceExit(pos);  break;
        }
    }

    // ── Zone ─────────────────────────────────────────────────────────

    private static void PlaceZone(Vector3 pos)
    {
        string name = $"Zone_{_zoneCounter++:D2}";
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Place Conference Zone");
        go.transform.position = pos;

        var zone          = go.AddComponent<ConferenceZone>();
        zone.sensorId     = name.ToLower();
        zone.displayName  = name;
        zone.gizmoBounds  = new Vector3(4f, 0.5f, 4f);
        zone.areaM2       = 16f;

        // Auto-create a goal point child so agents have a precise nav target
        var goalGo = new GameObject("GoalPoint");
        Undo.RegisterCreatedObjectUndo(goalGo, "Create Goal Point");
        goalGo.transform.SetParent(go.transform);
        goalGo.transform.localPosition = Vector3.zero;
        zone.goalPoint = goalGo.transform;

        EditorUtility.SetDirty(go);
        Selection.activeGameObject = go;

        Debug.Log($"[SceneTool] Placed ConferenceZone '{name}' at {pos}. " +
                  "Update sensorId in the Inspector to match your CSV zone_id.");
    }

    // ── Spawn point ───────────────────────────────────────────────────

    private static void PlaceSpawn(Vector3 pos)
    {
        string name = $"SpawnPoint_{_spawnCounter++:D2}";
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Place Spawn Point");
        go.transform.position = pos;

        WirePoint(go.transform, isSpawn: true);
        Selection.activeGameObject = go;
    }

    // ── Exit point ────────────────────────────────────────────────────

    private static void PlaceExit(Vector3 pos)
    {
        string name = $"ExitPoint_{_exitCounter++:D2}";
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Place Exit Point");
        go.transform.position = pos;

        WirePoint(go.transform, isSpawn: false);
        Selection.activeGameObject = go;
    }

    private static void WirePoint(Transform t, bool isSpawn)
    {
        var cm = Object.FindFirstObjectByType<CrowdManager>();
        if (cm == null)
        {
            Debug.LogWarning("[SceneTool] No CrowdManager in scene — point placed but not auto-wired.");
            return;
        }

        Undo.RecordObject(cm, isSpawn ? "Add Spawn Point" : "Add Exit Point");

        if (isSpawn)
        {
            var list = new List<Transform>(cm.spawnPoints ?? new Transform[0]) { t };
            cm.spawnPoints = list.ToArray();
        }
        else
        {
            var list = new List<Transform>(cm.exitPoints ?? new Transform[0]) { t };
            cm.exitPoints = list.ToArray();
        }

        EditorUtility.SetDirty(cm);
    }

    // ── Floor Y auto-detect ───────────────────────────────────────────

    private static void AutoDetectFloorY()
    {
        // Priority 1: first valid spawn point (most reliable)
        var cm = Object.FindFirstObjectByType<CrowdManager>();
        if (cm?.spawnPoints != null)
        {
            foreach (var sp in cm.spawnPoints)
            {
                if (sp == null) continue;
                FloorY = sp.position.y;
                Debug.Log($"[SceneTool] Floor Y = {FloorY:F3} (from spawn point '{sp.name}').");
                return;
            }
        }

        // Priority 2: first ConferenceZone
        var zone = Object.FindFirstObjectByType<ConferenceZone>();
        if (zone != null)
        {
            FloorY = zone.transform.position.y;
            Debug.Log($"[SceneTool] Floor Y = {FloorY:F3} (from ConferenceZone '{zone.sensorId}').");
            return;
        }

        Debug.Log($"[SceneTool] No reference objects found — Floor Y stays at {FloorY:F3}. " +
                  "Set it manually in the toolbar to match your glTF floor height.");
    }

    // ── Live Play overlay ─────────────────────────────────────────────

    private static void DrawPlayOverlay()
    {
        var cm = Object.FindFirstObjectByType<CrowdManager>();
        if (cm?.Zones == null) return;

        foreach (var zone in cm.Zones)
        {
            if (zone == null) continue;

            float density = cm.GetBoothDensity(zone.sensorId);
            int   count   = cm.GetZoneCount(zone.sensorId);
            float fill    = Mathf.Clamp01(density / 3f);
            Color col     = Color.Lerp(new Color(0.20f, 0.90f, 0.30f),
                                       new Color(0.90f, 0.20f, 0.20f), fill);
            col.a = 0.9f;

            // Coloured outline at the top face of the zone bounds
            Vector3 c  = zone.transform.position;
            Vector3 hb = zone.gizmoBounds * 0.5f;
            float   cy = c.y + hb.y;

            Handles.color = col;
            Handles.DrawAAPolyLine(4f,
                new Vector3(c.x - hb.x, cy, c.z - hb.z),
                new Vector3(c.x + hb.x, cy, c.z - hb.z),
                new Vector3(c.x + hb.x, cy, c.z + hb.z),
                new Vector3(c.x - hb.x, cy, c.z + hb.z),
                new Vector3(c.x - hb.x, cy, c.z - hb.z));

            Handles.Label(
                c + Vector3.up * (hb.y + 0.35f),
                $"{zone.displayName}\n{count} dwellers  |  {density:F2} p/m²",
                EnsureLabelStyle(col));
        }

        DrawPointMarkers(cm.spawnPoints, new Color(0.30f, 1.00f, 0.40f, 0.85f));
        DrawPointMarkers(cm.exitPoints,  new Color(1.00f, 0.40f, 0.40f, 0.85f));
    }

    private static void DrawPointMarkers(Transform[] points, Color col)
    {
        if (points == null) return;
        Handles.color = col;
        foreach (var p in points)
        {
            if (p == null) continue;
            float sz = HandleUtility.GetHandleSize(p.position) * 0.22f;
            Handles.SphereHandleCap(0, p.position, Quaternion.identity, sz, EventType.Repaint);
        }
    }

    // ── Style helpers ─────────────────────────────────────────────────

    private static void EnsureStyles()
    {
        if (_stylesReady) return;
        _stylesReady = true;

        _btnStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize    = 11,
            fontStyle   = FontStyle.Bold,
            fixedHeight = 20,
        };
    }

    private static GUIStyle EnsureLabelStyle(Color col)
    {
        if (_labelStyle == null)
        {
            _labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize  = 9,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
        }
        _labelStyle.normal.textColor = col;
        return _labelStyle;
    }

    // ── Menu items ────────────────────────────────────────────────────

    [MenuItem("Conference Sim/Place Zone %&z")]
    private static void MenuPlaceZone()  => EnterMode(PlaceMode.Zone);

    [MenuItem("Conference Sim/Place Spawn Point %&s")]
    private static void MenuPlaceSpawn() => EnterMode(PlaceMode.Spawn);

    [MenuItem("Conference Sim/Place Exit Point %&e")]
    private static void MenuPlaceExit()  => EnterMode(PlaceMode.Exit);
}
