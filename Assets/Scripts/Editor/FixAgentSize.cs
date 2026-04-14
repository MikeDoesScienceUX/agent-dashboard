using UnityEditor;
using UnityEngine;

public static class FixAgentSize
{
    [MenuItem("Conference Sim/Fix Agent Size")]
    static void Fix()
    {
        const string path = "Assets/Agent.prefab";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
        {
            Debug.LogError($"[FixAgentSize] Could not find prefab at '{path}'.");
            return;
        }

        using (var scope = new PrefabUtility.EditPrefabContentsScope(path))
        {
            var root = scope.prefabContentsRoot;

            // Visual scale: was (0.5, 0.5, 0.5) — increase to (1.0, 1.0, 1.0)
            root.transform.localScale = new Vector3(1.0f, 1.0f, 1.0f);

            // NavMeshAgent radius should match half the visual radius
            var nav = root.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (nav != null)
                nav.radius = 0.5f;
        }

        Debug.Log("[FixAgentSize] Agent.prefab scale set to (1, 1, 1). " +
                  "Re-bake the NavMesh if agents clip walls.");
    }
}
