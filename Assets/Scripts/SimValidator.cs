using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Validates the full simulation setup at runtime and in the Editor wizard.
/// Call Validate() to get a list of issues with severity and fix instructions.
///
/// Attach to ___SimController and it auto-validates on Start.
/// SimulationHUD reads ActiveIssues for the on-screen warning panel.
/// </summary>
[AddComponentMenu("Conference Sim/Sim Validator")]
public class SimValidator : MonoBehaviour
{
    public enum Severity { Error, Warning, Info }

    public struct Issue
    {
        public Severity severity;
        public string   message;
        public string   fix;

        public Issue(Severity s, string msg, string f = "") { severity = s; message = msg; fix = f; }
    }

    /// <summary>Issues found in the last Validate() call. Read by SimulationHUD.</summary>
    public List<Issue> ActiveIssues { get; private set; } = new List<Issue>();

    public bool HasErrors   => ActiveIssues.Exists(i => i.severity == Severity.Error);
    public bool HasWarnings => ActiveIssues.Exists(i => i.severity == Severity.Warning);

    private CrowdManager  _cm;
    private DataLoader    _dl;

    void Start()
    {
        _cm = FindFirstObjectByType<CrowdManager>();
        _dl = FindFirstObjectByType<DataLoader>();
        Validate();

        if (HasErrors)
            Debug.LogError($"[SimValidator] {ActiveIssues.FindAll(i => i.severity == Severity.Error).Count} error(s). Open Window > Conference Sim > Zone Setup.");
        else if (HasWarnings)
            Debug.LogWarning($"[SimValidator] {ActiveIssues.FindAll(i => i.severity == Severity.Warning).Count} warning(s).");
        else
            Debug.Log("[SimValidator] Setup looks good.");
    }

    // ── Main Validate ───────────────────────────────────────────────

    /// <summary>Run all checks. Can be called from Editor tools as well.</summary>
    public List<Issue> Validate()
    {
        ActiveIssues.Clear();

        CheckCrowdManager();
        CheckDataLoader();
        CheckZones();
        CheckNavMesh();
        CheckAgentPrefab();
        CheckCSVZoneMatch();

        return ActiveIssues;
    }

    // ── Individual Checks ───────────────────────────────────────────

    void CheckCrowdManager()
    {
        var cm = _cm ?? FindFirstObjectByType<CrowdManager>();

        if (cm == null)
        {
            ActiveIssues.Add(new Issue(Severity.Error,
                "No CrowdManager found in scene.",
                "Add CrowdManager component to ___SimController."));
            return;
        }

        if (cm.spawnPoints == null || cm.spawnPoints.Length == 0)
        {
            // Only an error if no zones have individual spawnPoint overrides either
            var allZones = FindObjectsByType<ConferenceZone>(FindObjectsSortMode.None);
            bool anyZoneSpawn = System.Array.Exists(allZones, z => z.spawnPoint != null);
            if (!anyZoneSpawn)
                ActiveIssues.Add(new Issue(Severity.Error,
                    "CrowdManager has no Spawn Points and no zone-level spawnPoint overrides.",
                    "Assign entrance Transform(s) to CrowdManager > Spawn Points, OR assign a spawnPoint on each ConferenceZone."));
        }

        if (cm.exitPoints == null || cm.exitPoints.Length == 0)
            ActiveIssues.Add(new Issue(Severity.Warning,
                "CrowdManager has no Exit Points.",
                "Assign door/exit Transform(s) to CrowdManager > Exit Points. Agents will not be able to leave."));

        if (cm.agentPrefab == null)
            ActiveIssues.Add(new Issue(Severity.Error,
                "CrowdManager: Agent Prefab is not assigned.",
                "Create or assign a GameObject prefab with NavMeshAgent + AgentController components."));

        if (cm.config == null)
            ActiveIssues.Add(new Issue(Severity.Info,
                "No SimConfig asset assigned — default values will be used.",
                "Create via Assets > Create > Conference Sim > SimConfig, then assign to CrowdManager."));
    }

