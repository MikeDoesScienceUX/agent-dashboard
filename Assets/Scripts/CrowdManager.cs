using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Simulation director. Owns the clock, CSV-driven spawning, agent object pool,
/// spatial grid, day-phase tracking, group spawning, and session-schedule redirects.
///
/// New in this version:
///   • Group spawning — agents are spawned in cohesive social groups (Poisson group size).
///   • Day-phase — CurrrentDayPhaseIndex updates every FixedUpdate for AgentController.
///   • Zone metadata API — GetZoneType(), GetZoneTopics(), FindNearestZoneByType().
///   • MoveAgentToZoneImmediate() for smooth session redirects.
///
/// Execution order -10 ensures the spatial grid is rebuilt before agents tick.
/// </summary>
[DefaultExecutionOrder(-10)]
[AddComponentMenu("Conference Sim/Crowd Manager")]
public class CrowdManager : MonoBehaviour
{
    [Header("References")]
    [Tooltip("SimConfig ScriptableObject. Leave null to use built-in defaults.")]
    public SimConfig config;
    public DataLoader dataLoader;
    public GameObject agentPrefab;
    [Tooltip("Optional. When assigned, zone gravity weights are nudged by per-run calibration offsets.")]
    public CalibrationManager calibrationManager;

    [Header("Spawn & Exit Points")]
    public Transform[] spawnPoints;
    public Transform[] exitPoints;

    [Header("Simulation Speed")]
    [Tooltip("1 = real time. 60 = one minute of simulation per real second.")]
    [Range(0.1f, 120f)]
    public float timeScaleMultiplier = 1f;

    [Header("Scenario Planning")]
    [Tooltip("Multiplies ALL CSV spawn counts. 1.0 = as recorded.")]
    [Range(0.1f, 5f)]
    public float attendeeScaleMultiplier = 1f;

    [Header("Object Pool")]
    [Tooltip("Pre-warm this many agents into the pool at Start.")]
    public int poolWarmupSize = 300;

    // ── Public State ─────────────────────────────────────────────────

    public float            SimClock  { get; private set; }
    public ConferenceZone[] Zones     { get; private set; }
    public SpatialGrid      Grid      { get; private set; }

    /// <summary>Index into SimConfig.dayPhases for the current hour. -1 if no phase matches.</summary>
    public int CurrentDayPhaseIndex { get; private set; } = -1;

    // ── Internal Data ─────────────────────────────────────────────────

    private Dictionary<string, List<AgentController>> _agentsByZone
        = new Dictionary<string, List<AgentController>>();

    private Dictionary<string, ConferenceZone> _zoneLookup
        = new Dictionary<string, ConferenceZone>();

    // Spawn queue: sorted by spawn time
    private List<(float time, string zoneId)> _spawnQueue = new List<(float, string)>(256);
    private int _spawnIdx;

    // Object pool
    private Queue<GameObject> _pool = new Queue<GameObject>();

    // Social groups
    private List<AgentGroup> _groups    = new List<AgentGroup>(64);
    private int              _nextGroupId;

    // Cached persona spawn cumulative weights (computed once)
    private float[] _personaCumWeights;

    // Epoch hour (hour-of-day at simulation t=0)
    private float _epochHour = 9.0f;  // default: 9 AM

    // Cached stats for HUD
    private Dictionary<AgentController.AgentState, int> _cachedStats
        = new Dictionary<AgentController.AgentState, int>();
    private Dictionary<int, int> _cachedPersonaCounts = new Dictionary<int, int>();
    private float _statsTimer;

    // ── Lifecycle ─────────────────────────────────────────────────────

