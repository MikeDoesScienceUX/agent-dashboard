# Codebase Structure

**Analysis Date:** 2026-04-27

## Directory Layout

```
easl-2025/
├── Assets/
│   ├── Scripts/                    # Core simulation C# (~4800 lines total)
│   │   ├── AgentController.cs      # Per-agent physics + 6-state FSM (925 lines)
│   │   ├── CrowdManager.cs         # Simulation director + object pool (~500 lines)
│   │   ├── ConferenceZone.cs       # Zone metadata + gizmo visualization (~150 lines)
│   │   ├── SimConfig.cs            # ScriptableObject: all tunable parameters (~280 lines)
│   │   ├── DataLoader.cs           # CSV parser: sensor_data.csv (~250 lines)
│   │   ├── SessionScheduleLoader.cs # CSV parser: session_schedule.csv (~180 lines)
│   │   ├── AnalyticsManager.cs     # Logging + heatmap + validation (~400 lines)
│   │   ├── SimulationHUD.cs        # Runtime overlay UI (300+ lines)
│   │   ├── CongestionMonitor.cs    # Crowd safety monitoring + alerts (~180 lines)
│   │   ├── AgentColorizer.cs       # State-to-color mapper
│   │   ├── AgentGroup.cs           # Social group cohesion + contagion (54 lines)
│   │   ├── SpatialGrid.cs          # O(n) neighbor query via spatial hash (78 lines)
│   │   ├── FlowSensorTrigger.cs    # Hallway flow counting via collider triggers (36 lines)
│   │   ├── CalibrationManager.cs   # Post-run β tuning
│   │   ├── LLMConversationClient.cs # Optional Claude Haiku API integration
│   │   ├── SimValidator.cs         # Offline scene validation checks
│   │   ├── ConfigLoader.cs         # Runtime JSON override of SimConfig
│   │   └── Editor/                 # Editor tools (not required at runtime)
│   │       ├── ConferenceSetupWindow.cs
│   │       ├── ConferenceSceneTool.cs
│   │       ├── NavMeshBaker.cs
│   │       └── SceneAutoSetup.cs
│   ├── Scenes/
│   │   ├── SampleScene.unity       # Main venue simulation (working scene)
│   │   └── SampleScene 26.unity    # Current state (last modified)
│   ├── StreamingAssets/            # Runtime data (loaded from disk)
│   │   ├── sensor_data.csv         # Ground-truth Posterbuddy headcounts (timestamped zone counts)
│   │   ├── session_schedule.csv    # Conference schedule (session → room → times → attendance)
│   │   └── sim_config.json         # Runtime SimConfig parameter overrides (optional)
│   ├── SimOutput/                  # Generated at runtime (not in repo)
│   │   ├── spatial_log.csv         # Agent positions + states every 0.5 s
│   │   ├── heatmap.csv             # 2D grid of average occupancy (0.5 m cells)
│   │   ├── heatmap.png             # False-color visualization of heatmap
│   │   ├── zone_timeseries.csv     # Per-zone per-state agent counts every 30 s
│   │   ├── flow_metrics.csv        # Per-flow-sensor cumulative counts per minute
│   │   ├── bottleneck_events.csv   # Timestamped density alerts (warning/danger)
│   │   ├── simulation_summary.json # KPIs: peak occupancy, avg dwell, total agents
│   │   └── validation_report.json  # RMSE, NRMSE, χ², Pearson r vs. ground truth
│   ├── Materials/                  # Shared shaders/materials
│   ├── Settings/                   # URP/rendering settings
│   ├── Hall *.gltf                 # Venue geometry (imported GLTF venue models)
│   ├── Agent.prefab                # Agent GameObject prefab (used by pool)
│   └── SimConfig.asset             # Default ScriptableObject instance
├── ProjectSettings/                # Unity project configuration
├── Library/                        # Build cache (not committed)
├── Temp/                           # Temporary builds
├── .gitignore
├── README.md                       # High-level overview and usage
└── Pedestrian_Dynamics_Reference.md # Research citations (equations, parameters)
```

