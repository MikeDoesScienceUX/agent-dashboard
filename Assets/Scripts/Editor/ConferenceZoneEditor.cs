using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Custom inspector + Scene handles for ConferenceZone.
///
/// In the Scene view (select a ConferenceZone):
///   • Cyan cube handles on each edge — drag to resize the zone footprint
///   • Yellow sphere handle on goal point — drag to reposition without selecting it
///   • Dimension labels update live while dragging
///   • Area m² auto-updates to match the resized footprint
///
/// In the Inspector:
///   • Create Goal Point — one click creates a child empty and assigns it
///   • Auto-size to Renderer / Collider — measures the mesh/collider and fits bounds
///   • Live occupancy bar during Play mode
/// </summary>
[CustomEditor(typeof(ConferenceZone))]
public class ConferenceZoneEditor : Editor
{
    SerializedProperty _sensorId, _displayName, _areaM2, _goalPoint,
                       _zoneColor, _gizmoBounds, _usePolygon;

    void OnEnable()
    {
        _sensorId    = serializedObject.FindProperty("sensorId");
        _displayName = serializedObject.FindProperty("displayName");
        _areaM2      = serializedObject.FindProperty("areaM2");
        _goalPoint   = serializedObject.FindProperty("goalPoint");
        _zoneColor   = serializedObject.FindProperty("zoneColor");
        _gizmoBounds = serializedObject.FindProperty("gizmoBounds");
        _usePolygon  = serializedObject.FindProperty("usePolygon");
    }

    // ── Inspector ────────────────────────────────────────────────────

