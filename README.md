# EASL 2026 — Conference Digital Twin

A Unity-based pedestrian simulation and digital twin of the EASL 2026 International Liver Congress venue (Halls 7, 8, 8.1, and Mezzanine). NPC agents navigate the real floor plan using research-calibrated pedestrian dynamics, producing spatial logs, heatmaps, and validation reports that can be compared against ground-truth sensor data.

---

## Project Structure

```
easl-2025/
├── Assets/
│   ├── Scenes/
│   │   ├── SampleScene.unity          # Main simulation scene
│   │   └── SampleScene 26.unity       # Hall 26 variant
│   ├── Scripts/
│   │   ├── AgentController.cs         # Per-agent physics + 6-state FSM
│   │   ├── CrowdManager.cs            # Simulation director, spawning, object pool
│   │   ├── ConferenceZone.cs          # Zone metadata (type, topics, spawn point)
│   │   ├── SimConfig.cs               # ScriptableObject — all tunable parameters
│   │   ├── DataLoader.cs              # Ingests sensor_data.csv into time-slice queue
│   │   ├── SessionScheduleLoader.cs   # Reads session_schedule.csv for agenda redirects
│   │   ├── AnalyticsManager.cs        # Position logging, heatmap, validation reports
│   │   ├── HeatmapOverlay.cs          # Real-time heatmap visualisation in-scene
│   │   ├── FlowSensorTrigger.cs       # Box-collider triggers that count foot traffic
│   │   ├── CongestionMonitor.cs       # Bottleneck detection and event logging
│   │   ├── CalibrationManager.cs      # Auto-calibrates gravity β after each run
│   │   ├── AgentColorizer.cs          # Colors agents by behavioural state
│   │   ├── AgentGroup.cs              # Social group cohesion + contagion container
│   │   ├── SpatialGrid.cs             # Spatial hash grid for O(n) SFM queries
│   │   ├── SimulationHUD.cs           # On-screen stats overlay
│   │   ├── HallSwitcher.cs            # Toggle hall geometry visibility at runtime
│   │   ├── LLMConversationClient.cs   # Optional Claude API calls during Socializing
│   │   ├── SimValidator.cs            # Standalone validation helper
│   │   ├── QueueNode.cs               # Priority queue node for spawn scheduling
│   │   └── Editor/
│   │       ├── ConferenceSceneTool.cs     # Scene setup wizard
│   │       ├── ConferenceSetupWindow.cs   # Editor window for zone placement
│   │       ├── ConferenceZoneEditor.cs    # Custom inspector for ConferenceZone
│   │       ├── NavMeshBaker.cs            # One-click NavMesh bake helper
│   │       ├── FixAgentSize.cs            # Batch-fix agent NavMesh radius
│   │       └── SceneAutoSetup.cs          # Automates scene wiring on load
│   ├── StreamingAssets/
│   │   ├── sensor_data.csv            # Ground-truth headcounts from Posterbuddy sensors
│   │   ├── session_schedule.csv       # Conference schedule (session → zone → time)
│   │   └── sim_config.json            # Optional JSON override for SimConfig fields
│   ├── SimOutput/                     # Written at runtime — gitignored PNG, included CSVs
│   │   ├── spatial_log.csv            # Agent positions every 0.5 s
│   │   ├── heatmap.csv                # Average density per 0.5 m² cell
│   │   ├── zone_timeseries.csv        # Zone occupancy snapshots every 30 s
│   │   ├── flow_metrics.csv           # Cumulative enter/exit counts per minute
│   │   ├── bottleneck_events.csv      # Detected congestion events with location + severity
│   │   ├── simulation_summary.json    # KPIs: peak occupancy, avg dwell, duration
│   │   └── validation_report.json     # RMSE, NRMSE, χ², Pearson r vs. observed data
│   ├── Materials/                     # Floor, wall, and agent glow materials
│   ├── Settings/                      # URP render pipeline assets (PC + Mobile)
│   ├── Hall 7.gltf                    # Exported venue geometry
│   ├── Hall 8.gltf
│   ├── Hall 8.1.gltf
│   ├── Hall 8.1 Mezzanine.gltf
│   ├── EASL2026 - Hall 7 v2.gltf
│   ├── Joining Path.gltf
│   ├── Agent.prefab                   # Agent archetype with NavMeshAgent + scripts
│   └── SimConfig.asset                # Default parameter preset
├── Packages/
│   ├── manifest.json                  # Unity package dependencies
│   └── packages-lock.json
├── ProjectSettings/                   # Unity editor and project configuration
├── Scripts2/                          # Standalone C# scripts (pre-integration drafts)
├── Pedestrian_Dynamics_Reference.md   # Full academic reference for all model parameters
└── README.md
```

