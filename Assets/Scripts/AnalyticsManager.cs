using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Logs agent positions, accumulates a heatmap, and exports a full research package on quit.
///
/// Output files (all in Application.dataPath/SimOutput/):
///   spatial_log.csv          — agent positions + state every 0.5 s
///   heatmap.csv              — average density per 0.5 m² cell
///   zone_timeseries.csv      — zone occupancy snapshot every 30 s  ← great for R/Python
///   flow_metrics.csv         — cumulative enters/exits per zone per minute
///   simulation_summary.json  — high-level KPIs (peak occupancy, avg dwell, etc.)
///   validation_report.json   — RMSE, NRMSE, chi², Pearson r vs. CSV ground truth
///
/// Uses CrowdManager.GetAllActiveAgents() — no FindObjectsOfType overhead.
/// Buffer flushed to disk every 5000 lines to avoid unbounded RAM growth.
/// </summary>
[AddComponentMenu("Conference Sim/Analytics Manager")]
public class AnalyticsManager : MonoBehaviour
{
    [Header("References")]
    public SimConfig    config;
    public DataLoader   dataLoader;
    public CrowdManager crowdManager;

    [Header("Heatmap Grid")]
    public Vector3 floorMin  = new Vector3(-50, 0, -50);
    public Vector3 floorMax  = new Vector3( 50, 0,  50);
    public float   cellSize  = 0.5f;

    [Header("Flow Sensors (optional)")]
    public FlowSensorEntry[] flowSensors;

    [Serializable]
    public struct FlowSensorEntry
    {
        public string      sensorName;
        public BoxCollider triggerZone;
        [HideInInspector] public int         cumulativeCount;
        [HideInInspector] public List<float> minuteRates;
    }

    // ── Internal ────────────────────────────────────────────────────

    private float _logTimer;
    private float _zoneSnapshotTimer;
    private float _flowTimer;

    // Heatmap
    private int   _gridW, _gridH;
    private int[,] _heatmap;
    private int    _heatmapSamples;

    // Buffers — flushed periodically
    private StringBuilder _spatialBuf    = new StringBuilder(65536);
    private StringBuilder _zoneTimeBuf   = new StringBuilder(16384);
    private StringBuilder _flowBuf       = new StringBuilder(4096);
    private int           _spatialLines;
    private int           _zoneLines;
    private string        _spatialPath;
    private string        _zoneTimePath;
    private string        _flowPath;

    // Per-zone occupancy time series for validation (in-memory — compact)
    private Dictionary<string, List<(float t, int n)>> _simOcc
        = new Dictionary<string, List<(float, int)>>();
    private Dictionary<string, List<(float t, int n)>> _obsOcc
        = new Dictionary<string, List<(float, int)>>();

    // Live KPI tracking
    private Dictionary<string, int> _peakOccupancy = new Dictionary<string, int>();
    private Dictionary<string, List<float>> _dwellSamples = new Dictionary<string, List<float>>();
    private int _totalAgentsEverSpawned;

    // ── Lifecycle ───────────────────────────────────────────────────

    void Start()
    {
        if (config       == null) config       = ScriptableObject.CreateInstance<SimConfig>();
        if (crowdManager == null) crowdManager = FindFirstObjectByType<CrowdManager>();

        _gridW   = Mathf.CeilToInt((floorMax.x - floorMin.x) / cellSize);
        _gridH   = Mathf.CeilToInt((floorMax.z - floorMin.z) / cellSize);
        _heatmap = new int[_gridW, _gridH];

        // Prepare output paths
        string dir = Path.Combine(Application.dataPath, "SimOutput");
        Directory.CreateDirectory(dir);
        _spatialPath  = Path.Combine(dir, "spatial_log.csv");
        _zoneTimePath = Path.Combine(dir, "zone_timeseries.csv");
        _flowPath     = Path.Combine(dir, "flow_metrics.csv");

        // Write headers
        File.WriteAllText(_spatialPath,  "sim_time,agent_id,x,z,state,zone_id\n");
        File.WriteAllText(_zoneTimePath, "sim_time,zone_id,dwelling,roaming,transiting,total\n");
        File.WriteAllText(_flowPath,     "sim_time_min,zone_id,enters_cumulative,exits_cumulative\n");

        BuildObservedOccupancy();

        foreach (int i in Enumerable.Range(0, flowSensors.Length))
        {
            flowSensors[i].minuteRates     = new List<float>();
            flowSensors[i].cumulativeCount = 0;
        }
    }