## Directory Purposes

**Assets/Scripts/:**
- Purpose: All C# source code for simulation, spawning, agent behavior, analytics, and UI
- Contains: 18 core scripts (~4800 lines total) + Editor tools
- Key files: `CrowdManager.cs` (director), `AgentController.cs` (agent brain), `SimConfig.cs` (parameters)

**Assets/Scenes/:**
- Purpose: Unity scene files (.unity binary format) containing the floor plan geometry, zone markers, spawn/exit points, and initial component configuration
- Contains: Main working scene (`SampleScene.unity`) with all hierarchy setup
- Key files: `SampleScene 26.unity` (current version with all markers, zones, prefab assignments)

**Assets/StreamingAssets/:**
- Purpose: Runtime CSV and JSON files bundled with the build, read via `Application.streamingAssetsPath`
- Contains: Ground-truth sensor data and session schedule as timestamped CSVs, optional JSON parameter overrides
- Key files:
  - `sensor_data.csv`: Posterbuddy sensor readings (timestamp, zone_id, enters, exits, occupancy_snapshot)
  - `session_schedule.csv`: Conference sessions (session_id, room_id, start/end times, expected attendance)
  - `sim_config.json`: Optional runtime parameter patch (JSON keys override SimConfig fields)

**Assets/SimOutput/:**
- Purpose: Generated at runtime; simulation outputs CSV and JSON files for analysis and validation
- Contains: Spatial logs, heatmaps, validation metrics, bottleneck events
- Generated by: `AnalyticsManager.cs` on every FixedUpdate (periodic flush) and `OnApplicationQuit()`

**Assets/Editor/:**
- Purpose: Editor-only tools for scene setup, NavMesh baking, and configuration window
- Contains: Window UI, automated zone detection, agent size fixing, NavMesh baker
- Usage: Tools → Conference Sim menu in Unity Editor (not runtime)

**Assets/Materials/:**
- Purpose: Shared materials and shaders (URP-compatible)
- Usage: Agent color display via MaterialPropertyBlock in `AgentController.ApplyStateColor()`

**Assets/Hall *.gltf:**
- Purpose: Venue geometry imported from external floor plan GLTF models
- Contains: 3D mesh, colliders for NavMesh baking, wall boundaries
- Usage: Visual context + NavMesh surface for pathfinding

**Assets/Agent.prefab:**
- Purpose: Prefab template for agent GameObjects (sphere collider, renderer, NavMeshAgent, AgentController)
- Usage: Instantiated by object pool (`CrowdManager`) at startup and recycled on spawn/despawn

**Assets/SimConfig.asset:**
- Purpose: Default ScriptableObject instance holding all ~50 tunable simulation parameters
- Usage: Assigned to `CrowdManager.config` in Inspector; can be overridden at runtime via `sim_config.json`

## Key File Locations

**Entry Points:**

- `Assets/Scripts/CrowdManager.cs`: Simulation director; Start() initializes zones/pool, FixedUpdate() drives spawning and agent ticking
- `Assets/Scripts/AgentController.cs`: Per-agent behavior; FixedUpdate() runs state machine and SFM forces
- `Assets/Scenes/SampleScene 26.unity`: Active scene with all configured zones, spawn/exit points, and CrowdManager/DataLoader/AnalyticsManager assigned

**Configuration:**

- `Assets/SimConfig.asset`: Default parameters (SFM forces, fatigue, dwell distribution, density thresholds, persona multipliers, day-phases, colors)
- `Assets/StreamingAssets/sim_config.json`: Runtime JSON patch (optional; keys override SimConfig fields without recompile)
- `Assets/StreamingAssets/sensor_data.csv`: Ground-truth headcounts (drives spawning and validation)

**Core Logic:**