    void Start()
    {
        if (config == null)
        {
            config = ScriptableObject.CreateInstance<SimConfig>();
            Debug.LogWarning("[CrowdManager] No SimConfig assigned — using defaults.");
        }

        if (agentPrefab == null)
        {
            Debug.LogError("[CrowdManager] agentPrefab not assigned.");
            Grid  = new SpatialGrid(5f);
            Zones = FindObjectsByType<ConferenceZone>(FindObjectsSortMode.None);
            InitStatCache();
            return;
        }

        if (dataLoader == null)
            Debug.LogWarning("[CrowdManager] dataLoader not assigned — CSV-driven spawning disabled.");
        if (calibrationManager == null)
            calibrationManager = FindFirstObjectByType<CalibrationManager>();

        Grid = new SpatialGrid(5f);

        // Auto-discover zones
        Zones = FindObjectsByType<ConferenceZone>(FindObjectsSortMode.None);
        if (Zones.Length == 0)
            Debug.LogWarning("[CrowdManager] No ConferenceZone components found.");
        foreach (var z in Zones)
        {
            _zoneLookup[z.sensorId]   = z;
            _agentsByZone[z.sensorId] = new List<AgentController>(32);
        }
        Debug.Log($"[CrowdManager] {Zones.Length} zone(s): " +
                  string.Join(", ", Zones.Select(z => z.sensorId)));

        // Warn only if there are truly no spawn points available at all
        bool anyZoneSpawn = System.Array.Exists(Zones, z => z.spawnPoint != null);
        if ((spawnPoints == null || spawnPoints.Length == 0) && !anyZoneSpawn)
            Debug.LogWarning("[CrowdManager] No spawnPoints on CrowdManager and no zone-level spawnPoint overrides found — agents will not spawn.");

        // Compute epoch hour from DataLoader
        if (dataLoader != null && dataLoader.IsLoaded)
            _epochHour = dataLoader.EpochTime.Hour
                       + dataLoader.EpochTime.Minute / 60f;

        // Pre-compute persona cumulative weights for O(1) sampling
        BuildPersonaWeights();

        // Pre-warm pool
        for (int i = 0; i < poolWarmupSize; i++)
            _pool.Enqueue(CreateNewAgent());

        InitStatCache();
    }

    void FixedUpdate()
    {
        SimClock += Time.fixedDeltaTime * timeScaleMultiplier;

        // Update day-phase index
        UpdateDayPhase();

        // Process CSV time slices
        if (dataLoader != null && dataLoader.IsLoaded && dataLoader.TimeSliceQueue != null)
            while (dataLoader.TimeSliceQueue.Count > 0 &&
                   SimClock >= dataLoader.TimeSliceQueue.Peek().simTime)
                ProcessTimeSlice(dataLoader.TimeSliceQueue.Dequeue());

        // Trickle-spawn queued agents
        ProcessSpawnQueue();

        // Rebuild spatial grid
        RebuildGrid();

        // Refresh cached stats
        _statsTimer += Time.fixedDeltaTime;
        if (_statsTimer >= 0.5f) { _statsTimer = 0f; RebuildStats(); }
    }

    // ── Day-Phase ─────────────────────────────────────────────────────

    private void UpdateDayPhase()
    {
        if (config.dayPhases == null || config.dayPhases.Length == 0) { CurrentDayPhaseIndex = -1; return; }

        float hourOfDay = (_epochHour + SimClock / 3600f) % 24f;
        for (int i = 0; i < config.dayPhases.Length; i++)
        {
            var p = config.dayPhases[i];
            if (hourOfDay >= p.startHour && hourOfDay < p.endHour)
            { CurrentDayPhaseIndex = i; return; }
        }
        CurrentDayPhaseIndex = -1;
    }

    // ── Poisson Spawning ─────────────────────────────────────────────

    private void ProcessTimeSlice(DataLoader.TimeSlice slice)
    {
        const float window = 900f;
        foreach (var snap in slice.snapshots)
        {
            if (snap.enters > 0)
            {
                int scaled = Mathf.Max(1, Mathf.RoundToInt(snap.enters * attendeeScaleMultiplier));
                SchedulePoisson(snap.zoneId, scaled, slice.simTime, window);
            }
            if (snap.exits > 0)
                ScheduleDepartures(snap.zoneId, snap.exits);
        }
    }