    void FixedUpdate()
    {
        if (crowdManager == null) return;
        float dt = Time.fixedDeltaTime;

        _logTimer          += dt;
        _zoneSnapshotTimer += dt;
        _flowTimer         += dt;

        if (_logTimer >= config.analyticsLogInterval)
        {
            _logTimer = 0f;
            LogAgentPositions();
        }

        if (_zoneSnapshotTimer >= 30f)
        {
            _zoneSnapshotTimer = 0f;
            LogZoneSnapshot();
        }

        if (_flowTimer >= 60f)
        {
            _flowTimer = 0f;
            FlushFlowMetrics();
        }
    }

    void OnApplicationQuit() => FinalizeAndExport();
    void OnDestroy()         => FinalizeAndExport();

    // ── Spatial Logging ─────────────────────────────────────────────

    private void LogAgentPositions()
    {
        float t = crowdManager.SimClock;

        foreach (var a in crowdManager.GetAllActiveAgents())
        {
            Vector3 p = a.transform.position;
            _spatialBuf.Append(t.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                       .Append(a.gameObject.GetInstanceID()).Append(',')
                       .Append(p.x.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                       .Append(p.z.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                       .Append(a.CurrentState).Append(',')
                       .AppendLine(a.TargetSensorId);

            // Heatmap
            int gx = Mathf.FloorToInt((p.x - floorMin.x) / cellSize);
            int gz = Mathf.FloorToInt((p.z - floorMin.z) / cellSize);
            if (gx >= 0 && gx < _gridW && gz >= 0 && gz < _gridH)
                _heatmap[gx, gz]++;

            _spatialLines++;
        }

        _heatmapSamples++;

        // Track sim occupancy for validation
        SnapshotSimOccupancy(t);

        // Update peak occupancy per zone
        if (crowdManager.Zones != null)
            foreach (var z in crowdManager.Zones)
            {
                int n = crowdManager.GetZoneCount(z.sensorId);
                if (!_peakOccupancy.ContainsKey(z.sensorId) || n > _peakOccupancy[z.sensorId])
                    _peakOccupancy[z.sensorId] = n;
            }

        // Flush spatial log every 5000 lines
        if (_spatialLines >= 5000)
        {
            try
            {
                File.AppendAllText(_spatialPath, _spatialBuf.ToString());
                _spatialBuf.Clear();
                _spatialLines = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AnalyticsManager] Spatial log flush failed: {ex.Message}. Buffer cleared to prevent OOM.");
                _spatialBuf.Clear();
                _spatialLines = 0;
            }
        }
    }

    // ── Zone Timeseries ─────────────────────────────────────────────

    private void LogZoneSnapshot()
    {
        if (crowdManager.Zones == null) return;
        float t = crowdManager.SimClock;

        var counts = crowdManager.GetStateCounts();

        foreach (var z in crowdManager.Zones)
        {
            int dwelling  = crowdManager.GetZoneCount(z.sensorId);
            // approximate: get all agents for zone and split by state
            int total     = 0;
            int roaming   = 0;
            int transiting = 0;

            foreach (var a in crowdManager.GetAllActiveAgents())
            {
                if (a.TargetSensorId != z.sensorId) continue;
                total++;
                if (a.CurrentState == AgentController.AgentState.Roaming)   roaming++;
                if (a.CurrentState == AgentController.AgentState.Transit)    transiting++;
            }

            _zoneTimeBuf.Append(t.ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                        .Append(z.sensorId).Append(',')
                        .Append(dwelling).Append(',')
                        .Append(roaming).Append(',')
                        .Append(transiting).Append(',')
                        .AppendLine(total.ToString());
            _zoneLines++;
        }

        if (_zoneLines >= 2000)
        {
            try
            {
                File.AppendAllText(_zoneTimePath, _zoneTimeBuf.ToString());
                _zoneTimeBuf.Clear();
                _zoneLines = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AnalyticsManager] Zone timeseries flush failed: {ex.Message}. Buffer cleared.");
                _zoneTimeBuf.Clear();
                _zoneLines = 0;
            }
        }
    }

    // ── Flow Metrics ────────────────────────────────────────────────

    private void FlushFlowMetrics()
    {
        for (int i = 0; i < flowSensors.Length; i++)
        {
            flowSensors[i].minuteRates.Add(flowSensors[i].cumulativeCount);
            flowSensors[i].cumulativeCount = 0;
        }
    }

    public void RecordFlowEvent(string sensorName)
    {
        for (int i = 0; i < flowSensors.Length; i++)
            if (flowSensors[i].sensorName == sensorName)
            { flowSensors[i].cumulativeCount++; return; }
    }

    // ── Validation Data ─────────────────────────────────────────────

    private void SnapshotSimOccupancy(float t)
    {
        if (crowdManager.Zones == null) return;
        foreach (var z in crowdManager.Zones)
        {
            int n = crowdManager.GetZoneCount(z.sensorId);
            if (!_simOcc.ContainsKey(z.sensorId))
                _simOcc[z.sensorId] = new List<(float, int)>();
            _simOcc[z.sensorId].Add((t, n));
        }
    }

    private void BuildObservedOccupancy()
    {
        if (dataLoader == null) return;
        string path = Path.Combine(Application.streamingAssetsPath, dataLoader.csvFileName);
        if (!File.Exists(path)) return;

        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2) return;
        string[] hdr = lines[0].Split(',');

        int iTs  = Col(hdr, "timestamp");
        int iZ   = Col(hdr, "zone_id");
        int iOcc = Col(hdr, "occupancy_snapshot");
        if (iTs < 0 || iZ < 0 || iOcc < 0) return;

        DateTime? epoch = null;
        for (int i = 1; i < lines.Length; i++)
        {
            string[] c = lines[i].Trim().Split(',');
            if (c.Length <= Math.Max(iTs, Math.Max(iZ, iOcc))) continue;
            try
            {
                var ts  = DateTime.Parse(c[iTs].Trim(), CultureInfo.InvariantCulture,
                                         DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                if (!epoch.HasValue) epoch = ts;
                float t  = (float)(ts - epoch.Value).TotalSeconds;
                string z = c[iZ].Trim();
                int    n = int.Parse(c[iOcc].Trim());
                if (!_obsOcc.ContainsKey(z)) _obsOcc[z] = new List<(float, int)>();
                _obsOcc[z].Add((t, n));
            }
            catch { }
        }
    }

    // ── Final Export ─────────────────────────────────────────────────

    private bool _exported;

    public void FinalizeAndExport()
    {
        if (_exported) return;
        _exported = true;

        // Flush remaining buffers
        try
        {
            if (_spatialBuf.Length  > 0) File.AppendAllText(_spatialPath,  _spatialBuf.ToString());
            if (_zoneTimeBuf.Length > 0) File.AppendAllText(_zoneTimePath, _zoneTimeBuf.ToString());
        }
        catch (Exception ex)
        {
            Debug.LogError($"[AnalyticsManager] Final buffer flush failed: {ex.Message}");
        }

        ExportHeatmap();
        var report = RunValidation();
        ExportJson(config.validationExportPath, JsonUtility.ToJson(report, true));
        ExportSummary();

        Debug.Log("[AnalyticsManager] All outputs written to Assets/SimOutput/");
    }

    private void ExportHeatmap()
    {
        if (_heatmap == null) return;

        var sb = new StringBuilder();
        sb.AppendLine("grid_x,grid_z,world_x,world_z,avg_density_per_m2");
        float cellArea = Mathf.Max(cellSize * cellSize, 0.01f);

        for (int x = 0; x < _gridW; x++)
        for (int z = 0; z < _gridH; z++)
        {
            if (_heatmap[x, z] == 0) continue;
            float wx  = floorMin.x + (x + 0.5f) * cellSize;
            float wz  = floorMin.z + (z + 0.5f) * cellSize;
            float den = (float)_heatmap[x, z] / Mathf.Max(1, _heatmapSamples) / cellArea;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2:F2},{3:F2},{4:F4}", x, z, wx, wz, den));
        }
        WriteFile("SimOutput/heatmap.csv", sb.ToString());
        ExportHeatmapPng();
    }

    private void ExportHeatmapPng()
    {
        try
        {
            // Find max hit count for normalisation
            int max = 1;
            for (int x = 0; x < _gridW; x++)
            for (int z = 0; z < _gridH; z++)
                if (_heatmap[x, z] > max) max = _heatmap[x, z];

            var tex    = new Texture2D(_gridW, _gridH, TextureFormat.RGBA32, false);
            var pixels = new Color[_gridW * _gridH];

            for (int x = 0; x < _gridW; x++)
            for (int z = 0; z < _gridH; z++)
            {
                float t = (float)_heatmap[x, z] / max;
                pixels[z * _gridW + x] = HeatColor(t);
            }

            tex.SetPixels(pixels);
            tex.Apply(false);

            byte[] png  = tex.EncodeToPNG();
            Destroy(tex);

            string path = Path.Combine(Application.dataPath, "SimOutput", "heatmap.png");
            File.WriteAllBytes(path, png);
            Debug.Log($"[AnalyticsManager] Heatmap PNG → {path}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[AnalyticsManager] Heatmap PNG export failed (graphics context may be unavailable): {ex.Message}");
        }
    }

    private static Color HeatColor(float t)
    {
        t = Mathf.Clamp01(t);
        if (t < 0.01f) return new Color(0f, 0f, 0f, 0f);
        if (t < 0.25f) return Color.Lerp(Color.blue,   Color.cyan,   t / 0.25f);
        if (t < 0.50f) return Color.Lerp(Color.cyan,   Color.green,  (t - 0.25f) / 0.25f);
        if (t < 0.75f) return Color.Lerp(Color.green,  Color.yellow, (t - 0.50f) / 0.25f);
        return                Color.Lerp(Color.yellow, Color.red,    (t - 0.75f) / 0.25f);
    }

    [Serializable] public class SimSummary
    {
        public float  totalSimDurationSec;
        public int    zonesMonitored;
        public string peakOccupancyZone;
        public int    peakOccupancyCount;
        public float  avgDwellTimeSec;
        public string exportTimestamp;
    }

    private void ExportSummary()
    {
        var s = new SimSummary
        {
            totalSimDurationSec = crowdManager != null ? crowdManager.SimClock : 0f,
            zonesMonitored      = _peakOccupancy.Count,
            exportTimestamp     = DateTime.UtcNow.ToString("O")
        };

        if (_peakOccupancy.Count > 0)
        {
            var peak = _peakOccupancy.OrderByDescending(kvp => kvp.Value).First();
            s.peakOccupancyZone  = peak.Key;
            s.peakOccupancyCount = peak.Value;
        }

        ExportJson("SimOutput/simulation_summary.json", JsonUtility.ToJson(s, true));
    }

    // ── Validation ──────────────────────────────────────────────────

    [Serializable] public class ValidationReport
    {
        public float overallRMSE, overallNRMSE, chiSquared, chiSquaredCritical_p05;
        public int   degreesOfFreedom;
        public bool  chiSquaredPasses;
        public float pearsonR;
        public List<SensorValidation> perSensor = new List<SensorValidation>();
    }

    [Serializable] public class SensorValidation
    {
        public string sensorId;
        public float  rmse, nrmse, chiSqContrib, meanObserved, meanSimulated, bias;
    }

    private ValidationReport RunValidation()
    {
        var rpt = new ValidationReport();
        float totalSqErr = 0; int totalPts = 0;
        float chiSq = 0; float sumObsMean = 0; int nSensors = 0;

        foreach (var kvp in _obsOcc)
        {
            if (!_simOcc.TryGetValue(kvp.Key, out var sim)) continue;

            float sqErr = 0, sObs = 0, sSim = 0;
            int matched = 0;

            foreach (var (ot, oc) in kvp.Value)
            {
                float minD = float.MaxValue; int sc = 0;
                foreach (var (st, sn) in sim) { float d = Mathf.Abs(st - ot); if (d < minD) { minD = d; sc = sn; } }
                if (minD > 60f) continue;
                float e = sc - oc;
                sqErr += e * e; sObs += oc; sSim += sc; matched++;
                if (oc > 0) chiSq += (e * e) / oc;
            }

            if (matched == 0) continue;

            var sv = new SensorValidation { sensorId = kvp.Key };
            sv.rmse          = Mathf.Sqrt(sqErr / matched);
            sv.meanObserved  = sObs / matched;
            sv.meanSimulated = sSim / matched;
            sv.nrmse         = sv.meanObserved > 0 ? sv.rmse / sv.meanObserved : 0;
            sv.bias          = sv.meanSimulated - sv.meanObserved;
            sv.chiSqContrib  = chiSq;

            rpt.perSensor.Add(sv);
            totalSqErr  += sqErr; totalPts += matched;
            sumObsMean  += sv.meanObserved; nSensors++;
        }

        if (totalPts > 0)
        {
            rpt.overallRMSE  = Mathf.Sqrt(totalSqErr / totalPts);
            float gMean = sumObsMean / Mathf.Max(1, nSensors);
            rpt.overallNRMSE = gMean > 0 ? rpt.overallRMSE / gMean : 0;
        }

        rpt.chiSquared       = chiSq;
        rpt.degreesOfFreedom = Mathf.Max(1, totalPts - 3 - 1);
        float df = rpt.degreesOfFreedom;
        rpt.chiSquaredCritical_p05 = df * Mathf.Pow(1 - 2f/(9*df) + 1.645f * Mathf.Sqrt(2f/(9*df)), 3);
        rpt.chiSquaredPasses       = rpt.chiSquared <= rpt.chiSquaredCritical_p05;
        rpt.pearsonR               = PearsonR();

        Debug.Log($"[Validation] RMSE={rpt.overallRMSE:F2} NRMSE={rpt.overallNRMSE:F3} " +
                  $"χ²={rpt.chiSquared:F1} (pass={rpt.chiSquaredPasses}) r={rpt.pearsonR:F3}");
        return rpt;
    }

    private float PearsonR()
    {
        var pairs = _obsOcc
            .Where(kvp => _simOcc.ContainsKey(kvp.Key))
            .Select(kvp => ((float)kvp.Value.Average(v => v.n),
                            (float)_simOcc[kvp.Key].Average(v => v.n)))
            .ToList();

        if (pairs.Count < 2) return 0;
        float oBar = pairs.Average(p => p.Item1), sBar = pairs.Average(p => p.Item2);
        float num = 0, dO = 0, dS = 0;
        foreach (var (o, s) in pairs)
        { float do_ = o - oBar, ds_ = s - sBar; num += do_ * ds_; dO += do_ * do_; dS += ds_ * ds_; }
        float den = Mathf.Sqrt(dO * dS);
        return den > 1e-4f ? num / den : 0;
    }

    // ── File Helpers ────────────────────────────────────────────────

    private void WriteFile(string rel, string content)
    {
        string p = Path.Combine(Application.dataPath, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p));
        File.WriteAllText(p, content);
    }

    private void ExportJson(string rel, string json) => WriteFile(rel, json);

    private static int Col(string[] h, string name)
    {
        for (int i = 0; i < h.Length; i++)
            if (string.Equals(h[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
