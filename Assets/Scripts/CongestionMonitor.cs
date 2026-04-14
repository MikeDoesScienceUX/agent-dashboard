using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Real-time congestion and crowd safety monitoring.
///
/// Checks every zone against three density thresholds every second of sim time:
///   Green  (Normal)  — below warningDensity
///   Yellow (Warning) — warningDensity ≤ density < dangerDensity
///   Red    (Danger)  — density ≥ dangerDensity  ← alert fires
///
/// Based on ISF (International Safety Foundation) crowd density guidelines:
///   comfortable  ≤ 1.0 p/m²
///   warning      ≈ 2.0 p/m²
///   danger       ≈ 4.0 p/m²  (standing crush risk above ~6)
///
/// On alert:
///   • HUD warning banner and flashing zone colour override
///   • Console log
///   • Event written to bottleneck_events.csv
///
/// Attach to: ___SimController alongside CrowdManager.
/// </summary>
[AddComponentMenu("Conference Sim/Congestion Monitor")]
public class CongestionMonitor : MonoBehaviour
{
    [Header("References (auto-found if null)")]
    public CrowdManager crowdManager;

    [Header("Density Thresholds (persons / m²)")]
    [Tooltip("Below this — all clear.")]
    public float normalDensity  = 1.0f;
    [Tooltip("Yellow warning band.")]
    public float warningDensity = 2.0f;
    [Tooltip("Red alert — intervention recommended.")]
    public float dangerDensity  = 4.0f;

    [Header("Timing")]
    [Tooltip("How often to evaluate thresholds (real-time seconds, not sim seconds).")]
    [Range(0.25f, 5f)]
    public float checkInterval = 1f;

    [Header("Alert Cooldown")]
    [Tooltip("Minimum sim-seconds between repeated alerts for the same zone.")]
    public float alertCooldownSec = 120f;

    // ── Public state (read by SimulationHUD) ─────────────────────────

    public enum ZoneStatus { Normal, Warning, Danger }

    public struct ZoneAlert
    {
        public string     zoneId;
        public string     displayName;
        public ZoneStatus status;
        public float      density;
        public int        count;
        public float      simTime;
    }

    /// <summary>Current status of every monitored zone. Refreshed each check.</summary>
    public IReadOnlyList<ZoneAlert> CurrentAlerts => _current;

    /// <summary>True if any zone is currently in Danger status.</summary>
    public bool HasDanger  => _hasDanger;
    /// <summary>True if any zone is in Warning or Danger status.</summary>
    public bool HasWarning => _hasWarning;

    // ── Internal ─────────────────────────────────────────────────────

    private List<ZoneAlert> _current    = new List<ZoneAlert>();
    private bool            _hasDanger;
    private bool            _hasWarning;

    // Last alert fire time per zone (to enforce cooldown)
    private Dictionary<string, float> _lastAlertTime = new Dictionary<string, float>();

    // CSV output
    private string        _csvPath;
    private StringBuilder _csvBuf  = new StringBuilder(4096);
    private int           _csvLines;
    private float         _checkTimer;
    private bool          _initialised;

    // Flash state for HUD
    private float _flashTimer;
    public  bool  FlashOn { get; private set; }

    // ── Lifecycle ─────────────────────────────────────────────────────

    void Start()
    {
        if (crowdManager == null) crowdManager = FindFirstObjectByType<CrowdManager>();

        string dir = Path.Combine(Application.dataPath, "SimOutput");
        Directory.CreateDirectory(dir);
        _csvPath = Path.Combine(dir, "bottleneck_events.csv");
        File.WriteAllText(_csvPath, "sim_time,zone_id,display_name,density_pm2,count,status\n");
        _initialised = true;
    }