    void CheckDataLoader()
    {
        var dl = _dl ?? FindFirstObjectByType<DataLoader>();
        if (dl == null)
        {
            ActiveIssues.Add(new Issue(Severity.Error,
                "No DataLoader found in scene.",
                "Add DataLoader component to ___SimController."));
            return;
        }

        string path = Path.Combine(Application.streamingAssetsPath, dl.csvFileName);
        if (!File.Exists(path))
            ActiveIssues.Add(new Issue(Severity.Error,
                $"Sensor CSV not found: StreamingAssets/{dl.csvFileName}",
                "Place your sensor_data.csv in Assets/StreamingAssets/ and check the filename in DataLoader."));
        else if (!dl.IsLoaded)
            ActiveIssues.Add(new Issue(Severity.Error,
                "DataLoader failed to parse the CSV.",
                "Check the Unity Console for parse errors. Ensure the CSV has the required columns: timestamp, zone_id, enters, exits."));
    }

    void CheckZones()
    {
        var zones = FindObjectsByType<ConferenceZone>(FindObjectsSortMode.None);

        if (zones.Length == 0)
        {
            ActiveIssues.Add(new Issue(Severity.Error,
                "No ConferenceZone components found in scene.",
                "Open Window > Conference Sim > Zone Setup and import zones from your CSV, or create them manually."));
            return;
        }

        // Check for duplicate sensorIds
        var seen = new HashSet<string>();
        foreach (var z in zones)
        {
            if (string.IsNullOrEmpty(z.sensorId))
                ActiveIssues.Add(new Issue(Severity.Error,
                    $"ConferenceZone '{z.displayName}' has an empty sensorId.",
                    "Set sensorId to match the zone_id value in your CSV."));
            else if (!seen.Add(z.sensorId))
                ActiveIssues.Add(new Issue(Severity.Error,
                    $"Duplicate sensorId '{z.sensorId}' on multiple ConferenceZone objects.",
                    "Each zone must have a unique sensorId matching the CSV."));

            if (z.areaM2 <= 0)
                ActiveIssues.Add(new Issue(Severity.Warning,
                    $"Zone '{z.sensorId}' has areaM2 = {z.areaM2}. Density will be 0.",
                    "Set a realistic floor area (e.g., 20 m²) on the ConferenceZone component."));
        }
    }

    void CheckNavMesh()
    {
        // Prefer sampling near a known spawn point — avoids false positives when
        // the venue floor doesn't cover the world origin (common with glTF imports).
        Vector3 samplePos = Vector3.zero;
        var cm = _cm ?? FindFirstObjectByType<CrowdManager>();
        if (cm?.spawnPoints != null)
        {
            foreach (var sp in cm.spawnPoints)
            {
                if (sp != null) { samplePos = sp.position; break; }
            }
        }

        if (!NavMesh.SamplePosition(samplePos, out _, 100f, NavMesh.AllAreas))
            ActiveIssues.Add(new Issue(Severity.Error,
                "No NavMesh baked in this scene.",
                "Open Window > AI > Navigation (or Agents), select the floor mesh, mark it Navigation Static + Walkable, then click Bake."));
    }

    void CheckAgentPrefab()
    {
        var cm = _cm ?? FindFirstObjectByType<CrowdManager>();
        if (cm == null || cm.agentPrefab == null) return;

        if (cm.agentPrefab.GetComponent<NavMeshAgent>() == null)
            ActiveIssues.Add(new Issue(Severity.Error,
                "Agent prefab is missing a NavMeshAgent component.",
                "Add a NavMeshAgent to the agent prefab."));

        if (cm.agentPrefab.GetComponent<AgentController>() == null)
            ActiveIssues.Add(new Issue(Severity.Error,
                "Agent prefab is missing an AgentController component.",
                "Add AgentController to the agent prefab."));
    }

    void CheckCSVZoneMatch()
    {
        var dl = _dl ?? FindFirstObjectByType<DataLoader>();
        if (dl == null || !dl.IsLoaded) return;

        var zoneIds = new HashSet<string>();
        foreach (var z in FindObjectsByType<ConferenceZone>(FindObjectsSortMode.None))
            zoneIds.Add(z.sensorId);

        foreach (var csvZone in dl.ZoneIds)
        {
            if (!zoneIds.Contains(csvZone))
                ActiveIssues.Add(new Issue(Severity.Warning,
                    $"CSV zone_id '{csvZone}' has no matching ConferenceZone in scene.",
                    $"Add a ConferenceZone with sensorId = '{csvZone}', or remove it from the CSV."));
        }
    }
}
