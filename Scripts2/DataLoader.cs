using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Phase 2, Task 2.1 — DataLoader.cs
/// Reads Mike's Posterbuddy CSV, parses every row into a SensorSnapshot,
/// and loads them into a chronological queue keyed by simulation time.
///
/// Expected CSV schema:
///   timestamp,sensor_id,occupancy,arrivals,departures,avg_dwell_sec
///   2026-04-10T09:00:00Z,Booth_A,0,0,0,0
///   ...
/// </summary>
public class DataLoader : MonoBehaviour
{
    [Header("Configuration")]
    [Tooltip("Path to the Posterbuddy CSV file (relative to StreamingAssets).")]
    public string csvFileName = "posterbuddy_data.csv";

    /// <summary>
    /// One row of Mike's CSV, parsed into typed fields.
    /// </summary>
    [System.Serializable]
    public struct SensorSnapshot
    {
        public DateTime timestamp;
        public string   sensorId;
        public int      occupancy;
        public int      arrivals;
        public int      departures;
        public float    avgDwellSec;

        /// <summary>Seconds elapsed since the earliest timestamp in the dataset.</summary>
        public float simTime;
    }

    /// <summary>
    /// All snapshots for a single 5-minute interval, grouped together.
    /// CrowdManager pops one of these per tick.
    /// </summary>
    [System.Serializable]
    public class TimeSlice
    {
        public float simTime;
        public List<SensorSnapshot> snapshots = new List<SensorSnapshot>();
    }

    // ── Public API ──────────────────────────────────────────────────
    /// <summary>Chronological queue of time slices. CrowdManager dequeues from this.</summary>
    public Queue<TimeSlice> TimeSliceQueue { get; private set; }

    /// <summary>Set of all unique sensor IDs found in the CSV.</summary>
    public HashSet<string> SensorIds { get; private set; }

    /// <summary>The absolute DateTime of the first row (used as sim t=0).</summary>
    public DateTime EpochTime { get; private set; }

    /// <summary>Total duration of the dataset in seconds.</summary>
    public float TotalDurationSec { get; private set; }

    /// <summary>True after Load() has successfully completed.</summary>
    public bool IsLoaded { get; private set; }


    // ── Lifecycle ───────────────────────────────────────────────────
    void Awake()
    {
        TimeSliceQueue = new Queue<TimeSlice>();
        SensorIds = new HashSet<string>();
        Load();
    }


    // ── Core Parser ─────────────────────────────────────────────────
    /// <summary>
    /// Parse the CSV into a sorted list of SensorSnapshots, then bucket them
    /// into TimeSlices (one per unique timestamp) and enqueue chronologically.
    /// </summary>
    public void Load()
    {
        IsLoaded = false;
        TimeSliceQueue.Clear();
        SensorIds.Clear();

        string fullPath = Path.Combine(Application.streamingAssetsPath, csvFileName);
        if (!File.Exists(fullPath))
        {
            Debug.LogError($"[DataLoader] CSV not found at: {fullPath}");
            return;
        }

        string[] lines = File.ReadAllLines(fullPath);
        if (lines.Length < 2)
        {
            Debug.LogError("[DataLoader] CSV has no data rows.");
            return;
        }

        // ── Step 1: Parse all rows ──────────────────────────────────
        List<SensorSnapshot> allSnapshots = new List<SensorSnapshot>(lines.Length - 1);

        for (int i = 1; i < lines.Length; i++) // skip header
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            string[] cols = line.Split(',');
            if (cols.Length < 6)
            {
                Debug.LogWarning($"[DataLoader] Skipping malformed row {i}: {line}");
                continue;
            }

            try
            {
                SensorSnapshot snap = new SensorSnapshot
                {
                    timestamp   = DateTime.Parse(cols[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    sensorId    = cols[1].Trim(),
                    occupancy   = int.Parse(cols[2]),
                    arrivals    = int.Parse(cols[3]),
                    departures  = int.Parse(cols[4]),
                    avgDwellSec = float.Parse(cols[5], CultureInfo.InvariantCulture)
                };
                allSnapshots.Add(snap);
                SensorIds.Add(snap.sensorId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DataLoader] Parse error on row {i}: {ex.Message}");
            }
        }

        if (allSnapshots.Count == 0)
        {
            Debug.LogError("[DataLoader] No valid rows parsed.");
            return;
        }

        // ── Step 2: Sort chronologically ────────────────────────────
        allSnapshots.Sort((a, b) => a.timestamp.CompareTo(b.timestamp));

        EpochTime = allSnapshots[0].timestamp;
        DateTime lastTime = allSnapshots[allSnapshots.Count - 1].timestamp;
        TotalDurationSec = (float)(lastTime - EpochTime).TotalSeconds;

        // Assign simTime (seconds since epoch)
        for (int i = 0; i < allSnapshots.Count; i++)
        {
            var snap = allSnapshots[i];
            snap.simTime = (float)(snap.timestamp - EpochTime).TotalSeconds;
            allSnapshots[i] = snap;
        }

        // ── Step 3: Bucket into TimeSlices by timestamp ─────────────
        var sliceMap = new SortedDictionary<float, TimeSlice>();
        foreach (var snap in allSnapshots)
        {
            if (!sliceMap.ContainsKey(snap.simTime))
            {
                sliceMap[snap.simTime] = new TimeSlice { simTime = snap.simTime };
            }
            sliceMap[snap.simTime].snapshots.Add(snap);
        }

        foreach (var kvp in sliceMap)
        {
            TimeSliceQueue.Enqueue(kvp.Value);
        }

        IsLoaded = true;
        Debug.Log($"[DataLoader] Loaded {allSnapshots.Count} snapshots across {sliceMap.Count} time slices. " +
                  $"Sensors: {SensorIds.Count}. Duration: {TotalDurationSec}s. Epoch: {EpochTime:O}");
    }
}
