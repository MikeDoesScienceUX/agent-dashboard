# EASL 2026 — Conference Digital Twin

Unity pedestrian simulation of the EASL 2026 venue (Halls 7, 8, 8.1, Mezzanine). Agents navigate the real floor plan using research-calibrated crowd dynamics and produce spatial logs, heatmaps, and validation reports against ground-truth sensor data.

---

## Project Structure

```
Assets/
├── Scenes/
│   └── SampleScene.unity          # Main simulation scene
├── Scripts/
│   ├── AgentController.cs         # Per-agent physics + 6-state FSM
│   ├── CrowdManager.cs            # Simulation director, spawning, object pool
│   ├── ConferenceZone.cs          # Zone metadata (type, topics, spawn point)
│   ├── SimConfig.cs               # All tunable parameters (ScriptableObject)
│   ├── DataLoader.cs              # Ingests sensor_data.csv
│   ├── SessionScheduleLoader.cs   # Reads session_schedule.csv for agenda redirects
│   ├── AnalyticsManager.cs        # Position logging, heatmap, validation reports
│   ├── HeatmapOverlay.cs          # Real-time heatmap visualisation
│   ├── FlowSensorTrigger.cs       # Collider triggers that count foot traffic
│   ├── CongestionMonitor.cs       # Bottleneck detection and event logging
│   ├── CalibrationManager.cs      # Auto-calibrates gravity β after each run
│   ├── AgentColorizer.cs          # Colors agents by state
│   ├── AgentGroup.cs              # Social group cohesion + contagion
│   └── SpatialGrid.cs             # Spatial hash for O(n) neighbour queries
├── StreamingAssets/
│   ├── sensor_data.csv            # Ground-truth headcounts (Posterbuddy sensors)
│   ├── session_schedule.csv       # Conference schedule (session → zone → time)
│   └── sim_config.json            # Runtime parameter overrides
├── SimOutput/                     # Written at runtime
│   ├── spatial_log.csv
│   ├── heatmap.csv / heatmap.png
│   ├── zone_timeseries.csv
│   ├── flow_metrics.csv
│   ├── bottleneck_events.csv
│   ├── simulation_summary.json
│   └── validation_report.json
├── Hall 7.gltf                    # Venue geometry
├── Hall 8.gltf
├── Hall 8.1.gltf
├── Hall 8.1 Mezzanine.gltf
├── Agent.prefab
└── SimConfig.asset                # Default parameter preset
```

---

## Simulation Mechanics

### Spawning
Agents arrive via a **Poisson process** driven by `sensor_data.csv` headcounts. Inter-arrival times are sampled as `Δt = -ln(U) / λ`, producing realistic bursty arrivals. Agents can arrive in social groups (Poisson-distributed size, mean ~2.3).

### Agent Behaviour
Each agent runs a 6-state FSM: **Transit → Dwelling → Roaming → Socializing → Resting → Exiting**. Zone selection uses a **gravity model** — agents prefer nearer zones, zones matching their topic interests, and zones they haven't visited yet. Hunger and thirst drives can override zone selection to route agents to food/drink zones.

### Social Force Model (Helbing et al.)
Movement in Transit is governed by three forces summed each `FixedUpdate`:
- **Driving force** — pulls toward NavMesh waypoint at desired speed with relaxation time τ = 0.5 s
- **Agent repulsion** — exponential repulsion + body compression + sliding friction on contact
- **Wall repulsion** — same form, using `NavMesh.FindClosestEdge` (no physics collider overhead)

Agents respond more to people ahead of them than behind (anisotropy λ = 0.5). Neighbour queries use a spatial hash grid — O(n), not O(n²).

### Fatigue
Walking speed decays with cumulative walk time (floor at 75% of starting speed, time constant ~120 min). Dwell time at each subsequent booth also decays as a power law (Bitgood 2009). Fatigued agents become more sensitive to distance in the gravity model, preferring nearby booths.

### Dwell Time
Time at each booth is sampled from a **log-normal distribution** (median ~5 min, σ = 0.8), scaled by persona and visit-number decay.

### Personas
Five agent types (Researcher 40%, Networker 25%, Student 20%, Industry 10%, BoothStaff 5%) with distinct speed, dwell, socializing, and fatigue multipliers, and topic affinities.

### Validation
On quit, the simulation computes RMSE, NRMSE, χ², and Pearson r against `sensor_data.csv` and writes `validation_report.json`. `CalibrationManager` uses these results to adjust gravity parameters for the next run.

For full equations and parameter derivations, see `Pedestrian_Dynamics_Reference.md`.

---

## Running

1. Open in **Unity 6** (or 2022.3 LTS+).
2. Open `Assets/Scenes/SampleScene.unity`.
3. Set `timeScaleMultiplier` on `CrowdManager` (e.g. `60` = 1 conference minute per real second).
4. Press Play. Outputs write to `Assets/SimOutput/` on Stop.

All parameters are in `Assets/SimConfig.asset`. Fields can be overridden at runtime via `StreamingAssets/sim_config.json` without recompiling.
