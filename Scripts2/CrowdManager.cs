using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Phase 2, Tasks 2.2 & 2.3 — CrowdManager.cs
/// Reads DataLoader's chronological queue. For each 5-minute time slice:
///   • Spawns `arrivals` agents using Poisson-distributed inter-arrival times
///     (no mass spawning — agents trickle in over the 300-second window).
///   • Handles `departures` by picking dwelling agents at the booth and
///     re-routing them to the nearest exit.
///
/// Phase 4, Tasks 4.1–4.3 — Target assignment, dwell handoff, density checks.
/// </summary>
public class CrowdManager : MonoBehaviour
{
    [Header("References")]
    public SimConfig config;
    public DataLoader dataLoader;
    public GameObject agentPrefab;

    [Header("Spawn / Exit Points")]
    [Tooltip("Transform(s) at venue entrances where agents are instantiated.")]
    public Transform[] spawnPoints;

    [Tooltip("Transform(s) at venue exits where departing agents walk to before being destroyed.")]
    public Transform[] exitPoints;

    [Header("Booth Targets")]
    [Tooltip("Map sensor_id strings to booth goal Transforms. Populate in Inspector or via code.")]
    public BoothMapping[] boothMappings;

    [System.Serializable]
    public struct BoothMapping
    {
        public string sensorId;
        public Transform goalTransform;
        [Tooltip("Approximate floor area of the booth zone in m^2, for density calculation.")]
        public float areaM2;
    }

    // ── Internal State ──────────────────────────────────────────────
    private float _simClock;                     // seconds since sim start
    private float _nextSliceTime = float.MaxValue;
    private DataLoader.TimeSlice _currentSlice;

    // Pending spawn orders: each entry is (simTimeToSpawn, sensorId)
    private List<(float time, string sensorId, float avgDwell)> _spawnQueue
        = new List<(float, string, float)>();

    // All living agents, indexed by their target sensor_id for fast lookup
    private Dictionary<string, List<AgentController>> _agentsBySensor
        = new Dictionary<string, List<AgentController>>();

    // Quick lookup: sensorId → booth data
    private Dictionary<string, BoothMapping> _boothLookup
        = new Dictionary<string, BoothMapping>();


    // ── Lifecycle ───────────────────────────────────────────────────
    void Start()
    {
        // Build booth lookup
        foreach (var bm in boothMappings)
        {
            _boothLookup[bm.sensorId] = bm;
            _agentsBySensor[bm.sensorId] = new List<AgentController>();
        }

        // Prime the first time slice
        if (dataLoader.IsLoaded && dataLoader.TimeSliceQueue.Count > 0)
        {
            _nextSliceTime = dataLoader.TimeSliceQueue.Peek().simTime;
        }

        _simClock = 0f;
    }

    void FixedUpdate()
    {
        // ── Advance simulation clock (decoupled from framerate) ─────
        _simClock += Time.fixedDeltaTime;

        // ── Check if it's time to process the next CSV time slice ────
        while (dataLoader.TimeSliceQueue.Count > 0 &&
               _simClock >= dataLoader.TimeSliceQueue.Peek().simTime)
        {
            ProcessTimeSlice(dataLoader.TimeSliceQueue.Dequeue());
        }

        // ── Trickle-spawn agents from the Poisson queue ─────────────
        ProcessSpawnQueue();
    }


    // ── Phase 2.2: Poisson-Distributed Arrival Spawning ─────────────

    /// <summary>
    /// For each sensor snapshot in the time slice, schedule `arrivals` agents
    /// to spawn at Poisson-distributed intervals over the next 300 seconds.
    /// Handle `departures` immediately by re-routing dwelling agents.
    /// </summary>
    private void ProcessTimeSlice(DataLoader.TimeSlice slice)
    {
        float windowDuration = 300f; // 5-minute window between CSV rows

        foreach (var snap in slice.snapshots)
        {
            // ── ARRIVALS: Schedule Poisson-distributed spawns ────────
            if (snap.arrivals > 0)
            {
                SchedulePoissonArrivals(snap.sensorId, snap.arrivals,
                    snap.avgDwellSec, slice.simTime, windowDuration);
            }

            // ── DEPARTURES: Re-route dwelling agents to exits ───────
            if (snap.departures > 0)
            {
                ScheduleDepartures(snap.sensorId, snap.departures);
            }
        }
    }

