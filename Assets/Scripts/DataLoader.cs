using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Reads the Posterbuddy/sensor CSV and loads all rows into a chronological queue.
///
/// Expected CSV schema (EASL 2026 format):
///   timestamp,zone_id,enters,exits,occupancy_snapshot
///   2026-05-07T09:00:00Z,ExhibitHall,12,0,12
///   ...
///
/// One TimeSlice is produced per unique timestamp.
/// CrowdManager dequeues TimeSlices as the simulation clock advances.
/// </summary>
[AddComponentMenu("Conference Sim/Data Loader")]
public class DataLoader : MonoBehaviour
{
    [Header("CSV File")]
    [Tooltip("CSV filename inside StreamingAssets/. Include extension.")]
    public string csvFileName = "sensor_data.csv";

    // ── Public structs ──────────────────────────────────────────────

    [Serializable]
    public struct SensorSnapshot
    {
        public DateTime timestamp;
        public string   zoneId;
        public int      enters;
        public int      exits;
        public int      occupancySnapshot;
        /// <summary>Seconds elapsed since dataset epoch (first timestamp = 0).</summary>
        public float    simTime;
    }

    [Serializable]
    public class TimeSlice
    {
        public float simTime;
        public List<SensorSnapshot> snapshots = new List<SensorSnapshot>();
    }

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>Chronological queue of time slices. CrowdManager dequeues from this.</summary>
    public Queue<TimeSlice> TimeSliceQueue { get; private set; }

    /// <summary>All unique zone IDs found in the CSV.</summary>
    public HashSet<string> ZoneIds { get; private set; }

    /// <summary>DateTime of the first row (sim t = 0).</summary>
    public DateTime EpochTime { get; private set; }

    /// <summary>Total dataset duration in seconds.</summary>
    public float TotalDurationSec { get; private set; }

    /// <summary>True once Load() has completed without error.</summary>
    public bool IsLoaded { get; private set; }

    // Ground-truth lookup: zoneId → sorted list of (simTime, occupancySnapshot)
    private Dictionary<string, List<(float t, int occ)>> _groundTruth
        = new Dictionary<string, List<(float, int)>>();

    /// <summary>
    /// Returns the most-recently-passed CSV occupancy_snapshot for a zone at the given
    /// sim time, or -1 if no data exists for that zone.
    /// </summary>
    public int GetGroundTruth(string zoneId, float simTime)
    {
        if (!_groundTruth.TryGetValue(zoneId, out var list) || list.Count == 0) return -1;
        // Binary search: largest t ≤ simTime
        int lo = 0, hi = list.Count - 1, idx = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (list[mid].t <= simTime) { idx = mid; lo = mid + 1; }
            else                          hi = mid - 1;
        }
        return idx >= 0 ? list[idx].occ : list[0].occ;
    }

    // ── Lifecycle ───────────────────────────────────────────────────

    void Awake()
    {
        TimeSliceQueue = new Queue<TimeSlice>();
        ZoneIds = new HashSet<string>();
        Load();
    }

    // ── Parser ──────────────────────────────────────────────────────

    public void Load()
    {
        IsLoaded = false;
        TimeSliceQueue.Clear();
        ZoneIds.Clear();

        string fullPath = Path.Combine(Application.streamingAssetsPath, csvFileName);
        if (!File.Exists(fullPath))
        {
            Debug.LogError($"[DataLoader] CSV not found: {fullPath}");
            return;
        }

        string[] lines = File.ReadAllLines(fullPath);
        if (lines.Length < 2)
        {
            Debug.LogError("[DataLoader] CSV has no data rows.");
            return;
        }

        // Parse header to find column indices (flexible — column order doesn't matter)
        string[] header = lines[0].Split(',');
        int iTimestamp = FindCol(header, "timestamp");
        int iZone      = FindCol(header, "zone_id");
        int iEnters    = FindCol(header, "enters");
        int iExits     = FindCol(header, "exits");
        int iOcc       = FindCol(header, "occupancy_snapshot");

        if (iTimestamp < 0 || iZone < 0 || iEnters < 0 || iExits < 0)
        {
            Debug.LogError("[DataLoader] CSV is missing required columns: timestamp, zone_id, enters, exits");
            return;
        }

        var allSnapshots = new List<SensorSnapshot>(lines.Length);

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            string[] cols = line.Split(',');
            if (cols.Length <= Mathf.Max(iTimestamp, iZone, iEnters, iExits))
            {
                Debug.LogWarning($"[DataLoader] Skipping malformed row {i}: {line}");
                continue;
            }

            try
            {
                var snap = new SensorSnapshot
                {
                    // Accept both "2026-05-07T09:00:00" (no Z) and "2026-05-07T09:00:00Z"
                    timestamp         = DateTime.Parse(cols[iTimestamp].Trim(),
                                            CultureInfo.InvariantCulture,
                                            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal),
                    zoneId            = cols[iZone].Trim(),
                    enters            = int.Parse(cols[iEnters].Trim()),
                    exits             = int.Parse(cols[iExits].Trim()),
                    occupancySnapshot = iOcc >= 0 && iOcc < cols.Length
                                            ? int.Parse(cols[iOcc].Trim()) : 0
                };
                allSnapshots.Add(snap);
                ZoneIds.Add(snap.zoneId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DataLoader] Parse error row {i}: {ex.Message}");
            }
        }

        if (allSnapshots.Count == 0)
        {
            Debug.LogError("[DataLoader] No valid rows parsed.");
            return;
        }

        // Sort and assign simTime
        allSnapshots.Sort((a, b) => a.timestamp.CompareTo(b.timestamp));
        EpochTime = allSnapshots[0].timestamp;
        TotalDurationSec = (float)(allSnapshots[allSnapshots.Count - 1].timestamp - EpochTime).TotalSeconds;

        for (int i = 0; i < allSnapshots.Count; i++)
        {
            var s = allSnapshots[i];
            s.simTime = (float)(s.timestamp - EpochTime).TotalSeconds;
            allSnapshots[i] = s;
        }

        // Bucket into TimeSlices by timestamp
        var sliceMap = new SortedDictionary<float, TimeSlice>();
        foreach (var snap in allSnapshots)
        {
            if (!sliceMap.ContainsKey(snap.simTime))
                sliceMap[snap.simTime] = new TimeSlice { simTime = snap.simTime };
            sliceMap[snap.simTime].snapshots.Add(snap);
        }

        foreach (var kvp in sliceMap)
            TimeSliceQueue.Enqueue(kvp.Value);

        // Build ground-truth lookup (allSnapshots is already sorted by simTime)
        _groundTruth.Clear();
        foreach (var snap in allSnapshots)
        {
            if (!_groundTruth.ContainsKey(snap.zoneId))
                _groundTruth[snap.zoneId] = new List<(float, int)>();
            _groundTruth[snap.zoneId].Add((snap.simTime, snap.occupancySnapshot));
        }

        IsLoaded = true;
        Debug.Log($"[DataLoader] Loaded {allSnapshots.Count} snapshots | " +
                  $"{sliceMap.Count} time slices | {ZoneIds.Count} zones | " +
                  $"{TotalDurationSec}s total | Epoch: {EpochTime:O}");
    }

    private static int FindCol(string[] header, string name)
    {
        for (int i = 0; i < header.Length; i++)
            if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