---

## How It Works

### 1. Data Ingestion

`DataLoader` reads `sensor_data.csv` at startup. Each row is a timestamped headcount snapshot from a Posterbuddy sensor co-located with a `ConferenceZone`. The loader converts wall-clock timestamps to simulation seconds and pushes sorted `TimeSlice` structs into a priority queue consumed by `CrowdManager`.

`SessionScheduleLoader` reads `session_schedule.csv` and fires `CrowdManager.RedirectAgentsToZone()` at the scheduled times, pulling agents toward session rooms as talks begin.

### 2. Spawning

`CrowdManager` uses **Poisson process spawning** — inter-arrival times are sampled as `Δt = -ln(U) / λ` where `λ = count / window`. This produces realistic bursty arrivals rather than uniform trickle. Spawn events are inserted into a sorted queue and drained each `FixedUpdate` when `SimClock` passes the event time.

Agents can arrive in **social groups**. Group size is sampled from a Poisson distribution with `λ = 2.3` (configurable), meaning most arrivals are solo but pairs and small groups are common. Group members share an `AgentGroup` object that applies cohesion forces and propagates socializing behaviour.

### 3. Agent Behaviour — 6-State FSM

Each agent runs a finite state machine:

| State | Behaviour |
|---|---|
| **Transit** | Walking to assigned zone via NavMesh + Social Force Model |
| **Dwelling** | Stationary at booth. Duration sampled from log-normal distribution |
| **Roaming** | Slow wandering near zone using Ornstein-Uhlenbeck heading process |
| **Socializing** | Stationary conversation cluster. Contagious within social groups |
| **Resting** | Seated at rest area. Partially recovers fatigue |
| **Exiting** | Routing to exit → returned to object pool |

### 4. Persona System

Agents are assigned one of five personas at spawn, sampled by weighted probability:

| Persona | Weight | Key traits |
|---|---|---|
| Researcher | 40% | Long dwell, topic-driven, low social |
| Networker | 25% | Fast walker, short dwell, high social |
| Student | 20% | Slightly faster, broad topics |
| Industry | 10% | Moderate dwell, high social, targets HCC/transplant |
| BoothStaff | 5% | Slow, very long dwell, rarely moves |

Each persona has multipliers on speed, dwell duration, socializing duration, fatigue rate, and topic affinity.

### 5. Zone Selection — Gravity Model

When an agent finishes dwelling and selects its next destination, it uses a **gravity model with entropy maximization** (Wilson 1967):

```
P(zone j) ∝ attractiveness(j) × exp(-β × distance(j)) × visitPenalty(j)
```

Where:
- `β` (impedance) starts at `0.15 /m` and grows with fatigue — tired agents prefer nearby zones
- `attractiveness` is boosted by topic overlap between the agent's persona and the zone's `topicTags`
- `visitPenalty` reduces the probability of revisiting already-seen booths (persona-modulated)
- Crowding is modelled as a sigmoid avoidance: `P(enter) = 1 / (1 + exp(sensitivity × (density - threshold)))`

Hunger and thirst drives can **override** the gravity model entirely, routing agents to the nearest `food` or `drink` zone type when drives exceed configurable thresholds.

---

## Pedestrian Physics — Social Force Model

Agent movement in Transit uses the **Helbing Social Force Model** (Helbing & Molnar 1995, Helbing et al. 2000). The net force on agent `i` is:

```
m × dv/dt = F_drive + Σⱼ F_social(i,j) + Σ_W F_wall(i,W) + F_group
```

### Driving Force

```
F_drive = m × (v₀ × ê_goal - v) / τ
```

Pulls the agent toward its NavMesh waypoint at desired speed `v₀`, with relaxation time `τ = 0.5 s`.

### Agent-Agent Repulsion

```
F_ij = [A × exp((rᵢⱼ - dᵢⱼ) / B) + k × g(rᵢⱼ - dᵢⱼ)] × n̂ᵢⱼ
       + κ × g(rᵢⱼ - dᵢⱼ) × Δv_tangential × t̂ᵢⱼ
```

Where `g(x) = x` when agents overlap (`x > 0`), else `0`. The second term is sliding friction during physical contact.