    public override void OnInspectorGUI()
    {
        var zone = (ConferenceZone)target;
        serializedObject.Update();

        // ── Live stats during Play ──────────────────────────────────
        if (Application.isPlaying)
        {
            var cm = FindFirstObjectByType<CrowdManager>();
            if (cm != null)
            {
                int   n       = cm.GetZoneCount(zone.sensorId);
                float density = cm.GetBoothDensity(zone.sensorId);
                float fill    = Mathf.Clamp01(density / 3f);

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("LIVE", EditorStyles.boldLabel);

                // Occupancy bar
                Rect r = EditorGUILayout.GetControlRect(false, 14f);
                EditorGUI.DrawRect(r, new Color(0.15f, 0.15f, 0.15f));
                EditorGUI.DrawRect(new Rect(r.x, r.y, r.width * fill, r.height),
                    Color.Lerp(new Color(0.2f, 0.8f, 0.3f), new Color(0.9f, 0.2f, 0.2f), fill));
                EditorGUI.LabelField(r, $"  {n} dwelling  |  {density:F2} p/m²",
                    new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.white } });

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }
        }

        // ── Zone identity ───────────────────────────────────────────
        EditorGUILayout.LabelField("Zone Identity", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(_sensorId,    new GUIContent("Sensor ID",
            "Must match zone_id in sensor_data.csv exactly (case-sensitive)."));
        EditorGUILayout.PropertyField(_displayName, new GUIContent("Display Name",
            "Shown in HUD and editor tools."));
        EditorGUILayout.PropertyField(_areaM2,      new GUIContent("Floor Area m²",
            "Walkable area used for density = count / area. Use Auto-size buttons below."));

        // ── Goal point ──────────────────────────────────────────────
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Navigation Target", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(_goalPoint, new GUIContent("Goal Point",
            "Where agents navigate inside this zone. Drag the yellow handle in Scene view."));

        if (_goalPoint.objectReferenceValue == null)
        {
            EditorGUILayout.HelpBox(
                "No goal point — agents walk to this GameObject's position.\n" +
                "Create a child empty for precise placement.", MessageType.Info);
            if (GUILayout.Button("Create Goal Point (child empty)"))
                CreateGoalPoint(zone);
        }

        // ── Visualisation ───────────────────────────────────────────
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Zone Footprint", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(_zoneColor);
        EditorGUILayout.PropertyField(_gizmoBounds, new GUIContent("Bounds (X, Y, Z)",
            "Drag the cyan handles in Scene view, or use the buttons below."));

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Auto-size to Renderer"))  AutoSizeTo(zone, useCollider: false);
        if (GUILayout.Button("Auto-size to Collider"))  AutoSizeTo(zone, useCollider: true);
        EditorGUILayout.EndHorizontal();

        if (!zone.usePolygon)
            EditorGUILayout.HelpBox(
                "Select this zone and drag the cyan handles in the Scene view to resize.\n" +
                "Area m² updates automatically.", MessageType.None);

        // ── Polygon Mode ────────────────────────────────────────────
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Polygon Shape", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(_usePolygon, new GUIContent("Use Polygon",
            "Replace the rectangle with a freeform polygon. " +
            "Drag vertex spheres in the Scene view; click edge dots to split an edge."));
        if (EditorGUI.EndChangeCheck())
        {
            serializedObject.ApplyModifiedProperties();
            if (zone.usePolygon && zone.polyPoints.Count < 3)
                InitPolyFromBounds(zone);
        }

        if (zone.usePolygon)
        {
            EditorGUILayout.HelpBox(
                "Drag vertex spheres to reshape.\n" +
                "Click the small dots on edges to split an edge (add vertex).\n" +
                "Delete a vertex with the ✕ button below (min 3 vertices).",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to Bounds Rectangle"))
            {
                Undo.RecordObject(zone, "Reset Polygon");
                InitPolyFromBounds(zone);
                EditorUtility.SetDirty(zone);
            }
            if (GUILayout.Button("Fit Bounds to Polygon"))
            {
                Undo.RecordObject(zone, "Fit Bounds");
                FitBoundsToPolygon(zone);
                EditorUtility.SetDirty(zone);
            }
            EditorGUILayout.EndHorizontal();

            // Vertex list with delete buttons
            var pts = zone.polyPoints;
            EditorGUILayout.LabelField($"Vertices  ({pts.Count})", EditorStyles.miniLabel);
            int deleteIdx = -1;
            for (int i = 0; i < pts.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label($"  {i}:", GUILayout.Width(24));
                EditorGUI.BeginChangeCheck();
                float nx = EditorGUILayout.FloatField(pts[i].x, GUILayout.Width(64));
                float nz = EditorGUILayout.FloatField(pts[i].y, GUILayout.Width(64));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(zone, "Edit Vertex");
                    pts[i] = new Vector2(nx, nz);
                    zone.areaM2 = RoundTo(PolygonArea(pts), 1);
                    EditorUtility.SetDirty(zone);
                }
                Color prevBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.8f, 0.25f, 0.25f);
                bool canDelete = pts.Count > 3;
                EditorGUI.BeginDisabledGroup(!canDelete);
                if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
                    deleteIdx = i;
                EditorGUI.EndDisabledGroup();
                GUI.backgroundColor = prevBg;
                EditorGUILayout.EndHorizontal();
            }
            if (deleteIdx >= 0)
            {
                Undo.RecordObject(zone, "Delete Vertex");
                pts.RemoveAt(deleteIdx);
                zone.areaM2 = RoundTo(PolygonArea(pts), 1);
                EditorUtility.SetDirty(zone);
            }
        }

        serializedObject.ApplyModifiedProperties();
    }

    // ── Scene View Handles ───────────────────────────────────────────

    void OnSceneGUI()
    {
        var zone = (ConferenceZone)target;

        if (!Application.isPlaying)
        {
            if (zone.usePolygon && zone.polyPoints != null && zone.polyPoints.Count >= 3)
                DrawPolygonHandles(zone);
            else
                DrawResizeHandles(zone);
        }

        if (zone.goalPoint != null)
            DrawGoalHandle(zone);
    }

    // ── Polygon Handles ──────────────────────────────────────────────

    void DrawPolygonHandles(ConferenceZone zone)
    {
        var pts = zone.polyPoints;
        int n   = pts.Count;
        float y = zone.transform.position.y + 0.02f;

        // Draw closed outline
        var lineVerts = new Vector3[n + 1];
        for (int i = 0; i < n; i++)
        {
            lineVerts[i]   = LocalToWorld(zone, pts[i]);
            lineVerts[i].y = y;
        }
        lineVerts[n] = lineVerts[0];
        Handles.color = zone.zoneColor;
        Handles.DrawAAPolyLine(3f, lineVerts);

        // Vertex handles — drag to reshape
        for (int i = 0; i < n; i++)
        {
            Vector3 wp = LocalToWorld(zone, pts[i]);
            wp.y       = y;
            float sz   = HandleUtility.GetHandleSize(wp) * 0.12f;

            Handles.color = zone.zoneColor;
            EditorGUI.BeginChangeCheck();
            Vector3 newWp = Handles.FreeMoveHandle(
                wp, sz, Vector3.zero, Handles.SphereHandleCap);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(zone, "Move Polygon Vertex");
                newWp.y  = zone.transform.position.y;
                pts[i]   = WorldToLocal(zone, newWp);
                zone.areaM2 = RoundTo(PolygonArea(pts), 1);
                EditorUtility.SetDirty(zone);
            }

            // Vertex index label
            Handles.Label(wp + Vector3.up * (sz + 0.15f), i.ToString(),
                new GUIStyle { normal = { textColor = zone.zoneColor }, fontSize = 9 });
        }

        // Edge midpoint dots — click to split edge and insert a new vertex
        Color splitCol = new Color(zone.zoneColor.r, zone.zoneColor.g, zone.zoneColor.b, 0.55f);
        for (int i = 0; i < n; i++)
        {
            int     j   = (i + 1) % n;
            Vector3 a   = LocalToWorld(zone, pts[i]); a.y = y;
            Vector3 b   = LocalToWorld(zone, pts[j]); b.y = y;
            Vector3 mid = (a + b) * 0.5f;
            float   sz  = HandleUtility.GetHandleSize(mid) * 0.07f;

            Handles.color = splitCol;
            if (Handles.Button(mid, Quaternion.identity, sz, sz * 1.5f, Handles.DotHandleCap))
            {
                Undo.RecordObject(zone, "Split Polygon Edge");
                pts.Insert(j, (pts[i] + pts[j]) * 0.5f);
                zone.areaM2 = RoundTo(PolygonArea(pts), 1);
                EditorUtility.SetDirty(zone);
                GUIUtility.ExitGUI(); // prevent layout error after list resize
            }
        }
    }

    void DrawResizeHandles(ConferenceZone zone)
    {
        Vector3 origin = zone.transform.position;
        float   hx     = zone.gizmoBounds.x * 0.5f;
        float   hz     = zone.gizmoBounds.z * 0.5f;
        float   cy     = origin.y + zone.gizmoBounds.y * 0.5f; // handle mid-height

        // Edge midpoints
        Vector3 pX = new Vector3(origin.x + hx, cy, origin.z);
        Vector3 nX = new Vector3(origin.x - hx, cy, origin.z);
        Vector3 pZ = new Vector3(origin.x, cy, origin.z + hz);
        Vector3 nZ = new Vector3(origin.x, cy, origin.z - hz);

        float hSize = HandleUtility.GetHandleSize(origin) * 0.11f;
        Handles.color = zone.zoneColor;

        // +X handle — anchors the -X edge, moves only the +X side
        EditorGUI.BeginChangeCheck();
        Vector3 nPX = Handles.Slider(pX, Vector3.right, hSize, Handles.CubeHandleCap, 0.25f);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(zone.transform, "Resize Zone");
            Undo.RecordObject(zone, "Resize Zone");
            float fixedEdge = origin.x - hx;
            float newHalf   = Mathf.Max(0.25f, (nPX.x - fixedEdge) / 2f);
            var p = zone.transform.position; p.x = fixedEdge + newHalf;
            zone.transform.position = p;
            zone.gizmoBounds.x = newHalf * 2f;
            zone.areaM2 = RoundTo(zone.gizmoBounds.x * zone.gizmoBounds.z, 1);
            EditorUtility.SetDirty(zone);
        }

        // -X handle — anchors the +X edge, moves only the -X side
        EditorGUI.BeginChangeCheck();
        Vector3 nNX = Handles.Slider(nX, -Vector3.right, hSize, Handles.CubeHandleCap, 0.25f);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(zone.transform, "Resize Zone");
            Undo.RecordObject(zone, "Resize Zone");
            float fixedEdge = origin.x + hx;
            float newHalf   = Mathf.Max(0.25f, (fixedEdge - nNX.x) / 2f);
            var p = zone.transform.position; p.x = fixedEdge - newHalf;
            zone.transform.position = p;
            zone.gizmoBounds.x = newHalf * 2f;
            zone.areaM2 = RoundTo(zone.gizmoBounds.x * zone.gizmoBounds.z, 1);
            EditorUtility.SetDirty(zone);
        }

        // +Z handle — anchors the -Z edge, moves only the +Z side
        EditorGUI.BeginChangeCheck();
        Vector3 nPZ = Handles.Slider(pZ, Vector3.forward, hSize, Handles.CubeHandleCap, 0.25f);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(zone.transform, "Resize Zone");
            Undo.RecordObject(zone, "Resize Zone");
            float fixedEdge = origin.z - hz;
            float newHalf   = Mathf.Max(0.25f, (nPZ.z - fixedEdge) / 2f);
            var p = zone.transform.position; p.z = fixedEdge + newHalf;
            zone.transform.position = p;
            zone.gizmoBounds.z = newHalf * 2f;
            zone.areaM2 = RoundTo(zone.gizmoBounds.x * zone.gizmoBounds.z, 1);
            EditorUtility.SetDirty(zone);
        }

        // -Z handle — anchors the +Z edge, moves only the -Z side
        EditorGUI.BeginChangeCheck();
        Vector3 nNZ = Handles.Slider(nZ, -Vector3.forward, hSize, Handles.CubeHandleCap, 0.25f);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(zone.transform, "Resize Zone");
            Undo.RecordObject(zone, "Resize Zone");
            float fixedEdge = origin.z + hz;
            float newHalf   = Mathf.Max(0.25f, (fixedEdge - nNZ.z) / 2f);
            var p = zone.transform.position; p.z = fixedEdge - newHalf;
            zone.transform.position = p;
            zone.gizmoBounds.z = newHalf * 2f;
            zone.areaM2 = RoundTo(zone.gizmoBounds.x * zone.gizmoBounds.z, 1);
            EditorUtility.SetDirty(zone);
        }

        // Dimension labels
        GUIStyle dimStyle = new GUIStyle
            { normal = { textColor = zone.zoneColor }, fontSize = 10, fontStyle = FontStyle.Bold };

        Handles.Label(pX + Vector3.right   * 0.25f, $" {zone.gizmoBounds.x:F1} m", dimStyle);
        Handles.Label(pZ + Vector3.forward * 0.25f, $" {zone.gizmoBounds.z:F1} m", dimStyle);

        // Area label at center
        GUIStyle areaStyle = new GUIStyle
            { normal = { textColor = Color.white }, fontSize = 9,
              alignment = TextAnchor.MiddleCenter };
        Handles.Label(origin + Vector3.up * 0.1f, $"{zone.areaM2:F0} m²", areaStyle);
    }

    void DrawGoalHandle(ConferenceZone zone)
    {
        Handles.color = Color.yellow;
        float sz = HandleUtility.GetHandleSize(zone.goalPoint.position) * 0.18f;

        EditorGUI.BeginChangeCheck();
        var fmh_211_13_639105510758957206 = Quaternion.identity; Vector3 newPos = Handles.FreeMoveHandle(
            zone.goalPoint.position, sz,
            Vector3.zero, Handles.SphereHandleCap);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(zone.goalPoint, "Move Goal Point");
            newPos.y = zone.goalPoint.position.y; // keep on floor
            zone.goalPoint.position = newPos;
        }

        GUIStyle gs = new GUIStyle
            { normal = { textColor = Color.yellow }, fontSize = 9 };
        Handles.Label(zone.goalPoint.position + Vector3.up * (sz + 0.2f), "Goal ◎", gs);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    void CreateGoalPoint(ConferenceZone zone)
    {
        var go = new GameObject("GoalPoint");
        Undo.RegisterCreatedObjectUndo(go, "Create Goal Point");
        go.transform.SetParent(zone.transform);
        go.transform.localPosition = Vector3.zero;
        Undo.RecordObject(zone, "Assign Goal Point");
        zone.goalPoint = go.transform;
        EditorUtility.SetDirty(zone);
        Selection.activeGameObject = go;
    }

    void AutoSizeTo(ConferenceZone zone, bool useCollider)
    {
        Bounds? b = null;

        if (useCollider)
        {
            var col = zone.GetComponentInChildren<Collider>();
            if (col != null) b = col.bounds;
        }
        else
        {
            var rend = zone.GetComponentInChildren<Renderer>();
            if (rend != null) b = rend.bounds;
        }

        if (b == null)
        {
            Debug.LogWarning($"[ZoneEditor] No {(useCollider ? "Collider" : "Renderer")} " +
                             $"found on '{zone.name}'.");
            return;
        }

        Undo.RecordObject(zone, "Auto-size Zone");
        zone.gizmoBounds = new Vector3(b.Value.size.x, 0.5f, b.Value.size.z);
        zone.areaM2      = RoundTo(b.Value.size.x * b.Value.size.z, 1);
        EditorUtility.SetDirty(zone);
    }

    // ── Polygon Helpers ──────────────────────────────────────────────

    static Vector3 LocalToWorld(ConferenceZone zone, Vector2 local)
        => zone.transform.position + new Vector3(local.x, 0, local.y);

    static Vector2 WorldToLocal(ConferenceZone zone, Vector3 world)
        => new Vector2(world.x - zone.transform.position.x,
                       world.z - zone.transform.position.z);

    /// <summary>Shoelace formula for signed polygon area (XZ plane).</summary>
    static float PolygonArea(List<Vector2> pts)
    {
        float area = 0f;
        int   n    = pts.Count;
        for (int i = 0; i < n; i++)
        {
            int j  = (i + 1) % n;
            area  += pts[i].x * pts[j].y - pts[j].x * pts[i].y;
        }
        return Mathf.Abs(area) * 0.5f;
    }

    /// <summary>Initialise a 4-point clockwise rectangle from gizmoBounds.</summary>
    static void InitPolyFromBounds(ConferenceZone zone)
    {
        float hx = zone.gizmoBounds.x * 0.5f;
        float hz = zone.gizmoBounds.z * 0.5f;
        zone.polyPoints = new List<Vector2>
        {
            new Vector2(-hx, -hz),
            new Vector2( hx, -hz),
            new Vector2( hx,  hz),
            new Vector2(-hx,  hz),
        };
        zone.areaM2 = RoundTo(PolygonArea(zone.polyPoints), 1);
        EditorUtility.SetDirty(zone);
    }

    /// <summary>Fit gizmoBounds to the AABB of the current polygon (useful for density calc).</summary>
    static void FitBoundsToPolygon(ConferenceZone zone)
    {
        if (zone.polyPoints.Count < 3) return;
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in zone.polyPoints)
        {
            if (p.x < minX) minX = p.x;  if (p.x > maxX) maxX = p.x;
            if (p.y < minZ) minZ = p.y;  if (p.y > maxZ) maxZ = p.y;
        }
        zone.gizmoBounds.x = maxX - minX;
        zone.gizmoBounds.z = maxZ - minZ;
        EditorUtility.SetDirty(zone);
    }

    static float RoundTo(float v, int decimals)
    {
        float f = Mathf.Pow(10, decimals);
        return Mathf.Round(v * f) / f;
    }
}