    private void SchedulePoisson(string zoneId, int count, float t0, float window)
    {
        float lambda = count / window;
        float t      = t0;
        for (int i = 0; i < count; i++)
        {
            t += -Mathf.Log(Random.Range(0.0001f, 1f)) / lambda;
            InsertSorted(Mathf.Min(t, t0 + window - 0.1f), zoneId);
        }
    }

    private void InsertSorted(float time, string zoneId)
    {
        if (_spawnIdx > 128) { _spawnQueue.RemoveRange(0, _spawnIdx); _spawnIdx = 0; }
        int lo = _spawnIdx, hi = _spawnQueue.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_spawnQueue[mid].time <= time) lo = mid + 1; else hi = mid;
        }
        _spawnQueue.Insert(lo, (time, zoneId));
    }

    private void ProcessSpawnQueue()
    {
        while (_spawnIdx < _spawnQueue.Count && SimClock >= _spawnQueue[_spawnIdx].time)
        {
            SpawnAgentOrGroup(_spawnQueue[_spawnIdx].zoneId);
            _spawnIdx++;
        }
    }

    // ── Group-Aware Spawning ──────────────────────────────────────────

    /// <summary>
    /// Spawns a single agent or a group (Poisson size). When spawning a group,
    /// all members share an AgentGroup reference for cohesion and social contagion.
    /// Each spawn call from the queue is a single "arrival event" that may represent
    /// a group arriving together.
    /// </summary>
    private void SpawnAgentOrGroup(string zoneId)
    {
        // Sample group size from Poisson(λ). Most arrivals are solo; some are groups.
        int groupSize = SamplePoisson(config.groupSizeLambda);
        groupSize     = Mathf.Clamp(groupSize, 1, config.groupSizeMax);

        AgentGroup group = null;
        if (groupSize > 1)
        {
            group = new AgentGroup(_nextGroupId++);
            _groups.Add(group);
        }

        // Use the zone's dedicated spawn point if set, otherwise fall back to the global pool
        Transform origin = transform;
        if (_zoneLookup.TryGetValue(zoneId, out var spawnZone) && spawnZone.spawnPoint != null)
            origin = spawnZone.spawnPoint;
        else if (spawnPoints != null && spawnPoints.Length > 0)
            origin = spawnPoints[Random.Range(0, spawnPoints.Length)];

        for (int i = 0; i < groupSize; i++)
        {
            // Skip extra members from the queue budget; they spawn as free riders
            if (i > 0 && _spawnIdx + i >= _spawnQueue.Count) break;
            SpawnAgent(zoneId, origin, group);
        }
    }

    private void SpawnAgent(string zoneId, Transform origin, AgentGroup group = null)
    {
        if (!_zoneLookup.TryGetValue(zoneId, out var zone))
        {
            Debug.LogWarning($"[CrowdManager] No ConferenceZone for '{zoneId}'.");
            return;
        }
        bool hasSpawn = (zone.spawnPoint != null) ||
                        (spawnPoints != null && spawnPoints.Length > 0);
        if (!hasSpawn) return;

        Vector3 pos = origin.position + new Vector3(Random.Range(-0.8f, 0.8f), 0, Random.Range(-0.8f, 0.8f));
        if (!NavMesh.SamplePosition(pos, out var hit, 3f, NavMesh.AllAreas))
        {
            Debug.LogWarning($"[CrowdManager] Spawn skipped for '{zoneId}': no NavMesh within 3 m of {pos}");
            return;
        }

        GameObject go = GetFromPool();
        go.transform.position = hit.position;
        go.transform.rotation = Quaternion.identity;

        // Enable NavMeshAgent and Warp to the valid position BEFORE SetActive(true).
        // This prevents "not close enough to NavMesh" errors that fire when OnEnable
        // runs during SetActive if the agent is still at a stale/invalid position.
        var nav = go.GetComponent<UnityEngine.AI.NavMeshAgent>();
        if (nav != null)
        {
            nav.enabled = true;
            nav.Warp(hit.position);
        }

        go.SetActive(true);

        int personaIdx = SamplePersona();

        var ac = go.GetComponent<AgentController>();
        ac.Initialize(config, zone.GoalTransform, zoneId, 0f, exitPoints, this, personaIdx, group);

        if (group != null) group.Add(ac);

        if (!_agentsByZone.ContainsKey(zoneId)) _agentsByZone[zoneId] = new List<AgentController>();
        _agentsByZone[zoneId].Add(ac);
    }

    // ── Persona Sampling ─────────────────────────────────────────────

    private void BuildPersonaWeights()
    {
        if (config.personas == null || config.personas.Length == 0)
        { _personaCumWeights = new float[] { 1f }; return; }

        _personaCumWeights = new float[config.personas.Length];
        float total = config.personas.Sum(p => Mathf.Max(p.spawnWeight, 0f));
        float acc   = 0f;
        for (int i = 0; i < config.personas.Length; i++)
        {
            acc += Mathf.Max(config.personas[i].spawnWeight, 0f) / Mathf.Max(total, 0.001f);
            _personaCumWeights[i] = acc;
        }
    }

    private int SamplePersona()
    {
        if (_personaCumWeights == null || _personaCumWeights.Length == 0) return 0;
        float r = Random.value;
        for (int i = 0; i < _personaCumWeights.Length; i++)
            if (r <= _personaCumWeights[i]) return i;
        return _personaCumWeights.Length - 1;
    }

    // ── Object Pool ──────────────────────────────────────────────────

    private GameObject CreateNewAgent()
    {
        // Instantiate with the prefab temporarily inactive so NavMeshAgent's OnEnable
        // never fires at the prefab's saved position (origin). Awake still runs; OnEnable does not.
        bool wasActive = agentPrefab.activeSelf;
        agentPrefab.SetActive(false);
        var go = Instantiate(agentPrefab);
        agentPrefab.SetActive(wasActive);
        return go;  // inactive, NavMeshAgent has not registered yet
    }

    // Returns an inactive GameObject — SpawnAgent is responsible for positioning,
    // enabling NavMeshAgent, Warping, then calling SetActive(true).
    private GameObject GetFromPool()
    {
        while (_pool.Count > 0)
        {
            var go = _pool.Dequeue();
            if (go != null) return go;
        }
        return CreateNewAgent();
    }

    public void ReturnToPool(GameObject go)
    {
        if (go == null) return;
        var ac = go.GetComponent<AgentController>();
        if (ac != null) { UnregisterAgent(ac); ac.ResetForPool(); }
        // Disable NavMeshAgent before deactivating so it unregisters cleanly
        // and won't re-register at a stale position on the next SetActive(true).
        var nav = go.GetComponent<UnityEngine.AI.NavMeshAgent>();
        if (nav != null) nav.enabled = false;
        go.SetActive(false);
        _pool.Enqueue(go);

        // Prune dissolved groups (all members gone) to prevent unbounded list growth
        _groups.RemoveAll(g => g.Members.Count == 0);
    }

    // ── Departures ───────────────────────────────────────────────────

    private void ScheduleDepartures(string zoneId, int count)
    {
        if (!_agentsByZone.TryGetValue(zoneId, out var list)) return;
        var candidates = list.Where(a => a != null &&
            a.CurrentState == AgentController.AgentState.Dwelling).ToList();
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        for (int i = 0; i < Mathf.Min(count, candidates.Count); i++)
            candidates[i].RouteToExit();
    }

    // ── Session Schedule API ─────────────────────────────────────────

    /// <summary>
    /// Redirect up to <paramref name="count"/> freely-moving agents to a zone.
    /// Uses smooth redirect if config.smoothSessionRedirects is true.
    /// </summary>
    public void RedirectAgentsToZone(string targetZoneId, int count)
    {
        if (!_zoneLookup.TryGetValue(targetZoneId, out var targetZone)) return;

        var candidates = new List<AgentController>();
        foreach (var kvp in _agentsByZone)
        {
            if (kvp.Key == targetZoneId) continue;
            foreach (var a in kvp.Value)
                if (a != null && (a.CurrentState == AgentController.AgentState.Roaming  ||
                                  a.CurrentState == AgentController.AgentState.Dwelling  ||
                                  a.CurrentState == AgentController.AgentState.Transit))
                    candidates.Add(a);
        }

        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        int n = Mathf.Min(count, candidates.Count);
        for (int i = 0; i < n; i++)
        {
            AgentController ac = candidates[i];

            if (_agentsByZone.TryGetValue(ac.TargetSensorId, out var oldList))
                oldList.Remove(ac);

            if (!_agentsByZone.ContainsKey(targetZoneId))
                _agentsByZone[targetZoneId] = new List<AgentController>();
            _agentsByZone[targetZoneId].Add(ac);

            if (config.smoothSessionRedirects)
                ac.QueueRedirect(targetZone.GoalTransform, targetZoneId);
            else
                ac.ReassignTarget(targetZone.GoalTransform, targetZoneId);
        }
    }

    /// <summary>Release agents from a session room back to free Transit.</summary>
    public void ReleaseZoneAgents(string zoneId)
    {
        if (!_agentsByZone.TryGetValue(zoneId, out var list)) return;
        foreach (var a in list)
        {
            if (a == null) continue;
            var s = a.CurrentState;
            if (s == AgentController.AgentState.Dwelling   ||
                s == AgentController.AgentState.Roaming    ||
                s == AgentController.AgentState.Socializing ||
                s == AgentController.AgentState.Resting)
                a.RouteToExit();
        }
    }

    /// <summary>Transfers a single agent to a new zone (gravity-driven drift).</summary>
    public void MoveAgentToZone(AgentController ac, string toZoneId)
    {
        if (!_zoneLookup.TryGetValue(toZoneId, out var targetZone)) return;

        if (_agentsByZone.TryGetValue(ac.TargetSensorId, out var oldList))
            oldList.Remove(ac);

        ac.ReassignTarget(targetZone.GoalTransform, toZoneId);

        if (!_agentsByZone.ContainsKey(toZoneId))
            _agentsByZone[toZoneId] = new List<AgentController>();
        _agentsByZone[toZoneId].Add(ac);
    }

    /// <summary>
    /// Transfers an agent to a new zone with an immediately-usable goal transform.
    /// Called by AgentController.ApplyPendingRedirect() for smooth session redirects.
    /// </summary>
    public void MoveAgentToZoneImmediate(AgentController ac, Transform goal, string toZoneId)
    {
        if (_agentsByZone.TryGetValue(ac.TargetSensorId, out var oldList))
            oldList.Remove(ac);

        if (!_agentsByZone.ContainsKey(toZoneId))
            _agentsByZone[toZoneId] = new List<AgentController>();
        _agentsByZone[toZoneId].Add(ac);

        ac.ReassignTarget(goal, toZoneId);
    }

    // ── Zone Metadata API (used by AgentController) ──────────────────

    /// <summary>
    /// Returns the calibration gravity-bias offset for a zone (0 if no calibration data).
    /// Applied as an exponential weight multiplier in AgentController.PickDestinationZone().
    /// </summary>
    public float GetZoneGravityBias(string sensorId)
        => calibrationManager != null ? calibrationManager.GetZoneBias(sensorId) : 0f;

    /// <summary>Returns true if a ConferenceZone with this sensorId exists.</summary>
    public bool ZoneExists(string sensorId) => _zoneLookup.ContainsKey(sensorId);

    /// <summary>Returns the zoneType string of a zone (e.g. "food", "drink", "exhibit").</summary>
    public string GetZoneType(string sensorId)
    {
        if (_zoneLookup.TryGetValue(sensorId, out var z)) return z.zoneType;
        return string.Empty;
    }

    /// <summary>Returns the sectionId of a zone (e.g. "Hall7"). Empty string = unconstrained.</summary>
    public string GetZoneSection(string sensorId)
    {
        if (_zoneLookup.TryGetValue(sensorId, out var z)) return z.sectionId;
        return string.Empty;
    }

    /// <summary>Returns the topic tags of a zone (e.g. ["NASH", "HCC"]).</summary>
    public string[] GetZoneTopics(string sensorId)
    {
        if (_zoneLookup.TryGetValue(sensorId, out var z)) return z.topicTags;
        return null;
    }

    /// <summary>
    /// Finds the nearest zone of a given type (e.g. "food") to a world position.
    /// Returns the sensorId or null if none found.
    /// </summary>
    public string FindNearestZoneByType(Vector3 worldPos, string zoneType)
    {
        float bestDist = float.MaxValue;
        string bestId  = null;
        foreach (var z in Zones)
        {
            if (!string.Equals(z.zoneType, zoneType, System.StringComparison.OrdinalIgnoreCase)) continue;
            float d = Vector3.Distance(worldPos, z.GoalTransform.position);
            if (d < bestDist) { bestDist = d; bestId = z.sensorId; }
        }
        return bestId;
    }

    // ── Spatial Grid ─────────────────────────────────────────────────

    private void RebuildGrid()
    {
        if (Grid == null) return;
        Grid.Clear();
        foreach (var list in _agentsByZone.Values)
            foreach (var a in list)
                if (a != null && a.gameObject.activeSelf)
                    Grid.Insert(a);
    }

    // ── Stats API ────────────────────────────────────────────────────

    private void InitStatCache()
    {
        foreach (AgentController.AgentState s in System.Enum.GetValues(typeof(AgentController.AgentState)))
            _cachedStats[s] = 0;
    }

    private void RebuildStats()
    {
        foreach (AgentController.AgentState s in System.Enum.GetValues(typeof(AgentController.AgentState)))
            _cachedStats[s] = 0;
        _cachedPersonaCounts.Clear();

        foreach (var list in _agentsByZone.Values)
            foreach (var a in list)
                if (a != null && a.gameObject.activeSelf)
                {
                    _cachedStats[a.CurrentState]++;
                    int pi = a.PersonaIndex;
                    if (!_cachedPersonaCounts.ContainsKey(pi)) _cachedPersonaCounts[pi] = 0;
                    _cachedPersonaCounts[pi]++;
                }
    }

    public IReadOnlyDictionary<AgentController.AgentState, int> GetStateCounts()    => _cachedStats;
    public IReadOnlyDictionary<int, int>                        GetPersonaCounts()   => _cachedPersonaCounts;

    public int GetZoneCount(string zoneId)
    {
        if (!_agentsByZone.TryGetValue(zoneId, out var list)) return 0;
        return list.Count(a => a != null && a.CurrentState == AgentController.AgentState.Dwelling);
    }

    public float GetBoothDensity(string zoneId)
    {
        if (!_zoneLookup.TryGetValue(zoneId, out var zone)) return 0f;
        return GetZoneCount(zoneId) / Mathf.Max(zone.areaM2, 1f);
    }

    public IEnumerable<AgentController> GetAllActiveAgents()
    {
        foreach (var list in _agentsByZone.Values)
            foreach (var a in list)
                if (a != null && a.gameObject.activeSelf)
                    yield return a;
    }

    public void UnregisterAgent(AgentController agent)
    {
        if (agent == null) return;
        if (_agentsByZone.TryGetValue(agent.TargetSensorId, out var list))
            list.Remove(agent);
    }

    // ── Utilities ────────────────────────────────────────────────────

    /// <summary>Samples a Poisson-distributed integer using Knuth's algorithm.</summary>
    private static int SamplePoisson(float lambda)
    {
        float L = Mathf.Exp(-lambda);
        int k = 0;
        float p = 1f;
        do { k++; p *= Random.value; } while (p > L);
        return k - 1;
    }
}