    void Update()
    {
        // Flash timer for HUD pulse (0.5 s on / 0.5 s off)
        _flashTimer += Time.deltaTime;
        if (_flashTimer >= 0.5f) { _flashTimer = 0f; FlashOn = !FlashOn; }

        if (!_initialised || crowdManager == null) return;

        _checkTimer += Time.deltaTime;
        if (_checkTimer >= checkInterval)
        {
            _checkTimer = 0f;
            Evaluate();
        }
    }

    void OnDestroy()
    {
        if (_csvBuf.Length > 0 && _csvPath != null)
            File.AppendAllText(_csvPath, _csvBuf.ToString());
    }

    // ── Core evaluation ───────────────────────────────────────────────

    private void Evaluate()
    {
        if (crowdManager.Zones == null) return;

        _current.Clear();
        _hasDanger  = false;
        _hasWarning = false;

        float simTime = crowdManager.SimClock;

        foreach (var zone in crowdManager.Zones)
        {
            if (zone == null) continue;

            float density = crowdManager.GetBoothDensity(zone.sensorId);
            int   count   = crowdManager.GetZoneCount(zone.sensorId);

            ZoneStatus status;
            if      (density >= dangerDensity)  status = ZoneStatus.Danger;
            else if (density >= warningDensity) status = ZoneStatus.Warning;
            else                                status = ZoneStatus.Normal;

            _current.Add(new ZoneAlert
            {
                zoneId      = zone.sensorId,
                displayName = zone.displayName,
                status      = status,
                density     = density,
                count       = count,
                simTime     = simTime,
            });

            if (status == ZoneStatus.Danger)  _hasDanger  = true;
            if (status >= ZoneStatus.Warning) _hasWarning = true;

            // Fire alert only when above danger and cooldown has passed
            if (status == ZoneStatus.Danger)
            {
                float lastFire = _lastAlertTime.TryGetValue(zone.sensorId, out var lf) ? lf : -999f;
                if (simTime - lastFire >= alertCooldownSec)
                {
                    _lastAlertTime[zone.sensorId] = simTime;
                    FireAlert(zone.sensorId, zone.displayName, density, count, simTime);
                }
            }
        }

        // Flush CSV every 200 lines
        if (_csvLines >= 200)
        {
            File.AppendAllText(_csvPath, _csvBuf.ToString());
            _csvBuf.Clear();
            _csvLines = 0;
        }
    }

    private void FireAlert(string zoneId, string displayName, float density, int count, float simTime)
    {
        int h = (int)(simTime / 3600), m = (int)(simTime % 3600 / 60);
        Debug.LogWarning($"[CongestionMonitor] ⚠ DANGER: '{displayName}' — " +
                         $"{density:F2} p/m² ({count} people) at sim {h:D2}:{m:D2}");

        _csvBuf.Append(simTime.ToString("F0", CultureInfo.InvariantCulture)).Append(',')
               .Append(zoneId).Append(',')
               .Append(displayName).Append(',')
               .Append(density.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
               .Append(count).Append(',')
               .AppendLine("DANGER");
        _csvLines++;
    }

    // ── Public API for HUD ────────────────────────────────────────────

    /// <summary>Returns the safety status colour for a zone ID.</summary>
    public Color GetStatusColor(string zoneId)
    {
        foreach (var a in _current)
        {
            if (a.zoneId != zoneId) continue;
            return a.status switch
            {
                ZoneStatus.Danger  => new Color(1.00f, 0.20f, 0.20f),
                ZoneStatus.Warning => new Color(1.00f, 0.80f, 0.10f),
                _                  => new Color(0.20f, 0.90f, 0.35f),
            };
        }
        return new Color(0.20f, 0.90f, 0.35f);
    }

    /// <summary>Returns a short status label for a zone ID.</summary>
    public string GetStatusLabel(string zoneId)
    {
        foreach (var a in _current)
        {
            if (a.zoneId != zoneId) continue;
            return a.status switch
            {
                ZoneStatus.Danger  => "⚠ DANGER",
                ZoneStatus.Warning => "▲ WARN",
                _                  => "✓ OK",
            };
        }
        return "✓ OK";
    }
}
