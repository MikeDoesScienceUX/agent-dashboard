using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drop this component on any GameObject to mark it as a conference zone
/// (booth, session room, exhibit area, food court, hallway, etc.).
///
/// CrowdManager auto-discovers ALL ConferenceZone objects in the scene at startup.
///
/// New fields:
///   zoneType   — "exhibit" | "food" | "drink" | "session" | "registration" | "rest"
///   topicTags  — content topics (e.g. "NASH", "HCC") matched against persona preferences
/// </summary>
[AddComponentMenu("Conference Sim/Conference Zone")]
public class ConferenceZone : MonoBehaviour
{
    [Header("Zone Identity")]
    [Tooltip("Unique ID matching the zone_id column in your sensor CSV.")]
    public string sensorId = "Zone_A";

    [Tooltip("Human-readable name shown in the HUD and Editor tools.")]
    public string displayName = "Zone A";

    [Tooltip("Approximate walkable floor area in m². Used for density calculation.")]
    public float areaM2 = 20f;


    [Header("Zone Content")]
    [Tooltip("Functional type: exhibit | food | drink | session | registration | rest\n" +
             "Agents with physiological drives seek 'food' and 'drink' zones automatically.")]
    public string zoneType = "exhibit";

    [Tooltip("Topic tags for this zone. Match against PersonaConfig.preferredTopics in SimConfig.\n" +
             "Examples: NASH, HCC, cirrhosis, transplant, viral-hepatitis, metabolic, industry, networking.")]
    public string[] topicTags = new string[0];


    [Header("Goal Point")]
    [Tooltip("Where agents walk to inside this zone. Leave null to use this object's position.")]
    public Transform goalPoint;

    [Header("Spawn Point Override")]
    [Tooltip("Agents spawning for this zone will appear here (e.g. the hall entrance nearest to this zone). " +
             "Leave null to use CrowdManager's global spawnPoints pool.")]
    public Transform spawnPoint;


    [Header("Visualization")]
    [Tooltip("Color used in the Scene gizmo and HUD occupancy bar.")]
    public Color zoneColor = Color.cyan;

    [Tooltip("Wireframe box size in meters shown in Scene view.")]
    public Vector3 gizmoBounds = new Vector3(5f, 0.5f, 5f);


    [Header("Polygon Mode")]
    [Tooltip("Enable to define a custom polygon footprint instead of a rectangle.")]
    public bool usePolygon = false;

    [HideInInspector]
    [Tooltip("XZ vertex offsets from this transform's position. Edit in Scene view.")]
    public List<Vector2> polyPoints = new List<Vector2>();

    /// <summary>The Transform agents navigate toward inside this zone.</summary>
    public Transform GoalTransform => goalPoint != null ? goalPoint : transform;


    // ── Scene Gizmos ────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        // Tint gizmo based on zone type for quick at-a-glance identification
        Color typeColor = ZoneTypeGizmoColor();

        if (usePolygon && polyPoints != null && polyPoints.Count >= 3)
        {
            DrawPolygonGizmo(typeColor);
        }
        else
        {
            Vector3 center = transform.position + Vector3.up * (gizmoBounds.y * 0.5f);
            Color fill = typeColor; fill.a = 0.15f;
            Gizmos.color = fill;
            Gizmos.DrawCube(center, gizmoBounds);
            Color wire = typeColor; wire.a = 0.8f;
            Gizmos.color = wire;
            Gizmos.DrawWireCube(center, gizmoBounds);
        }

        if (goalPoint != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(transform.position, goalPoint.position);
            Gizmos.DrawSphere(goalPoint.position, 0.25f);
        }
    }

    void DrawPolygonGizmo(Color col)
    {
        int n = polyPoints.Count;
        Color wire = col; wire.a = 0.85f;
        Gizmos.color = wire;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            Vector3 a = transform.position + new Vector3(polyPoints[i].x, 0.02f, polyPoints[i].y);
            Vector3 b = transform.position + new Vector3(polyPoints[j].x, 0.02f, polyPoints[j].y);
            Gizmos.DrawLine(a, b);
        }
    }

    private Color ZoneTypeGizmoColor()
    {
        return zoneType switch
        {
            "food"         => new Color(1.0f, 0.6f, 0.0f),  // orange
            "drink"        => new Color(0.0f, 0.6f, 1.0f),  // sky blue
            "session"      => new Color(0.6f, 0.2f, 0.8f),  // purple
            "registration" => new Color(1.0f, 0.9f, 0.0f),  // yellow
            "rest"         => new Color(0.3f, 0.8f, 0.3f),  // green
            _              => zoneColor                       // user color for exhibit
        };
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        string topicStr = topicTags != null && topicTags.Length > 0
            ? string.Join(", ", topicTags)
            : "—";

        Vector3 labelPos = transform.position + Vector3.up * (gizmoBounds.y + 0.6f);
        UnityEditor.Handles.Label(labelPos,
            $"  {displayName}\n  [{sensorId}]  {areaM2} m²\n  type: {zoneType}\n  topics: {topicStr}",
            new GUIStyle
            {
                normal    = { textColor = ZoneTypeGizmoColor() },
                fontSize  = 10,
                fontStyle = FontStyle.Bold
            });
    }
#endif
}
