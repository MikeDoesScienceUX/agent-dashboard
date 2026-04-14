using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Manages an ordered queue at a physical bottleneck (registration desk, coffee station,
/// narrow doorway, elevator, etc.).
///
/// How to use:
///   1. Place a QueueNode on a GameObject at the bottleneck location.
///   2. Set queueDirection — the direction agents line up (e.g. -transform.forward).
///   3. Agents within detectionRadius who are transiting TOWARD this node will be
///      intercepted and inserted into the queue.
///   4. The head of the queue advances at serviceRate (agents/second).
///
/// AgentController checks for nearby QueueNodes in TickTransit and calls TryJoin().
/// CrowdManager auto-discovers all QueueNodes at startup (optional — see GetAll()).
/// </summary>
[AddComponentMenu("Conference Sim/Queue Node")]
public class QueueNode : MonoBehaviour
{
    [Header("Queue Identity")]
    public string queueId = "Queue_A";
    public string displayName = "Queue";

    [Header("Physical Layout")]
    [Tooltip("Radius within which transiting agents are intercepted.")]
    public float detectionRadius = 4f;

    [Tooltip("Spacing between agents in the queue (m).")]
    public float agentSpacing = 0.8f;

    [Tooltip("Direction the queue extends from this node. Normalised automatically.")]
    public Vector3 queueDirection = Vector3.back;

    [Tooltip("Maximum queue length before agents divert around.")]
    public int maxQueueLength = 20;

    [Header("Service Rate")]
    [Tooltip("How fast the queue drains — agents released per second.")]
    public float serviceRate = 0.5f;

    // ── Runtime State ────────────────────────────────────────────────

    private readonly List<AgentController> _queue = new List<AgentController>(20);
    private float _serviceTimer;

    // Static registry so AgentController can scan for nearby nodes efficiently
    private static readonly List<QueueNode> _allNodes = new List<QueueNode>();
    public static IReadOnlyList<QueueNode> AllNodes => _allNodes;

    // ── Lifecycle ────────────────────────────────────────────────────

    void OnEnable()  { if (!_allNodes.Contains(this)) _allNodes.Add(this); }
    void OnDisable() { _allNodes.Remove(this); }

    void FixedUpdate()
    {
        if (_queue.Count == 0) return;

        _serviceTimer += Time.fixedDeltaTime;
        float interval = serviceRate > 0f ? 1f / serviceRate : float.MaxValue;

        while (_serviceTimer >= interval && _queue.Count > 0)
        {
            _serviceTimer -= interval;
            ReleaseHead();
        }

        // Update standing positions for all queued agents
        UpdateQueuePositions();
    }

    // ── Queue Management ─────────────────────────────────────────────

    /// <summary>
    /// Attempts to add an agent to this queue.
    /// Returns true if the agent was accepted, false if the queue is full or agent is ineligible.
    /// </summary>
    public bool TryJoin(AgentController agent)
    {
        if (_queue.Contains(agent)) return true;  // already in queue
        if (_queue.Count >= maxQueueLength) return false;

        _queue.Add(agent);

        // Halt the agent's NavMesh and place them at their queue slot
        agent.Nav.isStopped = true;
        agent.Nav.ResetPath();
        MoveAgentToSlot(agent, _queue.Count - 1);

        return true;
    }

    /// <summary>Removes an agent from the queue without releasing them (e.g. if they despawn).</summary>
    public void ForceRemove(AgentController agent) => _queue.Remove(agent);

    public bool IsQueued(AgentController agent) => _queue.Contains(agent);
    public int  QueueLength => _queue.Count;

    // ── Internal ─────────────────────────────────────────────────────

    private void ReleaseHead()
    {
        if (_queue.Count == 0) return;

        AgentController head = _queue[0];
        _queue.RemoveAt(0);

        if (head == null || !head.gameObject.activeSelf) return;

        // Resume agent movement — they continue to their original destination
        head.Nav.isStopped = false;
    }

    private void UpdateQueuePositions()
    {
        Vector3 dir = queueDirection.normalized;
        for (int i = 0; i < _queue.Count; i++)
        {
            if (_queue[i] == null || !_queue[i].gameObject.activeSelf) continue;
            MoveAgentToSlot(_queue[i], i);
        }
    }

    private void MoveAgentToSlot(AgentController agent, int slot)
    {
        Vector3 slotPos = transform.position + queueDirection.normalized * (slot * agentSpacing);
        if (NavMesh.SamplePosition(slotPos, out var hit, 2f, NavMesh.AllAreas))
            agent.Nav.Warp(hit.position);
    }

    // ── Static Helper ────────────────────────────────────────────────

    /// <summary>
    /// Finds the nearest QueueNode to worldPos that is within detectionRadius and
    /// whose queue is not full. Returns null if none qualify.
    /// </summary>
    public static QueueNode FindEligible(Vector3 worldPos)
    {
        QueueNode best  = null;
        float     bestD = float.MaxValue;
        foreach (var node in _allNodes)
        {
            if (node == null) continue;
            float d = Vector3.Distance(worldPos, node.transform.position);
            if (d < node.detectionRadius && node.QueueLength < node.maxQueueLength && d < bestD)
            { bestD = d; best = node; }
        }
        return best;
    }

    // ── Gizmos ───────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
        Gizmos.DrawWireSphere(transform.position, detectionRadius);

        Vector3 dir = queueDirection.normalized;
        Gizmos.color = Color.yellow;
        for (int i = 0; i < Mathf.Min(maxQueueLength, 8); i++)
        {
            Vector3 pos = transform.position + dir * (i * agentSpacing);
            Gizmos.DrawWireSphere(pos, 0.2f);
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Vector3 labelPos = transform.position + Vector3.up * 1.2f;
        UnityEditor.Handles.Label(labelPos,
            $"  {displayName} [{queueId}]\n  {_queue.Count}/{maxQueueLength}  {serviceRate} ag/s",
            new GUIStyle { normal = { textColor = Color.yellow }, fontSize = 10, fontStyle = FontStyle.Bold });
    }
#endif
}
