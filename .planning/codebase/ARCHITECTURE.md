# Architecture

**Analysis Date:** 2026-04-27

## Pattern Overview

**Overall:** Multi-layer discrete event simulation with agent-based modeling (ABM). A crowd director orchestrates CSV-driven spawning and session scheduling, while individual agents execute physics-based pathfinding layered with behavioral state machines. External ground-truth sensor data constrains and validates the simulation.

**Key Characteristics:**
- **Discrete event + continuous physics:** Events (spawning, redirects, state changes) tick on FixedUpdate, while agents run a real-time Social Force Model physics loop between state transitions.
- **Object pooling for performance:** Agent GameObjects recycled rather than destroyed/instantiated, eliminating GC spikes in long runs.
- **CSV-driven realism:** All spawning, session schedules, and validation metrics come from real Posterbuddy sensor data and EASL schedules.
- **Research-calibrated parameters:** Pedestrian dynamics (Helbing SFM), fatigue modeling (exponential decay), dwell distributions (log-normal), and density avoidance (sigmoid) all grounded in published literature.

## Layers

**Simulation Director:**
- Purpose: Owns the simulation clock, CSV spawning queue, object pool, spatial grid, day-phase tracking, and zone metadata. Acts as single source of truth for agent spawning, despawning, zone assignment, and session redirects.
- Location: `Assets/Scripts/CrowdManager.cs`
- Contains: Main update loop, spawn-queue processing, agent lifecycle management, zone registry, spatial hash rebuild
- Depends on: `SimConfig`, `DataLoader`, `AgentController`, `ConferenceZone`, `SpatialGrid`, `AgentGroup`
- Used by: `AgentController` (queries), `AnalyticsManager`, `SimulationHUD`, `SessionScheduleLoader`, `CongestionMonitor`

**Agent Behavior Engine:**
- Purpose: Per-agent physics, pathfinding override, state machine (6 states), physiological drives (hunger/thirst), and social force calculations. Each agent evaluates zone gravity, applies Helbing SFM forces every FixedUpdate, and transistions between states based on dwell completion, fatigue, crowd density, and agenda.
- Location: `Assets/Scripts/AgentController.cs` (~925 lines)
- Contains: State machine (Transit → Dwelling → Roaming → Socializing → Resting → Exiting), SFM force solver, zone selection gravity model, persona multipliers, fatigue tracking, group cohesion, LLM integration hook
- Depends on: `SimConfig`, `NavMeshAgent`, `CrowdManager`, `AgentGroup`, `SpatialGrid`
- Used by: `CrowdManager` (spawns/pools), `AgentGroup` (contagion), `LLMConversationClient`

**Data Input Layer:**
- Purpose: Load and parse CSV files (sensor data with timestamped zone headcounts, session schedule with room assignments and times) and expose them as queues or lookups.
- Location: `Assets/Scripts/DataLoader.cs`, `Assets/Scripts/SessionScheduleLoader.cs`
- Contains: CSV parsing with CultureInfo-aware DateTime parsing, `TimeSlice` queues, zone ID registry, ground-truth occupancy lookup for validation
- Depends on: File I/O, `CultureInfo`
- Used by: `CrowdManager` (spawn timing), `SessionScheduleLoader` (session time mapping), `AnalyticsManager` (validation)

**Scene Layout & Zones:**
- Purpose: Represents each conference venue area (booth, session room, food/drink, rest area, hallway). Stores metadata (zone type, topic tags, floor area, spawn override) that agents query for gravity-model scoring and physiological drive routing.
- Location: `Assets/Scripts/ConferenceZone.cs`
- Contains: Zone identity (sensorId), content type (exhibit/food/drink/session/rest), topic tags for persona matching, goal point for pathfinding, spawn point override, polygon/rectangle gizmo visualization
- Depends on: `Transform`, gizmo drawing
- Used by: `CrowdManager` (auto-discovery at Start), `AgentController` (zone scoring, type lookup)

