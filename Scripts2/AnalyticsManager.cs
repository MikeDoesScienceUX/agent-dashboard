using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Phase 5 — AnalyticsManager.cs
///
/// Task 5.1: Silently logs X, Z coordinates of every active agent every 0.5s.
/// Task 5.2: Exports spatial log as CSV and validation report as JSON.
/// Task 5.3: Computes RMSE between simulated booth occupancy and Posterbuddy
///           ground truth to mathematically validate the model.
///
/// Also provides:
///   • Spatial heatmap data (grid-based density accumulator)
///   • Flow rate sensors (invisible trigger collider counts)
///   • Pearson spatial correlation coefficient
///   • Chi-squared goodness of fit per sensor node
/// </summary>
public class AnalyticsManager : MonoBehaviour
{
    [Header("References")]
    public SimConfig config;
    public DataLoader dataLoader;
    public CrowdManager crowdManager;

    [Header("Heatmap Grid")]
    [Tooltip("World-space bounds of the venue floor (min corner).")]
    public Vector3 floorBoundsMin = new Vector3(-50, 0, -50);
    [Tooltip("World-space bounds of the venue floor (max corner).")]
    public Vector3 floorBoundsMax = new Vector3(50, 0, 50);
    [Tooltip("Grid cell size in meters for heatmap accumulation.")]
    public float cellSize = 0.5f;

    [Header("Flow Sensors")]
    [Tooltip("Place empty GameObjects with BoxColliders (IsTrigger) at hallway cross-sections.")]
    public FlowSensor[] flowSensors;

    [System.Serializable]
    public struct FlowSensor
    {
        public string sensorName;
        public BoxCollider triggerZone;
        [HideInInspector] public int cumulativeCount;
        [HideInInspector] public List<float> minuteRates;
    }

    // ── Internal Data Structures ────────────────────────────────────
    private float _logTimer;
    private float _flowRateTimer;
    private int _gridW, _gridH;
    private int[,] _heatmapAccumulator;
    private int _heatmapSamples;

    // Spatial log: each entry = (simTime, agentId, x, z, state)
    private StringBuilder _spatialLogBuffer = new StringBuilder();

    // Per-sensor occupancy time series: sensorId → list of (simTime, occupancy)
    private Dictionary<string, List<(float time, int count)>> _simOccupancy
        = new Dictionary<string, List<(float, int)>>();

    // Per-sensor observed occupancy (from CSV): sensorId → list of (simTime, occupancy)
    private Dictionary<string, List<(float time, int count)>> _obsOccupancy
        = new Dictionary<string, List<(float, int)>>();


    // ── Lifecycle ───────────────────────────────────────────────────
    void Start()
    {
        // Initialize heatmap grid
        _gridW = Mathf.CeilToInt((floorBoundsMax.x - floorBoundsMin.x) / cellSize);
        _gridH = Mathf.CeilToInt((floorBoundsMax.z - floorBoundsMin.z) / cellSize);
        _heatmapAccumulator = new int[_gridW, _gridH];
        _heatmapSamples = 0;

        // Write CSV header
        _spatialLogBuffer.AppendLine("sim_time,agent_id,x,z,state");

        // Parse observed occupancy from DataLoader for validation
        BuildObservedOccupancy();

        // Initialize flow sensor rate lists
        for (int i = 0; i < flowSensors.Length; i++)
        {
            flowSensors[i].minuteRates = new List<float>();
            flowSensors[i].cumulativeCount = 0;
        }

        _logTimer = 0f;
        _flowRateTimer = 0f;
    }

    void FixedUpdate()
    {
        if (config == null) return;

        float dt = Time.fixedDeltaTime;
        _logTimer += dt;
        _flowRateTimer += dt;

        // ── Task 5.1: Log agent positions at configured interval ────
        if (_logTimer >= config.analyticsLogInterval)
        {
            _logTimer = 0f;
            LogAllAgentPositions();
        }

        // ── Flow rate: snapshot every 60 seconds ────────────────────
        if (_flowRateTimer >= 60f)
        {
            _flowRateTimer = 0f;
            SnapshotFlowRates();
        }
    }

