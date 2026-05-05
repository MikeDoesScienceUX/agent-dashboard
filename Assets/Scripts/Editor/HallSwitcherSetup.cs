using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Conference Sim > Auto-Setup Hall Switcher
///
/// One click to:
///   1. Find every camera in the scene (sorted by name).
///   2. Add a HallSwitcher to ___SimController (or create a host object).
///   3. Populate its halls array — camera GameObject name becomes the displayName.
///   4. Stamp ConferenceZone.sectionId on all 48 zones by nearest hall camera.
///   5. Enable only the first camera so the scene starts in a known state.
///
/// Re-running is safe — overwrites the existing HallSwitcher array and re-stamps zones.
/// The whole operation is undoable with Ctrl+Z.
/// </summary>
public static class HallSwitcherSetup
{
    [MenuItem("Conference Sim/Auto-Setup Hall Switcher")]
    public static void Run()
    {
        // ── 1. Collect hall cameras ──────────────────────────────────────
        var hallCams = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)
            .OrderBy(c => c.gameObject.name)
            .ToArray();

        if (hallCams.Length == 0)
        {
            EditorUtility.DisplayDialog("Hall Switcher Setup",
                "No cameras found in the scene.",
                "OK");
            return;
        }

        // ── 2. Find or create HallSwitcher ───────────────────────────────
        var switcher = Object.FindFirstObjectByType<HallSwitcher>();
        if (switcher == null)
        {
            var host = GameObject.Find("___SimController");
            if (host == null)
            {
                host = new GameObject("HallSwitcherHost");
                Undo.RegisterCreatedObjectUndo(host, "Create HallSwitcherHost");
            }
            switcher = Undo.AddComponent<HallSwitcher>(host);
        }

        // ── 3. Populate halls array ──────────────────────────────────────
        Undo.RecordObject(switcher, "Setup Hall Switcher");
        switcher.halls = hallCams
            .Select(c => new HallSwitcher.HallEntry
            {
                displayName = c.gameObject.name.Trim(),
                camera      = c,
            })
            .ToArray();
        EditorUtility.SetDirty(switcher);

        // ── 4. Stamp section IDs on all zones ────────────────────────────
        var zones = Object.FindObjectsByType<ConferenceZone>(FindObjectsSortMode.None);
        int assigned = 0;

        if (zones.Length > 0)
        {
            Undo.RecordObjects(zones, "Assign Zone Section IDs");
            foreach (var zone in zones)
            {
                string best = NearestHallName(switcher, zone.GoalTransform.position);
                if (best == null) continue;
                zone.sectionId = best;
                EditorUtility.SetDirty(zone);
                assigned++;
            }
        }

        // ── 5. Enable only the first camera ──────────────────────────────
        for (int i = 0; i < hallCams.Length; i++)
            hallCams[i].enabled = (i == 0);

        Debug.Log($"[HallSwitcherSetup] {hallCams.Length} halls wired. " +
                  $"sectionId stamped on {assigned}/{zones.Length} zones. " +
                  $"Active camera: {hallCams[0].gameObject.name}");

        EditorUtility.DisplayDialog("Hall Switcher Setup — Done",
            $"{hallCams.Length} halls detected and wired:\n" +
            string.Join("\n", hallCams.Select((c, i) => $"  [{i + 1}]  {c.gameObject.name}")) +
            $"\n\n{assigned}/{zones.Length} zones stamped with section IDs.\n\n" +
            "Tip: rename the DisplayName fields in the HallSwitcher Inspector " +
            "if you want friendlier labels (e.g. 'Hall 7'). Then re-run " +
            "Conference Sim > Assign Section IDs to re-stamp zones.",
            "OK");
    }

    private static string NearestHallName(HallSwitcher sw, Vector3 pos)
    {
        float bestDist = float.MaxValue;
        string bestName = null;
        foreach (var h in sw.halls)
        {
            if (h.camera == null || string.IsNullOrEmpty(h.displayName)) continue;
            float d = Vector3.Distance(pos, h.camera.transform.position);
            if (d < bestDist) { bestDist = d; bestName = h.displayName; }
        }
        return bestName;
    }
}