**Physics & Pathfinding:**
- Purpose: Apply Helbing Social Force Model per FixedUpdate (driving + agent repulsion + wall repulsion + group cohesion forces), override NavMeshAgent velocity, smooth agent facing. Spatial hash grid accelerates neighbor lookups from O(n²) to O(n).
- Location: Agent movement in `AgentController.ApplySocialForces()` (~120 lines), `Assets/Scripts/SpatialGrid.cs`
- Contains: Force integration (velocity Euler update with clamping), micro-steering noise, agent-agent repulsion with anisotropy (agents react more to what's ahead), wall forces via `NavMesh.FindClosestEdge`, group cohesion force
- Depends on: `NavMeshAgent`, `NavMesh`, `SpatialGrid`
- Used by: `AgentController` every frame during moving states

**Analytics & Validation:**
- Purpose: Log agent positions and states to CSV, accumulate heatmap grid, compute zone occupancy timeseries, measure flow through sensor triggers, and on simulation end write validation metrics (RMSE, NRMSE, χ², Pearson r) against ground-truth sensor data.
- Location: `Assets/Scripts/AnalyticsManager.cs` (~400 lines)
- Contains: Dual-buffer CSV logging (spatial, zone timeseries, flow metrics), heatmap grid accumulation, per-zone occupancy tracking, summary JSON generation, validation report computation
- Depends on: `DataLoader`, `CrowdManager`, `FlowSensorTrigger`, file I/O
- Used by: Simulation lifecycle (logs every frame/period, writes on OnDestroy/quit)

**Monitoring & Safety:**
- Purpose: Real-time crowd safety assessment. Every second of simulation, checks each zone's density against ISF (International Safety Foundation) thresholds (comfortable ≤1.0 p/m², warning ~2.0, danger ~4.0) and fires alerts (HUD warning, console log, bottleneck CSV event) when thresholds are crossed.
- Location: `Assets/Scripts/CongestionMonitor.cs`
- Contains: Zone-by-zone density calculation, threshold checks, alert cooldown per zone, `ZoneAlert` list refreshed each check interval
- Depends on: `CrowdManager`, file I/O for bottleneck events
- Used by: `SimulationHUD` (displays alerts), simulation oversight

**UI & Runtime Control:**
- Purpose: Overlay HUD (press H to toggle) showing simulation clock, speed scaling (with keyboard + × controls), live per-state agent counts, zone occupancy bars with colors, session schedule display, and validation alerts. Press Space to pause, F12 to screenshot, M to toggle debug heatmap overlay.
- Location: `Assets/Scripts/SimulationHUD.cs` (~300 lines)
- Contains: IMGUI layout system, cached stat dictionaries (refreshed in Update), color-coded state bars, persona breakdowns, zone occupancy bars, validation issue display
- Depends on: `CrowdManager`, `CongestionMonitor`, `SessionScheduleLoader`, `SimValidator`
- Used by: Player interaction

## Data Flow

**Spawn Flow:**

1. `CrowdManager.Start()` calls `DataLoader.Load()` → parses `sensor_data.csv` into `TimeSlice` queue (per-timestamp grouped snapshots of all zones)
2. `CrowdManager.FixedUpdate()` checks if `SimClock ≥ next TimeSlice time`
3. If yes, dequeues `TimeSlice`, iterates zones, samples Poisson distribution (lambda = CSV enter count / simulation time window) to decide how many agents spawn this frame
4. For each spawn: pick persona (cumulative-weight sampler), select spawn point (zone override or global pool), create/activate agent from pool
5. If `groupSpawning` enabled, agents often spawn in social groups (Poisson group size, average ~2.3)
6. `AgentController.Initialize()` sets persona multipliers, personal speed (Gaussian ±0.26), agenda, physiological state
7. Agent enters `Transit` state → pathfinds to first zone from gravity model

**State Transition Flow:**

```
Transit
  ├─ Sigmoid crowding check: if density too high, → Roaming
  └─ Within 1.5 m of goal → Dwelling

Dwelling
  ├─ Timer expires (log-normal dwell with visit decay)
  ├─ Physiological override? (hunger/thirst) → Reroute via CheckPhysiologicalOverride()
  ├─ Random: 30% chance stay/move to next zone
  ├─ 20% chance → Socializing
  ├─ 20% chance (if fatigued) → Resting
  └─ Else stay (re-roll dwell timer)

Roaming (Ornstein-Uhlenbeck heading)
  ├─ Timer expires
  ├─ Density low & random? → Pick next zone (gravity model), MoveAgentToZone()
  ├─ Else 55% → Socializing
  ├─ Else (if fatigued) → Resting
  └─ Else extend roaming

Socializing
  ├─ Fires LLM conversation once per session (if enabled)
  ├─ Timer expires (60–600 s)
  └─ 50% → Transit, 50% → Roaming

Resting (partial fatigue recovery)
  ├─ Timer expires (300–1200 s)
  └─ → Transit

Exiting
  ├─ Pathfinds to nearest exit point
  └─ Within 1 m → ReturnToPool() (agent disabled, returned to queue)
```

**Session Redirect Flow:**

1. `SessionScheduleLoader.FixedUpdate()` checks if `SimClock ≥ session start time`
2. Calls `CrowdManager.RedirectAgentsToZone(roomId, expectedAttendance)`
3. If `smoothSessionRedirects = true`:
   - Agent queues redirect (stores pending goal + zone) with max 2-minute timeout
   - Finishes current state (Dwelling, Roaming, Socializing) naturally
   - `ApplyPendingRedirect()` at state exit or timeout
4. If `smoothSessionRedirects = false`: immediate `MoveAgentToZoneImmediate()`
5. At session end time: `ReleaseZoneAgents(roomId)` clears redirect flags, agents resume free movement

**Zone Selection (Gravity Model):**

When agent must pick next zone:
```
for each zone z:
  dist = distance(agent, z)
  interest = PersonaInterestScore(agent.persona, z.topicTags)
  visitPenalty = _visitedZones.Contains(z) ? 0.30^visitPenaltyMult : 1.0
  density = CurrentOccupancy(z) / z.areaM2
  beta = gravityBeta0 × (1 + fatigueZoneBias × (walkTime / fatigueTimeConstant))
  
  weight[z] = z.areaM2 × (0.5 + interest × 0.5) × exp(-beta × dist) × visitPenalty / (1 + density)

return random zone weighted by [weight]
```

**State Color Feedback:**

Each state is mapped to a color (MaterialPropertyBlock update every state change):
- Transit: Blue (~0.30, 0.65, 1.00)
- Dwelling: Green (~0.15, 0.90, 0.35)
- Roaming: Yellow (~1.00, 0.85, 0.15)
- Socializing: Magenta (~1.00, 0.45, 0.80)
- Resting: Purple (~0.60, 0.35, 1.00)
- Exiting: Red (~1.00, 0.30, 0.30)

## Key Abstractions

**AgentController (Per-Agent State Machine):**
- Purpose: Encapsulates all per-agent state, physics, and behavior. One component per agent GameObject.
- Examples: `Assets/Scripts/AgentController.cs` (925 lines)
- Pattern: Monobehaviour + component, FixedUpdate tick, state machine via enum + switch, public Initialize() + ResetForPool() for pooling lifecycle

**ConferenceZone (Zone Metadata):**
- Purpose: Marks a region of interest with semantic data (type, topics, floor area) that agents query. Auto-discovered by CrowdManager.
- Examples: `Assets/Scripts/ConferenceZone.cs` (150 lines)
- Pattern: Monobehaviour attached to scene GameObjects, public fields for inspector tweaking, gizmo visualization with polygon support

**SpatialGrid (Spatial Hash for Physics):**
- Purpose: O(1) insertion, O(k) neighbor queries (vs O(n) brute-force). Rebuilt every FixedUpdate.
- Examples: `Assets/Scripts/SpatialGrid.cs` (78 lines)
- Pattern: Standalone class (not Monobehaviour), 2D hash using Cantor pairing for negative-safe coords, cell-based buckets with reusable query buffer

**CrowdManager (Simulation Director):**
- Purpose: Single authoritative source for spawning, pooling, zone registry, spatial grid, and time advancement. Owns all dynamic state.
- Examples: `Assets/Scripts/CrowdManager.cs` (500+ lines)
- Pattern: Monobehaviour with DefaultExecutionOrder(-10), FixedUpdate processes spawn queue + updates grid, exposes public APIs (MoveAgentToZone, GetBoothDensity, FindNearestZoneByType)

**SimConfig (Tunable Parameters):**
- Purpose: ScriptableObject holding all ~50 simulation knobs (SFM forces, fatigue constants, dwell distributions, density thresholds, persona multipliers, day-phase schedules).
- Examples: `Assets/Scripts/SimConfig.cs` (250+ lines)
- Pattern: CreateAssetMenu ScriptableObject, serialized with [Header] sections for organization, default asset at `Assets/SimConfig.asset`

## Entry Points

**CrowdManager.Start() → FixedUpdate():**
- Location: `Assets/Scripts/CrowdManager.cs`
- Triggers: Scene load → Awake() → Start() → every FixedUpdate (~50 Hz)
- Responsibilities: Initialize zones & object pool, dequeue CSV time slices, spawn agents, manage active agent list, rebuild spatial grid, track day-phase index, cache statistics

**AgentController.FixedUpdate():**
- Location: `Assets/Scripts/AgentController.cs`
- Triggers: Every FixedUpdate (after CrowdManager due to execution order -10 vs 0)
- Responsibilities: Tick state machine, apply fatigue decay, accumulate physiological drives, handle pending redirects, apply social forces, update NavMesh destination

**DataLoader.Load():**
- Location: `Assets/Scripts/DataLoader.cs`
- Triggers: Awake() on scene load
- Responsibilities: Read `Assets/StreamingAssets/sensor_data.csv`, parse timestamped zone snapshots, populate TimeSlice queue, compute ground-truth occupancy lookup for validation

**SessionScheduleLoader.FixedUpdate():**
- Location: `Assets/Scripts/SessionScheduleLoader.cs`
- Triggers: Every FixedUpdate after DataLoader.Load()
- Responsibilities: Parse `Assets/StreamingAssets/session_schedule.csv`, check if SimClock ≥ session start/end times, fire redirect/release events to CrowdManager

**AnalyticsManager.FixedUpdate() + OnApplicationQuit():**
- Location: `Assets/Scripts/AnalyticsManager.cs`
- Triggers: Every FixedUpdate + on quit
- Responsibilities: Buffer agent positions/states every 0.5 s, accumulate heatmap grid, snapshot zone occupancy every 30 s, log flow events, compute and write validation report on quit

## Error Handling

**Strategy:** Defensive with debug logging. Missing references (null DataLoader, missing ConferenceZones) print warnings but continue with sensible defaults (empty spawn queue, auto-discovered zones, default SimConfig instance).

**Patterns:**

- **Null checks on references:** Every manager (CrowdManager, AnalyticsManager, etc.) checks if assigned components are null, logs warning, and creates default instances or no-ops.
  - Example: `AgentController.Initialize()` checks `if (_config == null) return` in TickTransit()
  - Example: `CrowdManager.Start()` creates default `SimConfig` if none assigned

- **Path validation:** DataLoader checks `File.Exists()` for CSV files, logs warning if missing (spawn queue stays empty, not a crash).

- **NavMesh fallback:** If `NavMesh.SamplePosition()` fails to snap position, agent uses unsnaped position (edge case, rare if NavMesh properly baked).

- **Stale reference cleanup:** Agent.ResetForPool() nullifies all references to enable GC. AgentGroup.Remove() handles null members gracefully.

## Cross-Cutting Concerns

**Logging:** Debug.Log/LogWarning used throughout for startup checks, spawn events, session redirects, validation errors. No production telemetry.

**Validation:** `AnalyticsManager` computes RMSE, NRMSE, χ², Pearson r against ground-truth sensor CSV data. `SimValidator` runs offline checks (NavMesh coverage, ConferenceZone completeness, missing topic tags).

**Authentication:** Not applicable (no external APIs called except optional LLMConversationClient for conversation generation, which authenticates separately).

**Time Scaling:** `CrowdManager.timeScaleMultiplier` (1–120×) scales FixedDeltaTime integration, allowing 1-minute simulation in 1 real second.

---

*Architecture analysis: 2026-04-27*