    /// <summary>
    /// Called when the simulation ends (or via a UI button).
    /// Exports all collected data and runs validation.
    /// </summary>
    public void FinalizeAndExport()
    {
        ExportSpatialLog();
        ExportHeatmapCSV();

        var validation = RunValidation();
        ExportValidationReport(validation);

        Debug.Log("[AnalyticsManager] All data exported. Validation complete.");
    }

    void OnApplicationQuit()
    {
        FinalizeAndExport();
    }


    // ══════════════════════════════════════════════════════════════════
    // ██  TASK 5.1: SPATIAL LOGGING  ██████████████████████████████████
    // ══════════════════════════════════════════════════════════════════

    private void LogAllAgentPositions()
    {
        float simTime = crowdManager.SimClock;

        // Find all agents in scene
        var agents = FindObjectsOfType<AgentController>();

        foreach (var agent in agents)
        {
            Vector3 pos = agent.transform.position;

            // Append to CSV buffer
            _spatialLogBuffer.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0:F2},{1},{2:F3},{3:F3},{4}",
                simTime, agent.gameObject.GetInstanceID(),
                pos.x, pos.z, agent.CurrentState));

            // Accumulate heatmap
            int gx = Mathf.FloorToInt((pos.x - floorBoundsMin.x) / cellSize);
            int gz = Mathf.FloorToInt((pos.z - floorBoundsMin.z) / cellSize);
            if (gx >= 0 && gx < _gridW && gz >= 0 && gz < _gridH)
            {
                _heatmapAccumulator[gx, gz]++;
            }
        }

        _heatmapSamples++;

        // Also snapshot per-sensor occupancy for validation
        SnapshotSimOccupancy(simTime, agents);
    }

    private void SnapshotSimOccupancy(float simTime, AgentController[] agents)
    {
        // Count dwelling agents per sensor
        var counts = new Dictionary<string, int>();
        foreach (var agent in agents)
        {
            if (agent.CurrentState == AgentController.AgentState.Dwelling)
            {
                string sid = agent.TargetSensorId;
                if (!counts.ContainsKey(sid)) counts[sid] = 0;
                counts[sid]++;
            }
        }

        foreach (var kvp in counts)
        {
            if (!_simOccupancy.ContainsKey(kvp.Key))
                _simOccupancy[kvp.Key] = new List<(float, int)>();
            _simOccupancy[kvp.Key].Add((simTime, kvp.Value));
        }
    }


    // ══════════════════════════════════════════════════════════════════
    // ██  TASK 5.2: DATA EXPORT  ██████████████████████████████████████
    // ══════════════════════════════════════════════════════════════════

    private void ExportSpatialLog()
    {
        string path = Path.Combine(Application.dataPath, config.analyticsExportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, _spatialLogBuffer.ToString());
        Debug.Log($"[AnalyticsManager] Spatial log exported: {path} ({_spatialLogBuffer.Length} chars)");
    }

    private void ExportHeatmapCSV()
    {
        string path = Path.Combine(Application.dataPath, "SimOutput/heatmap.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        var sb = new StringBuilder();
        sb.AppendLine("grid_x,grid_z,world_x,world_z,density");

        for (int x = 0; x < _gridW; x++)
        {
            for (int z = 0; z < _gridH; z++)
            {
                if (_heatmapAccumulator[x, z] == 0) continue;

                float worldX = floorBoundsMin.x + (x + 0.5f) * cellSize;
                float worldZ = floorBoundsMin.z + (z + 0.5f) * cellSize;
                float density = (float)_heatmapAccumulator[x, z] / Mathf.Max(1, _heatmapSamples);

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2:F2},{3:F2},{4:F4}",
                    x, z, worldX, worldZ, density));
            }
        }

        File.WriteAllText(path, sb.ToString());
        Debug.Log($"[AnalyticsManager] Heatmap exported: {path}");
    }


    // ══════════════════════════════════════════════════════════════════
    // ██  TASK 5.3: VALIDATION (The Reality Check)  ███████████████████
    // ══════════════════════════════════════════════════════════════════

    [System.Serializable]
    public class ValidationReport
    {
        public float overallRMSE;
        public float overallNRMSE;
        public float chiSquared;
        public int   degreesOfFreedom;
        public float chiSquaredCritical_p05;
        public bool  chiSquaredPasses;
        public float pearsonR;
        public List<SensorValidation> perSensor = new List<SensorValidation>();
    }

    [System.Serializable]
    public class SensorValidation
    {
        public string sensorId;
        public float rmse;
        public float nrmse;
        public float chiSquaredContribution;
        public float meanObserved;
        public float meanSimulated;
    }

    private void BuildObservedOccupancy()
    {
        // Reconstruct observed time series from DataLoader's raw data
        // We need to re-read the CSV data; DataLoader already parsed it
        // but we need it indexed differently.
        // For now, we'll populate this as CrowdManager processes slices.
        // Alternative: re-parse from DataLoader queued snapshots.
        _obsOccupancy.Clear();

        // Clone the queue to read without consuming
        // (DataLoader's queue is consumed by CrowdManager, so we parse CSV again)
        string fullPath = Path.Combine(Application.streamingAssetsPath,
            dataLoader.csvFileName);
        if (!File.Exists(fullPath)) return;

        string[] lines = File.ReadAllLines(fullPath);
        DateTime? epoch = null;

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            string[] cols = line.Split(',');
            if (cols.Length < 6) continue;

            try
            {
                DateTime ts = DateTime.Parse(cols[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
                if (!epoch.HasValue) epoch = ts;
                float simTime = (float)(ts - epoch.Value).TotalSeconds;
                string sid = cols[1].Trim();
                int occ = int.Parse(cols[2]);

                if (!_obsOccupancy.ContainsKey(sid))
                    _obsOccupancy[sid] = new List<(float, int)>();
                _obsOccupancy[sid].Add((simTime, occ));
            }
            catch { }
        }
    }

    private ValidationReport RunValidation()
    {
        var report = new ValidationReport();

        float totalSqError = 0f;
        int totalPoints = 0;
        float totalObsMean = 0f;
        float chiSq = 0f;
        int sensorCount = 0;

        // ── Per-sensor RMSE & chi-squared ───────────────────────────
        foreach (var kvp in _obsOccupancy)
        {
            string sid = kvp.Key;
            var obsSeries = kvp.Value;

            if (!_simOccupancy.ContainsKey(sid)) continue;
            var simSeries = _simOccupancy[sid];

            var sv = new SensorValidation { sensorId = sid };

            // Match observed timestamps to nearest simulated values
            float sumSqErr = 0f;
            int matched = 0;
            float sumObs = 0f;
            float sumSim = 0f;

            foreach (var (obsTime, obsCount) in obsSeries)
            {
                // Find closest sim snapshot
                int simCount = 0;
                float minTimeDiff = float.MaxValue;

                foreach (var (sTime, sCount) in simSeries)
                {
                    float diff = Mathf.Abs(sTime - obsTime);
                    if (diff < minTimeDiff)
                    {
                        minTimeDiff = diff;
                        simCount = sCount;
                    }
                }

                if (minTimeDiff > 60f) continue; // no sim data near this time

                float err = simCount - obsCount;
                sumSqErr += err * err;
                sumObs += obsCount;
                sumSim += simCount;
                matched++;

                // Chi-squared contribution (avoid div/0)
                if (obsCount > 0)
                    chiSq += (err * err) / (float)obsCount;
            }

            if (matched > 0)
            {
                sv.rmse = Mathf.Sqrt(sumSqErr / matched);
                sv.meanObserved = sumObs / matched;
                sv.meanSimulated = sumSim / matched;
                sv.nrmse = sv.meanObserved > 0 ? sv.rmse / sv.meanObserved : 0f;
                sv.chiSquaredContribution = chiSq;

                totalSqError += sumSqErr;
                totalPoints += matched;
                totalObsMean += sv.meanObserved;
                sensorCount++;
            }

            report.perSensor.Add(sv);
        }

        // ── Overall metrics ─────────────────────────────────────────
        if (totalPoints > 0)
        {
            report.overallRMSE = Mathf.Sqrt(totalSqError / totalPoints);
            float globalMeanObs = totalObsMean / Mathf.Max(1, sensorCount);
            report.overallNRMSE = globalMeanObs > 0
                ? report.overallRMSE / globalMeanObs : 0f;
        }

        report.chiSquared = chiSq;
        // df = (number of sensor-time observations) - (calibrated params) - 1
        // Approximate: calibrated params ≈ 3 (beta, v0, tau)
        report.degreesOfFreedom = Mathf.Max(1, totalPoints - 3 - 1);
        // Chi-squared critical value at p=0.05 (approximate for large df)
        // For df > 30: chi2_crit ≈ df * (1 - 2/(9*df) + z_0.05 * sqrt(2/(9*df)))^3
        // where z_0.05 = 1.645
        float df = report.degreesOfFreedom;
        float approxCrit = df * Mathf.Pow(
            1f - 2f / (9f * df) + 1.645f * Mathf.Sqrt(2f / (9f * df)), 3f);
        report.chiSquaredCritical_p05 = approxCrit;
        report.chiSquaredPasses = report.chiSquared <= approxCrit;

        // ── Pearson correlation on per-sensor mean occupancy ────────
        report.pearsonR = ComputePearsonR();

        Debug.Log($"[Validation] RMSE={report.overallRMSE:F2}, NRMSE={report.overallNRMSE:F3}, " +
                  $"χ²={report.chiSquared:F2} (crit={approxCrit:F2}, pass={report.chiSquaredPasses}), " +
                  $"Pearson r={report.pearsonR:F3}");

        return report;
    }

    /// <summary>
    /// Pearson correlation between observed and simulated mean occupancy per sensor.
    /// r = Σ[(sim - sim_bar)(obs - obs_bar)] / sqrt[Σ(sim - sim_bar)² * Σ(obs - obs_bar)²]
    /// </summary>
    private float ComputePearsonR()
    {
        var pairs = new List<(float obs, float sim)>();

        foreach (var kvp in _obsOccupancy)
        {
            string sid = kvp.Key;
            if (!_simOccupancy.ContainsKey(sid)) continue;

            float obsMean = kvp.Value.Average(v => v.count);
            float simMean = _simOccupancy[sid].Average(v => v.count);
            pairs.Add((obsMean, simMean));
        }

        if (pairs.Count < 2) return 0f;

        float obsBar = pairs.Average(p => p.obs);
        float simBar = pairs.Average(p => p.sim);

        float num = 0, denObs = 0, denSim = 0;
        foreach (var (obs, sim) in pairs)
        {
            float dObs = obs - obsBar;
            float dSim = sim - simBar;
            num += dObs * dSim;
            denObs += dObs * dObs;
            denSim += dSim * dSim;
        }

        float den = Mathf.Sqrt(denObs * denSim);
        return den > 0.0001f ? num / den : 0f;
    }

    private void ExportValidationReport(ValidationReport report)
    {
        string path = Path.Combine(Application.dataPath, config.validationExportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string json = JsonUtility.ToJson(report, true);
        File.WriteAllText(path, json);
        Debug.Log($"[AnalyticsManager] Validation report exported: {path}");
    }


    // ── Flow Rate Sensors ───────────────────────────────────────────

    private void SnapshotFlowRates()
    {
        for (int i = 0; i < flowSensors.Length; i++)
        {
            // Rate = agents passed in last 60s
            // (cumulative is incremented by OnTriggerEnter on a helper component)
            flowSensors[i].minuteRates.Add(flowSensors[i].cumulativeCount);
            flowSensors[i].cumulativeCount = 0;
        }
    }

    /// <summary>
    /// Call this from a FlowSensorTrigger component attached to flow sensor colliders.
    /// </summary>
    public void RecordFlowEvent(string sensorName)
    {
        for (int i = 0; i < flowSensors.Length; i++)
        {
            if (flowSensors[i].sensorName == sensorName)
            {
                flowSensors[i].cumulativeCount++;
                return;
            }
        }
    }
}
