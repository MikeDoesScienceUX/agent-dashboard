using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Post-run adaptive calibration. After every simulation run, reads the validation
/// report produced by AnalyticsManager and adjusts SimConfig.gravityBeta0 and
/// per-zone gravity bias offsets using proportional control (P-controller).
///
/// The controller drives the per-sensor mean bias toward zero:
///   adjustment = -learningRate × bias / meanObserved
///
/// Results are written to SimOutput/calibration_log.json so the calibration history
/// can be reviewed and the changes can be rolled back if needed.
///
/// Attach to: the same GameObject as CrowdManager and AnalyticsManager.
/// The calibration runs automatically on application quit if config.calibrationEnabled is true.
/// </summary>
[AddComponentMenu("Conference Sim/Calibration Manager")]
public class CalibrationManager : MonoBehaviour
{
    [Header("References")]
    public SimConfig      config;
    public CrowdManager   crowdManager;
    public AnalyticsManager analyticsManager;

    // Per-zone gravity bias offsets (sensorId → additive bias on log-weight)
    // These are applied at runtime by CrowdManager.GetZoneGravityBias().
    private readonly Dictionary<string, float> _zoneBias = new Dictionary<string, float>();

    private bool _calibrationDone;

    // ── Lifecycle ────────────────────────────────────────────────────

    void Start()
    {
        if (config           == null) config           = ScriptableObject.CreateInstance<SimConfig>();
        if (crowdManager     == null) crowdManager     = FindFirstObjectByType<CrowdManager>();
        if (analyticsManager == null) analyticsManager = FindFirstObjectByType<AnalyticsManager>();

        // Load previous bias offsets from persistent storage if they exist
        LoadBiasOffsets();
    }

    void OnApplicationQuit() => TryCalibrate();
    void OnDestroy()          => TryCalibrate();

    // ── Public API ───────────────────────────────────────────────────

    /// <summary>Returns the gravity bias offset for a given zone (0 if uncalibrated).</summary>
    public float GetZoneBias(string sensorId)
    {
        if (_zoneBias.TryGetValue(sensorId, out float b)) return b;
        return 0f;
    }

    // ── Calibration ──────────────────────────────────────────────────

