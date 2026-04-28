using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Conference Sim > Assign Section IDs
///
/// Automatically stamps ConferenceZone.sectionId on every zone in the scene by
/// finding the HallSwitcher camera whose position is closest to each zone.
///
/// Requirements:
///   • A HallSwitcher component must exist in the scene with cameras assigned.
///   • Each HallEntry must have a non-empty displayName — that becomes the sectionId.
///
/// Run once after setting up your hall cameras. Re-run any time you add zones or
/// move cameras. The operation is undoable (Ctrl+Z).
/// </summary>
public class SectionIdAssigner : EditorWindow
{
    [MenuItem("Conference Sim/Assign Section IDs")]
    public static void Open() => GetWindow<SectionIdAssigner>("Section IDs");

    private HallSwitcher  _switcher;
    private ConferenceZone[] _zones;
    private Vector2 _scroll;

    private void OnEnable() => Refresh();

    private void Refresh()
    {
        _switcher = FindFirstObjectByType<HallSwitcher>();
        _zones    = FindObjectsByType<ConferenceZone>(FindObjectsSortMode.None);
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Auto-Assign Section IDs", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Each ConferenceZone is stamped with the displayName of the nearest " +
            "HallSwitcher camera. This keeps agents inside their own hall.",
            MessageType.Info);

        EditorGUILayout.Space(4);

        if (GUILayout.Button("Refresh Scene", GUILayout.Height(24)))
            Refresh();

        EditorGUILayout.Space(6);

        // ── Status ──────────────────────────────────────────────────

        bool hasSwitcher = _switcher != null &&
                           _switcher.halls != null &&
                           _switcher.halls.Length > 0;

        if (!hasSwitcher)
        {
            EditorGUILayout.HelpBox(
                "No HallSwitcher found, or its halls array is empty.\n" +
                "Add a HallSwitcher to the scene and assign at least one hall camera.",
                MessageType.Error);
            return;
        }

        bool allCamsAssigned = _switcher.halls.All(h => h.camera != null);
        if (!allCamsAssigned)
        {
            EditorGUILayout.HelpBox(
                "Some HallSwitcher entries have no camera assigned. " +
                "Those halls will be skipped.",
                MessageType.Warning);
        }

        int zoneCount = _zones != null ? _zones.Length : 0;
        EditorGUILayout.LabelField($"Halls found: {_switcher.halls.Length}");
        EditorGUILayout.LabelField($"Zones found: {zoneCount}");

        EditorGUILayout.Space(6);

        // ── Hall list preview ────────────────────────────────────────
        EditorGUILayout.LabelField("Halls (will become section IDs):", EditorStyles.miniBoldLabel);
        _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(120));
        foreach (var h in _switcher.halls)
        {
            string camName = h.camera != null ? h.camera.name : "(no camera)";
            string label   = string.IsNullOrEmpty(h.displayName) ? "(unnamed)" : h.displayName;
            EditorGUILayout.LabelField($"  • \"{label}\"  ←  {camName}",
                                       EditorStyles.miniLabel);
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(8);

        GUI.enabled = zoneCount > 0;
        if (GUILayout.Button("Assign Section IDs Now", GUILayout.Height(32)))
            AssignSections();
        GUI.enabled = true;

        EditorGUILayout.Space(4);

        if (GUILayout.Button("Clear All Section IDs", GUILayout.Height(24)))
            ClearSections();
    }

    private void AssignSections()
    {
        Undo.RecordObjects(_zones, "Assign Zone Section IDs");

        int assigned = 0;
        foreach (var zone in _zones)
        {
            string best = FindNearestHallName(zone.GoalTransform.position);
            if (best != null)
            {
                zone.sectionId = best;
                EditorUtility.SetDirty(zone);
                assigned++;
            }
        }

        Debug.Log($"[SectionIdAssigner] Stamped sectionId on {assigned}/{_zones.Length} zones.");
        EditorGUILayout.Space();
    }

    private void ClearSections()
    {
        Undo.RecordObjects(_zones, "Clear Zone Section IDs");
        foreach (var zone in _zones)
        {
            zone.sectionId = "";
            EditorUtility.SetDirty(zone);
        }
        Debug.Log("[SectionIdAssigner] Cleared all section IDs.");
    }

    private string FindNearestHallName(Vector3 pos)
    {
        float bestDist = float.MaxValue;
        string bestName = null;

        foreach (var h in _switcher.halls)
        {
            if (h.camera == null || string.IsNullOrEmpty(h.displayName)) continue;
            float d = Vector3.Distance(pos, h.camera.transform.position);
            if (d < bestDist) { bestDist = d; bestName = h.displayName; }
        }
        return bestName;
    }
}
