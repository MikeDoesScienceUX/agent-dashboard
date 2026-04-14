using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click scene wiring for the EASL 2026 sample scene.
///
/// Menu: Conference Sim → Auto-Setup Sample Scene
///
/// Steps performed:
///   0. Remove any "Missing Script" components from ___SimController
///   1. Create ___SimController and add all required components
///   2. Create SimConfig asset (Assets/SimConfig.asset) if none exists, assign to CrowdManager
///   3. Add ConferenceZone to Zone_01 / Zone_02 / Zone_04 / Zone_06 with matching sensorIds
///   4. Wire SpawnPoint_01 → spawnPoints, ExitPoint_01 → exitPoints
///   5. Wire all component cross-references (DataLoader, AnalyticsManager, etc.)
///   6. Mark scene dirty
/// </summary>
public static class SceneAutoSetup
{
    // ── Zone definition table ─────────────────────────────────────────
    // Update boundsX/Z and areaM2 to match your actual room sizes.
    // sensorId must match zone_id in sensor_data.csv exactly.

    private struct ZoneDef
    {
        public string sceneName;
        public string sensorId;
        public string displayName;
        public float  areaM2;
        public float  boundsX;
        public float  boundsZ;
        public Color  color;
    }

    private static readonly ZoneDef[] Zones =
    {
        new ZoneDef { sceneName = "EASL2026 - Hall 7 v", sensorId = "hall_7", displayName = "Hall 7",
                      areaM2 = 120f, boundsX = 12f, boundsZ = 10f, color = new Color(0.9f, 0.3f, 0.3f) },
        new ZoneDef { sceneName = "Hall 8", sensorId = "hall_8", displayName = "Hall 8",
                      areaM2 =  80f, boundsX = 10f, boundsZ =  8f, color = new Color(0.3f, 0.6f, 1.0f) },
        new ZoneDef { sceneName = "Hall 8.1", sensorId = "hall_81", displayName = "Hall 8.1",
                      areaM2 =  80f, boundsX = 10f, boundsZ =  8f, color = new Color(0.4f, 0.9f, 0.5f) },
        new ZoneDef { sceneName = "Hall 8.1 Mezzanine", sensorId = "hall_81_mezzanine", displayName = "Hall 8.1 Mezzanine",
                      areaM2 = 100f, boundsX = 12f, boundsZ =  9f, color = new Color(1.0f, 0.8f, 0.2f) },
    };

    private const string SimConfigPath = "Assets/SimConfig.asset";

    // ── Menu entry ─────────────────────────────────────────────────────

    [MenuItem("Conference Sim/Auto-Setup Sample Scene")]
    public static void Run()
    {
        var warnings = new List<string>();
        int zonesConfigured = 0;
        int missingRemoved  = 0;

        // ── 0. Remove missing scripts ─────────────────────────────────
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            if (removed > 0)
            {
                missingRemoved += removed;
                EditorUtility.SetDirty(go);
                Debug.Log($"[AutoSetup] Removed {removed} missing script(s) from '{go.name}'.");
            }
        }

        // ── 1. Create / find ___SimController ─────────────────────────
        GameObject simCtrl = GameObject.Find("___SimController");
        if (simCtrl == null)
        {
            simCtrl = new GameObject("___SimController");
            Undo.RegisterCreatedObjectUndo(simCtrl, "Create ___SimController");
            Debug.Log("[AutoSetup] Created ___SimController.");
        }

        var cm  = GetOrAdd<CrowdManager>(simCtrl);
        var dl  = GetOrAdd<DataLoader>(simCtrl);
        var ssl = GetOrAdd<SessionScheduleLoader>(simCtrl);
        var am  = GetOrAdd<AnalyticsManager>(simCtrl);
        var hud = GetOrAdd<SimulationHUD>(simCtrl);
        var mon = GetOrAdd<CongestionMonitor>(simCtrl);
        GetOrAdd<SimValidator>(simCtrl);
        EditorUtility.SetDirty(simCtrl);

        // ── 2. SimConfig asset ────────────────────────────────────────
        SimConfig cfg = cm.config;

        if (cfg == null)
            cfg = AssetDatabase.LoadAssetAtPath<SimConfig>(SimConfigPath);

        if (cfg == null)
        {
            cfg = ScriptableObject.CreateInstance<SimConfig>();
            AssetDatabase.CreateAsset(cfg, SimConfigPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AutoSetup] Created SimConfig at {SimConfigPath}.");
        }

        if (cm.config == null)
        {
            Undo.RecordObject(cm, "Assign SimConfig");
            cm.config = cfg;
            EditorUtility.SetDirty(cm);
            Debug.Log("[AutoSetup] SimConfig assigned to CrowdManager.");
        }