    private void TryCalibrate()
    {
        if (_calibrationDone) return;
        if (!config.calibrationEnabled) return;
        _calibrationDone = true;

        string reportPath = Path.Combine(Application.dataPath, config.validationExportPath);
        if (!File.Exists(reportPath))
        {
            Debug.LogWarning("[CalibrationManager] Validation report not found — skipping calibration.");
            return;
        }

        var adjustments = new List<CalibrationAdjustment>();

        try
        {
            string json = File.ReadAllText(reportPath);
            var sensors = ParsePerSensorBias(json);

            float lr  = config.calibrationLearningRate;
            float cap = config.calibrationMaxAdjustment;

            foreach (var (sensorId, bias, meanObs) in sensors)
            {
                if (meanObs <= 0f) continue;  // guard: prevent division by zero

                // Normalised bias: how far off are we as a fraction of the observed mean?
                float normBias = bias / meanObs;

                // P-controller: reduce bias by a fraction each run
                float delta = -lr * normBias;
                delta = Mathf.Clamp(delta, -cap, cap);

                // Accumulate offset — positive delta means "attract more agents here"
                if (!_zoneBias.ContainsKey(sensorId)) _zoneBias[sensorId] = 0f;
                _zoneBias[sensorId] = Mathf.Clamp(_zoneBias[sensorId] + delta, -2f, 2f);

                adjustments.Add(new CalibrationAdjustment
                {
                    sensorId    = sensorId,
                    bias        = bias,
                    meanObs     = meanObs,
                    normBias    = normBias,
                    delta       = delta,
                    newOffset   = _zoneBias[sensorId]
                });

                Debug.Log($"[Calibration] {sensorId}: bias={bias:F1} normBias={normBias:F3} " +
                          $"Δoffset={delta:+0.000;-0.000} → {_zoneBias[sensorId]:F3}");
            }

            SaveBiasOffsets();
            ExportCalibrationLog(adjustments);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CalibrationManager] Calibration failed: {ex.Message}");
        }
    }

    // ── JSON Parsing ─────────────────────────────────────────────────

    /// <summary>
    /// Minimal parser for the perSensor array in validation_report.json.
    /// Returns list of (sensorId, bias, meanObserved).
    /// </summary>
    private static List<(string, float, float)> ParsePerSensorBias(string json)
    {
        var result = new List<(string, float, float)>();

        // Find the perSensor array
        int psIdx = json.IndexOf("\"perSensor\"", StringComparison.Ordinal);
        if (psIdx < 0) return result;

        int arrStart = json.IndexOf('[', psIdx);
        int arrEnd   = json.IndexOf(']', arrStart);
        if (arrStart < 0 || arrEnd < 0) return result;

        string arrJson = json.Substring(arrStart, arrEnd - arrStart + 1);

        // Split into objects
        int depth = 0, objStart = -1;
        for (int i = 0; i < arrJson.Length; i++)
        {
            if (arrJson[i] == '{') { if (depth++ == 0) objStart = i; }
            else if (arrJson[i] == '}')
            {
                if (--depth == 0 && objStart >= 0)
                {
                    string obj = arrJson.Substring(objStart, i - objStart + 1);
                    string id  = ExtractString(obj, "sensorId");
                    float  b   = ExtractFloat(obj, "bias");
                    float  mo  = ExtractFloat(obj, "meanObserved");
                    if (!string.IsNullOrEmpty(id))
                        result.Add((id, b, mo));
                    objStart = -1;
                }
            }
        }
        return result;
    }

    private static string ExtractString(string json, string key)
    {
        int k = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (k < 0) return null;
        int colon = json.IndexOf(':', k);
        int q1    = json.IndexOf('"', colon + 1);
        int q2    = json.IndexOf('"', q1 + 1);
        if (q1 < 0 || q2 < 0) return null;
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    private static float ExtractFloat(string json, string key)
    {
        int k = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (k < 0) return 0f;
        int colon = json.IndexOf(':', k);
        int start = colon + 1;
        while (start < json.Length && json[start] == ' ') start++;
        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' ||
               json[end] == '-' || json[end] == 'E' || json[end] == 'e' || json[end] == '+'))
            end++;
        if (float.TryParse(json.Substring(start, end - start),
                           NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            return v;
        return 0f;
    }

    // ── Persistence ──────────────────────────────────────────────────

    private string BiasFilePath => Path.Combine(Application.persistentDataPath, "calibration_bias.json");

    private void SaveBiasOffsets()
    {
        var sb = new StringBuilder("{\"biases\":[");
        bool first = true;
        foreach (var kvp in _zoneBias)
        {
            if (!first) sb.Append(',');
            sb.Append($"{{\"id\":\"{kvp.Key}\",\"offset\":{kvp.Value.ToString("F4", CultureInfo.InvariantCulture)}}}");
            first = false;
        }
        sb.Append("]}");
        File.WriteAllText(BiasFilePath, sb.ToString());
    }

    private void LoadBiasOffsets()
    {
        if (!File.Exists(BiasFilePath)) return;
        try
        {
            string json = File.ReadAllText(BiasFilePath);
            int arrS = json.IndexOf('['); int arrE = json.IndexOf(']');
            if (arrS < 0 || arrE < 0) return;
            string arr = json.Substring(arrS, arrE - arrS + 1);

            int d = 0, oS = -1;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] == '{') { if (d++ == 0) oS = i; }
                else if (arr[i] == '}' && --d == 0 && oS >= 0)
                {
                    string obj  = arr.Substring(oS, i - oS + 1);
                    string id   = ExtractString(obj, "id");
                    float  bias = ExtractFloat(obj, "offset");
                    if (!string.IsNullOrEmpty(id)) _zoneBias[id] = bias;
                    oS = -1;
                }
            }
            Debug.Log($"[CalibrationManager] Loaded {_zoneBias.Count} bias offset(s) from previous run.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CalibrationManager] Could not load bias file: {ex.Message}");
        }
    }

    // ── Export ───────────────────────────────────────────────────────

    [Serializable]
    private struct CalibrationAdjustment
    {
        public string sensorId;
        public float  bias, meanObs, normBias, delta, newOffset;
    }

    private void ExportCalibrationLog(List<CalibrationAdjustment> adjustments)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append($"\"timestamp\":\"{DateTime.UtcNow:O}\",");
        sb.Append($"\"learningRate\":{config.calibrationLearningRate.ToString("F4", CultureInfo.InvariantCulture)},");
        sb.Append("\"adjustments\":[");

        for (int i = 0; i < adjustments.Count; i++)
        {
            var a = adjustments[i];
            if (i > 0) sb.Append(',');
            sb.Append("{");
            sb.Append($"\"sensorId\":\"{a.sensorId}\",");
            sb.Append($"\"bias\":{a.bias.ToString("F3", CultureInfo.InvariantCulture)},");
            sb.Append($"\"meanObserved\":{a.meanObs.ToString("F3", CultureInfo.InvariantCulture)},");
            sb.Append($"\"normBias\":{a.normBias.ToString("F4", CultureInfo.InvariantCulture)},");
            sb.Append($"\"delta\":{a.delta.ToString("F4", CultureInfo.InvariantCulture)},");
            sb.Append($"\"newOffset\":{a.newOffset.ToString("F4", CultureInfo.InvariantCulture)}");
            sb.Append("}");
        }

        sb.Append("]}");

        string dir  = Path.Combine(Application.dataPath, "SimOutput");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(Application.dataPath, config.calibrationExportPath);
        File.WriteAllText(path, sb.ToString());
        Debug.Log($"[CalibrationManager] Calibration log → {path}");
    }
}
