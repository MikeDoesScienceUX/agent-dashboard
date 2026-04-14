using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Reads session_schedule.csv and fires zone-redirect events to CrowdManager
/// at each session start/end, driving realistic session-room population surges.
///
/// CSV schema (Assets/StreamingAssets/session_schedule.csv):
///   session_id,room_id,start_time,end_time,type,expected_attendance
///   EASL-001,arnold,2026-05-07T09:00:00Z,2026-05-07T10:30:00Z,keynote,150
///
/// room_id must match a ConferenceZone.sensorId in the scene.
/// start_time / end_time must use the same date as sensor_data.csv.
///
/// Attach to: ___SimController alongside CrowdManager and DataLoader.
/// </summary>
[AddComponentMenu("Conference Sim/Session Schedule Loader")]
public class SessionScheduleLoader : MonoBehaviour
{
    [Header("References")]
    public CrowdManager crowdManager;
    public DataLoader   dataLoader;

    [Header("CSV File")]
    [Tooltip("Filename inside StreamingAssets/.")]
    public string csvFileName = "session_schedule.csv";

    // ── Data ────────────────────────────────────────────────────────

    public struct SessionEntry
    {
        public string sessionId;
        public string roomId;
        public float  startSimTime;
        public float  endSimTime;
        public string type;
        public int    expectedAttendance;
        public bool   startFired;
        public bool   endFired;
    }

    private List<SessionEntry> _sessions = new List<SessionEntry>();

    public bool IsLoaded { get; private set; }

    // ── Lifecycle ───────────────────────────────────────────────────

    void Start()
    {
        if (crowdManager == null) crowdManager = FindFirstObjectByType<CrowdManager>();
        if (dataLoader   == null) dataLoader   = FindFirstObjectByType<DataLoader>();
        Load();
    }

    void FixedUpdate()
    {
        if (!IsLoaded || crowdManager == null) return;

        float t = crowdManager.SimClock;

        for (int i = 0; i < _sessions.Count; i++)
        {
            var s = _sessions[i];

            // Session start: redirect agents to room
            if (!s.startFired && t >= s.startSimTime)
            {
                s.startFired = true;
                _sessions[i] = s;
                crowdManager.RedirectAgentsToZone(s.roomId, s.expectedAttendance);
                Debug.Log($"[SessionSchedule] Session '{s.sessionId}' started in '{s.roomId}' " +
                          $"— redirecting {s.expectedAttendance} agents. t={t:F0}s");
            }

            // Session end: release agents back to free movement
            if (!s.endFired && t >= s.endSimTime)
            {
                s.endFired = true;
                _sessions[i] = s;
                crowdManager.ReleaseZoneAgents(s.roomId);
                Debug.Log($"[SessionSchedule] Session '{s.sessionId}' ended in '{s.roomId}'. t={t:F0}s");
            }
        }
    }

    // ── Parser ──────────────────────────────────────────────────────

    public void Load()
    {
        IsLoaded = false;
        _sessions.Clear();

        string path = Path.Combine(Application.streamingAssetsPath, csvFileName);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[SessionSchedule] No schedule file at: {path}  (optional — skipping)");
            return;
        }

        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2) return;

        string[] hdr = lines[0].Split(',');
        int iId   = Col(hdr, "session_id");
        int iRoom = Col(hdr, "room_id");
        int iSt   = Col(hdr, "start_time");
        int iEt   = Col(hdr, "end_time");
        int iType = Col(hdr, "type");
        int iAtt  = Col(hdr, "expected_attendance");

        if (iId < 0 || iRoom < 0 || iSt < 0 || iEt < 0)
        {
            Debug.LogError("[SessionSchedule] Missing required columns: session_id, room_id, start_time, end_time");
            return;
        }

        DateTime? epoch = dataLoader != null && dataLoader.IsLoaded
            ? dataLoader.EpochTime
            : (DateTime?)null;

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            string[] c = line.Split(',');

            try
            {
                var start = DateTime.Parse(c[iSt].Trim(), CultureInfo.InvariantCulture,
                                           DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal);
                var end   = DateTime.Parse(c[iEt].Trim(), CultureInfo.InvariantCulture,
                                           DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal);

                // Use DataLoader epoch if available; otherwise use first session as t=0
                if (!epoch.HasValue) epoch = start;

                _sessions.Add(new SessionEntry
                {
                    sessionId           = c[iId].Trim(),
                    roomId              = c[iRoom].Trim(),
                    startSimTime        = (float)(start - epoch.Value).TotalSeconds,
                    endSimTime          = (float)(end   - epoch.Value).TotalSeconds,
                    type                = iType >= 0 && iType < c.Length ? c[iType].Trim() : "talk",
                    expectedAttendance  = iAtt  >= 0 && iAtt  < c.Length ? int.Parse(c[iAtt].Trim()) : 50,
                    startFired          = false,
                    endFired            = false
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SessionSchedule] Parse error row {i}: {ex.Message}");
            }
        }

        _sessions.Sort((a, b) => a.startSimTime.CompareTo(b.startSimTime));
        IsLoaded = true;
        Debug.Log($"[SessionSchedule] Loaded {_sessions.Count} session(s).");
    }

    /// <summary>Returns all loaded sessions (read-only copy).</summary>
    public IReadOnlyList<SessionEntry> Sessions => _sessions;

    private static int Col(string[] h, string name)
    {
        for (int i = 0; i < h.Length; i++)
            if (string.Equals(h[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