        // ── 3. ConferenceZones ────────────────────────────────────────
        foreach (var def in Zones)
        {
            GameObject roomGo = GameObject.Find(def.sceneName);
            if (roomGo == null)
            {
                warnings.Add($"'{def.sceneName}' not found — skipped.");
                continue;
            }

            var zone = roomGo.GetComponent<ConferenceZone>();
            if (zone == null)
                zone = Undo.AddComponent<ConferenceZone>(roomGo);

            Undo.RecordObject(zone, "Auto-Setup Zone");
            zone.sensorId    = def.sensorId;
            zone.displayName = def.displayName;
            zone.areaM2      = def.areaM2;
            zone.gizmoBounds = new Vector3(def.boundsX, 3f, def.boundsZ);
            zone.zoneColor   = def.color;

            // Auto-create a GoalPoint child if missing
            if (zone.goalPoint == null)
            {
                var existing = roomGo.transform.Find("GoalPoint");
                if (existing != null)
                {
                    zone.goalPoint = existing;
                }
                else
                {
                    var goalGo = new GameObject("GoalPoint");
                    Undo.RegisterCreatedObjectUndo(goalGo, "Create GoalPoint");
                    goalGo.transform.SetParent(roomGo.transform);
                    goalGo.transform.localPosition = Vector3.zero;
                    zone.goalPoint = goalGo.transform;
                }
            }

            EditorUtility.SetDirty(roomGo);
            zonesConfigured++;
        }

        // ── 4. Wire CrowdManager ──────────────────────────────────────
        Undo.RecordObject(cm, "Auto-Setup CrowdManager");
        cm.dataLoader = dl;

        // Agent prefab — try several common paths
        if (cm.agentPrefab == null)
        {
            string[] prefabPaths = {
                "Assets/Agent.prefab",
                "Assets/Prefabs/Agent.prefab",
                "Assets/Prefabs/AgentPrefab.prefab",
            };
            foreach (var path in prefabPaths)
            {
                var p = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (p != null) { cm.agentPrefab = p; break; }
            }
            if (cm.agentPrefab == null)
                warnings.Add("No agent prefab found — assign CrowdManager.agentPrefab manually.");
        }

        // Spawn points — pick up any GameObjects whose name contains "spawn" or "startpoint"
        var spawnCandidates = FindByNameFragment("spawnpoint", "startpoint", "spawn");
        if (spawnCandidates.Count > 0)
            cm.spawnPoints = spawnCandidates.Select(g => g.transform).ToArray();
        else
            warnings.Add("No spawn point found — assign CrowdManager.spawnPoints manually.");

        // Exit points — pick up any GameObjects whose name contains "exit" or "door"
        var exitCandidates = FindByNameFragment("exitpoint", "exit", "door");
        if (exitCandidates.Count > 0)
            cm.exitPoints = exitCandidates.Select(g => g.transform).ToArray();
        else
            warnings.Add("No exit point found — assign CrowdManager.exitPoints manually.");

        EditorUtility.SetDirty(cm);

        // ── 5. Wire remaining components ──────────────────────────────
        Undo.RecordObject(ssl, "Auto-Setup SSL"); ssl.crowdManager = cm; ssl.dataLoader = dl; EditorUtility.SetDirty(ssl);
        Undo.RecordObject(am,  "Auto-Setup AM");  am.crowdManager  = cm; am.dataLoader  = dl; EditorUtility.SetDirty(am);
        Undo.RecordObject(hud, "Auto-Setup HUD"); hud.crowdManager = cm;                       EditorUtility.SetDirty(hud);
        Undo.RecordObject(mon, "Auto-Setup Mon"); mon.crowdManager = cm;                       EditorUtility.SetDirty(mon);

        // ── 6. Mark scene dirty ────────────────────────────────────────
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        // ── Report ─────────────────────────────────────────────────────
        string report =
            $"Auto-Setup complete.\n\n" +
            $"  Missing scripts removed : {missingRemoved}\n" +
            $"  Zones configured        : {zonesConfigured}\n" +
            $"  SimConfig               : {SimConfigPath}\n" +
            $"  Spawn points            : {cm.spawnPoints?.Length ?? 0}\n" +
            $"  Exit points             : {cm.exitPoints?.Length  ?? 0}\n";

        if (warnings.Count > 0)
            report += "\nWarnings:\n  • " + string.Join("\n  • ", warnings);

        report += "\n\nSave the scene (Ctrl+S) to persist.";

        EditorUtility.DisplayDialog("Conference Sim — Auto-Setup", report, "OK");
        Debug.Log("[AutoSetup] " + report);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c == null)
        {
            c = Undo.AddComponent<T>(go);
            Debug.Log($"[AutoSetup] Added {typeof(T).Name} to '{go.name}'.");
        }
        return c;
    }

    /// <summary>Find all root-level GameObjects whose name (lowercase) contains any of the fragments.</summary>
    private static List<GameObject> FindByNameFragment(params string[] fragments)
    {
        var result = new List<GameObject>();
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            string lower = go.name.ToLower();
            if (fragments.Any(f => lower.Contains(f)))
                result.Add(go);
        }
        return result;
    }
}
