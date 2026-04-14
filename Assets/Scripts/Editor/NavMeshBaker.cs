using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using NavMeshBuilder = UnityEditor.AI.NavMeshBuilder;

/// <summary>
/// One-click NavMesh baker for the conference venue.
///
/// Menu: Conference Sim → Bake NavMesh  (Ctrl+Alt+B)
///       Conference Sim → Clear NavMesh
///
/// What it does:
///   1. Marks all static mesh geometry as Navigation Static
///      (skips agent capsules, ConferenceZone markers, and ___SimController children)
///   2. Calls NavMeshBuilder.BuildNavMesh() using the scene's current
///      Navigation window settings (Window → AI → Navigation)
///   3. Logs a summary and repaints the Scene view
///
/// If agents still can't pathfind after baking:
///   • Open Window → AI → Navigation → Bake tab
///   • Confirm Agent Radius ≈ 0.25, Agent Height ≈ 1.8
///   • Make sure your floor mesh shows in the blue NavMesh overlay
/// </summary>
public static class NavMeshBaker
{
    // ── Bake ────────────────────────────────────────────────────────

    [MenuItem("Conference Sim/Bake NavMesh %&b")]
    public static void BakeNavMesh()
    {
        int marked = MarkNavigationStatic();

        NavMeshBuilder.BuildNavMesh();

        string msg = marked > 0
            ? $"{marked} mesh object(s) were newly marked as Navigation Static."
            : "All mesh objects were already marked — nothing changed.";

        Debug.Log($"[NavMeshBaker] NavMesh baked. {msg}");

        EditorUtility.DisplayDialog(
            "NavMesh Baked",
            $"NavMesh built successfully.\n\n{msg}\n\n" +
            "If the blue walkable overlay doesn't cover your floor:\n" +
            "  • Select the floor mesh → Inspector → check Navigation Static\n" +
            "  • Confirm agent settings in Window → AI → Navigation → Bake\n" +
            "    (Radius ≈ 0.25 m, Height ≈ 1.8 m)",
            "OK");

        SceneView.RepaintAll();
    }

    // ── Clear ───────────────────────────────────────────────────────

    [MenuItem("Conference Sim/Clear NavMesh")]
    public static void ClearNavMesh()
    {
        NavMeshBuilder.ClearAllNavMeshes();
        Debug.Log("[NavMeshBaker] NavMesh cleared.");
        SceneView.RepaintAll();
    }

    // ── Mark static geometry ────────────────────────────────────────

    /// <summary>
    /// Adds NavigationStatic flag to every MeshRenderer that is likely
    /// architectural geometry — skips agents, zone markers, and the
    /// ___SimController hierarchy.
    /// </summary>
    static int MarkNavigationStatic()
    {
        int count = 0;

#pragma warning disable CS0618 // NavigationStatic is obsolete but still functional with legacy NavMeshBuilder.BuildNavMesh()
        foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            var go = r.gameObject;

            // Skip dynamic / non-geometry objects
            if (IsSimControllerChild(go))              continue;
            if (r.GetComponentInParent<AgentController>() != null) continue;
            if (r.GetComponentInParent<ConferenceZone>()  != null) continue;

            var flags = GameObjectUtility.GetStaticEditorFlags(go);
            if ((flags & StaticEditorFlags.NavigationStatic) != 0) continue;

            Undo.RecordObject(go, "Mark Navigation Static");
            GameObjectUtility.SetStaticEditorFlags(
                go, flags | StaticEditorFlags.NavigationStatic);
            EditorUtility.SetDirty(go);
            count++;
        }
#pragma warning restore CS0618

        return count;
    }

    static bool IsSimControllerChild(GameObject go)
    {
        var t = go.transform;
        while (t != null)
        {
            if (t.name == "___SimController") return true;
            t = t.parent;
        }
        return false;
    }
}