- `Assets/Scripts/AgentController.cs`: 6-state FSM (Transit/Dwelling/Roaming/Socializing/Resting/Exiting), SFM physics, fatigue/physiological drives, zone gravity model
- `Assets/Scripts/CrowdManager.cs`: CSV-driven spawning via Poisson process, object pool, zone registry, spatial grid, day-phase tracking, session redirect API
- `Assets/Scripts/SpatialGrid.cs`: Spatial hash for O(n) neighbor queries in SFM calculations
- `Assets/Scripts/ConferenceZone.cs`: Zone metadata (type, topics, floor area) auto-discovered at startup

**Testing/Validation:**

- `Assets/Scripts/AnalyticsManager.cs`: Position logging, heatmap accumulation, zone timeseries, flow counting, validation metric computation (RMSE, χ², Pearson r)
- `Assets/Scripts/CongestionMonitor.cs`: Real-time density monitoring with ISF thresholds (comfortable ≤1 p/m², warning ~2, danger ~4)
- `Assets/Scripts/SimValidator.cs`: Offline checks (NavMesh coverage, ConferenceZone completeness, missing references)

**Data Input:**

- `Assets/Scripts/DataLoader.cs`: Parses `sensor_data.csv`, exposes `TimeSliceQueue` and ground-truth lookup
- `Assets/Scripts/SessionScheduleLoader.cs`: Parses `session_schedule.csv`, fires redirect events to CrowdManager at session start/end

**UI/Display:**

- `Assets/Scripts/SimulationHUD.cs`: IMGUI overlay (press H to toggle); shows clock, speed control, live state counts, zone occupancy, validation alerts
- `Assets/Scripts/AgentColorizer.cs`: Mapper from agent state to MaterialPropertyBlock color

## Naming Conventions

**Files:**

- `[Feature]Manager.cs`: Manager/coordinator component (e.g., `CrowdManager`, `AnalyticsManager`, `CongestionMonitor`)
- `[Feature]Controller.cs`: Per-object behavior component (e.g., `AgentController`)
- `[Feature].cs`: Lightweight data/utility class (e.g., `AgentGroup`, `SpatialGrid`, `SimConfig`)
- `[Feature]Loader.cs`: CSV/JSON parser (e.g., `DataLoader`, `SessionScheduleLoader`, `ConfigLoader`)
- `[Feature]Trigger.cs`: Physics trigger component (e.g., `FlowSensorTrigger`)

**Directories:**

- `Editor/`: Files with `[MenuItem]` or `EditorGUILayout` (not included in builds)
- `StreamingAssets/`: Runtime data files (CSV, JSON, images) bundled with app
- `SimOutput/`: Generated output files (per-run logs, metrics, validation reports)

**Classes/Enums:**

- `AgentState`: Public enum in `AgentController` for state values (Transit, Dwelling, Roaming, Socializing, Resting, Exiting)
- `PersonaConfig`: Serializable struct in `SimConfig` defining persona type (name, spawn weight, multipliers, topic preferences)
- `TimeSlice`: Serializable class in `DataLoader` grouping all zone snapshots at one timestamp
- `ZoneAlert`: Struct in `CongestionMonitor` for per-zone density status

## Where to Add New Code

**New Feature (e.g., new behavioral state or monitoring system):**

1. **Implementation:** Create `Assets/Scripts/[Feature]Manager.cs` (if system-level) or `Assets/Scripts/[Feature]Trigger.cs` (if per-zone)
2. **Integration:** Add reference field to `CrowdManager`, `AgentController`, or `AnalyticsManager` as appropriate; call from their FixedUpdate() or Start()
3. **Tests:** Create validation in `SimValidator.cs` if offline checks needed, or log metrics to SimOutput/ CSV

**Example: Adding a new monitoring metric**

1. Add field to `AnalyticsManager`: `private List<(float t, float value)> _newMetric`
2. Sample in FixedUpdate(): `_newMetric.Add((crowdManager.SimClock, ComputeMetric()))`
3. Write to SimOutput/ on quit: `File.WriteAllLines(path, _newMetric.Select(...))`

**New Agent Behavior (e.g., sub-state within Roaming):**