### Anisotropy

Agents react more strongly to people ahead of them than behind:

```
w(φ) = λ + (1 - λ) × (1 + cos φ) / 2
```

With `λ = 0.5`, agents in the field of view (~200°) exert roughly twice the repulsion of those behind.

### Wall Repulsion

Uses `NavMesh.FindClosestEdge` each frame — no physics collider overhead. Same exponential form as agent repulsion, with sliding friction along the wall tangent.

### Neighbour Queries

Agent-agent forces use `SpatialGrid` — a spatial hash with 5 m cells. Only neighbours in adjacent cells are evaluated, giving **O(n)** complexity instead of the naive O(n²).

### Calibrated Parameters

| Parameter | Symbol | Value | Source |
|---|---|---|---|
| Desired speed (free-flow) | v₀ | 1.34 m/s | Weidmann (1993) |
| Desired speed (browsing) | v₀ | 0.80 m/s | Teknomo (2006) |
| Relaxation time | τ | 0.5 s | Helbing & Molnar (1995) |
| Agent radius | r | 0.25 m | Weidmann (1993) |
| Repulsion strength | A | 2000 N | Helbing et al. (2000) |
| Repulsion range | B | 0.08 m | Helbing et al. (2000) |
| Body compression | k | 1.2 × 10⁵ kg/s² | Helbing et al. (2000) |
| Sliding friction | κ | 2.4 × 10⁵ kg/(m·s) | Helbing et al. (2000) |
| Anisotropy | λ | 0.5 | Helbing & Molnar (1995) |
| Jam density | ρ_max | 5.4 persons/m² | Seyfried et al. (2005) |

Speed decreases with density according to the Seyfried fundamental diagram:

```
v(ρ) = v₀ × (1 - exp(-γ × (1/ρ - 1/ρ_max)))    γ ≈ 1.913 m²
```

---

## Fatigue Model

Walking speed decays exponentially with cumulative walk time (not wall-clock time):

```
v(t) = v₀ × [α + (1 - α) × exp(-t / T_fatigue)]
```

- `α = 0.75` — agents never drop below 75% of their starting speed
- `T_fatigue = 7200 s` (120 min active walking)

Dwell time also decays across the session as exhibit fatigue accumulates (Bitgood 2009):

```
DwellTime(n) = DwellTime₀ × n^(-δ)     δ ≈ 0.20
```

The 10th booth visited receives roughly 60% of the dwell time of the first.

Fatigue also increases the gravity model's impedance `β`, making tired agents strongly prefer nearby booths over distant ones.

---

## Dwell Time Distribution

Time spent at each booth is sampled from a **log-normal distribution** (Serrell 1997):

```
DwellTime ~ LogNormal(μ = 5.70, σ = 0.80)   [seconds]
```

This gives a median dwell of ~300 s (5 minutes) with a long right tail representing deep discussions. The distribution is then scaled by persona `dwellMult` and the visit-number decay factor above.

---

## Day-Phase Rhythm

Agent behaviour shifts across the conference day:

| Phase | Hours | Speed | Social | Rest |
|---|---|---|---|---|
| Opening | 08:00–09:30 | +15% | −30% | −50% |
| Morning | 09:30–12:00 | baseline | baseline | baseline |
| Lunch | 12:00–14:00 | −20% | +30% | +50% |
| Afternoon | 14:00–17:00 | −10% | +10% | +20% |
| Evening | 17:00–22:00 | −25% | +50% | +80% |

---

## Analytics & Outputs

`AnalyticsManager` writes to `Assets/SimOutput/` at runtime:

| File | Contents | Update frequency |
|---|---|---|
| `spatial_log.csv` | `sim_time, agent_id, x, z, state, zone_id` | Every 0.5 s |
| `heatmap.csv` | Average density per 0.5 m² grid cell | On quit |
| `heatmap.png` | False-colour density image (blue → red) | On quit |
| `zone_timeseries.csv` | Dwelling / roaming / transiting counts per zone | Every 30 s |
| `flow_metrics.csv` | Cumulative enters and exits per zone per minute | Every 60 s |
| `bottleneck_events.csv` | Congestion events (location, agent count, timestamp) | On detection |
| `simulation_summary.json` | Peak occupancy zone, avg dwell, sim duration | On quit |
| `validation_report.json` | RMSE, NRMSE, χ², Pearson r vs. sensor_data.csv | On quit |

Buffers are flushed to disk every 5000 lines to bound RAM usage during long runs.