    /// <summary>
    /// Generate Poisson-distributed inter-arrival times for `count` agents.
    /// The Poisson process rate λ = count / windowDuration.
    /// Inter-arrival times are exponentially distributed: t = -ln(U) / λ
    /// </summary>
    private void SchedulePoissonArrivals(string sensorId, int count,
        float avgDwell, float windowStart, float windowDuration)
    {
        if (count <= 0) return;

        float lambda = count / windowDuration; // arrival rate (agents/sec)
        float cumTime = windowStart;

        for (int i = 0; i < count; i++)
        {
            // Exponential inter-arrival: t = -ln(U) / λ
            float u = Random.Range(0.0001f, 1f); // avoid ln(0)
            float interArrival = -Mathf.Log(u) / lambda;
            cumTime += interArrival;

            // Clamp within the window so we don't bleed into the next slice
            float spawnTime = Mathf.Min(cumTime, windowStart + windowDuration - 0.1f);

            _spawnQueue.Add((spawnTime, sensorId, avgDwell));
        }

        // Sort queue so earliest spawns come first
        _spawnQueue.Sort((a, b) => a.time.CompareTo(b.time));
    }

    /// <summary>
    /// Pop agents from the front of the spawn queue whenever simClock catches up.
    /// </summary>
    private void ProcessSpawnQueue()
    {
        while (_spawnQueue.Count > 0 && _simClock >= _spawnQueue[0].time)
        {
            var order = _spawnQueue[0];
            _spawnQueue.RemoveAt(0);
            SpawnAgent(order.sensorId, order.avgDwell);
        }
    }

    /// <summary>
    /// Instantiate one agent at a random spawn point, assign its target booth,
    /// and hand it off to AgentController.
    /// </summary>
    private void SpawnAgent(string sensorId, float avgDwellFromCSV)
    {
        if (!_boothLookup.ContainsKey(sensorId))
        {
            Debug.LogWarning($"[CrowdManager] Unknown sensor_id: {sensorId}");
            return;
        }

        // Pick a random entrance
        Transform origin = spawnPoints[Random.Range(0, spawnPoints.Length)];

        // Slight positional jitter so agents don't stack
        Vector3 jitter = new Vector3(Random.Range(-0.5f, 0.5f), 0, Random.Range(-0.5f, 0.5f));
        GameObject go = Instantiate(agentPrefab, origin.position + jitter, Quaternion.identity);
        go.name = $"Agent_{sensorId}_{Time.frameCount}";

        AgentController ac = go.GetComponent<AgentController>();
        if (ac == null)
        {
            Debug.LogError("[CrowdManager] agentPrefab is missing AgentController component.");
            Destroy(go);
            return;
        }

        // ── Phase 4.1: Wire up target assignment ────────────────────
        BoothMapping booth = _boothLookup[sensorId];
        ac.Initialize(config, booth.goalTransform, sensorId, avgDwellFromCSV,
                      exitPoints, this);

        // Register in tracking dictionary
        if (!_agentsBySensor.ContainsKey(sensorId))
            _agentsBySensor[sensorId] = new List<AgentController>();
        _agentsBySensor[sensorId].Add(ac);
    }


    // ── Phase 2.3: Departure Handling ───────────────────────────────

    /// <summary>
    /// Pick `count` agents currently dwelling at `sensorId` and switch them
    /// to Transit-to-Exit so they walk away, generating hallway traffic.
    /// </summary>
    private void ScheduleDepartures(string sensorId, int count)
    {
        if (!_agentsBySensor.ContainsKey(sensorId)) return;

        var candidates = _agentsBySensor[sensorId]
            .Where(a => a != null && a.CurrentState == AgentController.AgentState.Dwelling)
            .ToList();

        // Shuffle to avoid always removing the same agents
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            var tmp = candidates[i];
            candidates[i] = candidates[j];
            candidates[j] = tmp;
        }

        int toRemove = Mathf.Min(count, candidates.Count);
        for (int i = 0; i < toRemove; i++)
        {
            candidates[i].RouteToExit();
        }
    }


    // ── Phase 4.3: Density Check API ────────────────────────────────

    /// <summary>
    /// Returns the current agent density (persons/m^2) at a given booth.
    /// Used by AgentController to decide whether to enter Roaming state.
    /// </summary>
    public float GetBoothDensity(string sensorId)
    {
        if (!_boothLookup.ContainsKey(sensorId) || !_agentsBySensor.ContainsKey(sensorId))
            return 0f;

        BoothMapping booth = _boothLookup[sensorId];
        int dwelling = _agentsBySensor[sensorId]
            .Count(a => a != null && a.CurrentState == AgentController.AgentState.Dwelling);

        float area = Mathf.Max(booth.areaM2, 1f); // avoid div/0
        return dwelling / area;
    }

    /// <summary>
    /// Called by AgentController when an agent is destroyed or exits.
    /// Removes it from the tracking dictionary.
    /// </summary>
    public void UnregisterAgent(AgentController agent)
    {
        if (_agentsBySensor.ContainsKey(agent.TargetSensorId))
        {
            _agentsBySensor[agent.TargetSensorId].Remove(agent);
        }
    }

    /// <summary>Current simulation clock (seconds since start).</summary>
    public float SimClock => _simClock;
}
