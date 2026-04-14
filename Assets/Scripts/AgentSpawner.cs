using UnityEngine;

public class AgentSpawner : MonoBehaviour
{
    public Transform startPoint;
    public GameObject agentPrefab;

    [System.Serializable]
    public class GoalQuota
    {
        public Transform goal;
        public int count;
    }

    public GoalQuota[] goals;
    public float spawnRadius = 3f;

    [Tooltip("Agents spawned per second.")]
    public float LoginsPerSecond = 1f;

    [Tooltip("If true, all agents spawn from startPoint and go to a goal. If false, agents spawn at goals (using quotas) and stay there.")]
    public bool StartFromEntrance = true;

    // Superseded by CrowdManager + DataLoader. Kept to avoid missing-script warnings on
    // scene objects that still reference this component. Does nothing at runtime.
    void Start() { }
}