---

## Validation

On quit, the simulation automatically computes four validation metrics against the observed `sensor_data.csv`:

**RMSE / NRMSE** (temporal agreement per zone):
```
RMSE = √( (1/T) × Σₜ [N_sim(t) - N_obs(t)]² )
NRMSE = RMSE / N̄_obs          target: NRMSE < 0.15
```

**Chi-squared goodness of fit** (headcount distribution):
```
χ² = Σⱼ (N_sim_j - N_obs_j)² / N_obs_j
```

**Pearson spatial correlation** (heatmap shape):
```
r = Σ [(H_sim - H̄_sim)(H_obs - H̄_obs)] / √[Σ(H_sim - H̄_sim)² × Σ(H_obs - H̄_obs)²]
```
Target: `r > 0.80`

**Kolmogorov-Smirnov test** (flow rate distributions via `FlowSensorTrigger` colliders).

---

## Auto-Calibration

`CalibrationManager` reads the validation report after each run and applies proportional corrections to the gravity model's `β` and zone attractiveness parameters:

```
β_new = β_old × (1 + learningRate × bias_fraction)
```

Bounded by `calibrationMaxAdjustment = 15%` per run. Enabled by default; disable in `SimConfig`.

---

## Configuration

All parameters live in `Assets/SimConfig.asset` (a `SimConfig` ScriptableObject). Drag it onto `CrowdManager` in the Inspector. Key fields:

```csharp
// Social Force Model
desiredSpeed        = 1.34f   // m/s free-flow
relaxationTime      = 0.50f   // s
agentRadius         = 0.25f   // m
socialForceA        = 2000f   // N
socialForceB        = 0.08f   // m

// Fatigue
fatigueTimeConstant = 7200f   // s (120 min)
minSpeedFraction    = 0.75f   // floor at 75% of v₀
dwellDecayExponent  = 0.20f   // power-law per visit

// Dwell time (log-normal, seconds)
dwellMu             = 5.70f   // ln(300)
dwellSigma          = 0.80f

// Gravity model
gravityBeta0        = 0.15f   // 1/m impedance
fatigueZoneBias     = 0.75f   // how much β grows with fatigue

// Groups
groupSizeLambda     = 2.30f   // Poisson mean
groupSizeMax        = 6
```

`sim_config.json` in `StreamingAssets/` can override any field at runtime without recompiling.

---

## Running the Simulation

1. Open the project in **Unity 6** (or 2022.3 LTS+).
2. Open `Assets/Scenes/SampleScene.unity`.
3. Assign `sensor_data.csv` and `session_schedule.csv` to `DataLoader` and `SessionScheduleLoader`.
4. Set `timeScaleMultiplier` on `CrowdManager` (e.g. `60` = one conference minute per real second).
5. Press Play. Use `SimulationHUD` (on-screen overlay) to monitor live state counts.
6. On Stop, outputs appear in `Assets/SimOutput/`.

---

## References

1. Helbing, D. & Molnar, P. (1995). "Social force model for pedestrian dynamics." *Physical Review E*, 51(5), 4282–4286.
2. Helbing, D., Farkas, I. & Vicsek, T. (2000). "Simulating dynamical features of escape panic." *Nature*, 407, 487–490.
3. Weidmann, U. (1993). "Transporttechnik der Fussgänger." *Schriftenreihe des IVT*, 90, ETH Zürich.
4. Seyfried, A., Steffen, B., Klingsch, W. & Boltes, M. (2005). "The fundamental diagram of pedestrian movement revisited." *J. Stat. Mech.*, P10002.
5. Wilson, A.G. (1967). "A statistical theory of spatial distribution models." *Transportation Research*, 1(3), 253–269.
6. Serrell, B. (1997). "Paying attention: The duration and allocation of visitors' time in museum exhibitions." *Curator*, 40(2), 108–125.
7. Bitgood, S. (2009). "Museum fatigue: A critical review." *Visitor Studies*, 12(2), 93–111.
8. Johansson, A., Helbing, D. & Shukla, P.K. (2007). "Specification of the social force pedestrian model." *Advances in Complex Systems*, 10(S2), 271–288.
9. Van Zuylen, H.J. & Willumsen, L.G. (1980). "The most likely trip matrix estimated from traffic counts." *Transportation Research Part B*, 14(3), 281–293.

See `Pedestrian_Dynamics_Reference.md` for full equations, parameter derivations, and validation methodology.