1. Extend `AgentState` enum: add `RoamingSubtype1`
2. Add tick handler: `void TickRoamingSubtype1(float dt)`
3. Add transition in `TickRoaming()` or `EnterState()`
4. Add state color in `ApplyStateColor()`

**New Zone Type or Metadata:**

1. Add field to `ConferenceZone.cs` (e.g., `public string newMetadata`)
2. Add accessor to `CrowdManager.GetZone[Metadata]()` if agents query it
3. Auto-discover in `ConferenceZone.OnDrawGizmos()` for Editor visualization

## Special Directories

**Assets/Editor/:**
- Purpose: Editor-only extensions (not compiled into build)
- Generated: No
- Committed: Yes (build-time tools, but not required for runtime)
- Contents: `ConferenceSetupWindow` (zone auto-detection UI), `NavMeshBaker` (mesh baking helper), `FixAgentSize` (fixes agent collider radius), `SceneAutoSetup` (populate zone/spawn/exit arrays)

**Assets/SimOutput/:**
- Purpose: Runtime-generated analytics, logs, and validation metrics
- Generated: Yes (per run, written by `AnalyticsManager` + `CongestionMonitor`)
- Committed: No (.gitignore ignores SimOutput/*)
- Contents: CSV logs (spatial, zone timeseries, flow, bottlenecks), PNG heatmap, JSON summary + validation report

**Assets/StreamingAssets/:**
- Purpose: Runtime data bundled with the built application
- Generated: No (user-provided CSV + optional JSON)
- Committed: Yes (required for simulation to run)
- Contents: `sensor_data.csv` (ground truth), `session_schedule.csv` (sessions), `sim_config.json` (optional overrides)

**Library/, Temp/, obj/:**
- Purpose: Build cache, temporary files, IL2CPP output (git-ignored)
- Generated: Yes (by Unity build process)
- Committed: No

## Reference Map: Classes → File Locations

| Class | File | Purpose |
|-------|------|---------|
| `CrowdManager` | `Assets/Scripts/CrowdManager.cs` | Simulation director, spawning, pooling |
| `AgentController` | `Assets/Scripts/AgentController.cs` | Per-agent FSM, physics, behavior |
| `SimConfig` | `Assets/Scripts/SimConfig.cs` | ScriptableObject with all parameters |
| `ConferenceZone` | `Assets/Scripts/ConferenceZone.cs` | Zone metadata + gizmo |
| `DataLoader` | `Assets/Scripts/DataLoader.cs` | CSV parser for sensor data |
| `SessionScheduleLoader` | `Assets/Scripts/SessionScheduleLoader.cs` | CSV parser for session schedule |
| `AnalyticsManager` | `Assets/Scripts/AnalyticsManager.cs` | Logging, heatmap, validation |
| `SimulationHUD` | `Assets/Scripts/SimulationHUD.cs` | IMGUI overlay UI |
| `CongestionMonitor` | `Assets/Scripts/CongestionMonitor.cs` | Crowd safety monitoring |
| `SpatialGrid` | `Assets/Scripts/SpatialGrid.cs` | Spatial hash for physics |
| `AgentGroup` | `Assets/Scripts/AgentGroup.cs` | Social group data (lightweight) |
| `FlowSensorTrigger` | `Assets/Scripts/FlowSensorTrigger.cs` | Collider-based flow counting |
| `AgentColorizer` | `Assets/Scripts/AgentColorizer.cs` | State-to-color mapper |
| `LLMConversationClient` | `Assets/Scripts/LLMConversationClient.cs` | Claude Haiku API wrapper |
| `SimValidator` | `Assets/Scripts/SimValidator.cs` | Offline validation checks |
| `ConfigLoader` | `Assets/Scripts/ConfigLoader.cs` | Runtime JSON config patch |
| `CalibrationManager` | `Assets/Scripts/CalibrationManager.cs` | Post-run parameter tuning |

---

*Structure analysis: 2026-04-27*
