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

## How It Works

### Startup
`CrowdManager` initializes first. It finds every `ConferenceZone` in the scene, tells `DataLoader` to parse `sensor_data.csv` (timestamped headcount snapshots from the real Posterbuddy sensors), and pre-warms a pool of 300 agent GameObjects ready to activate. `SessionScheduleLoader` reads `session_schedule.csv` so it knows when talks start and which zones to pull people toward.

### Spawning
Every frame, `CrowdManager` checks if the sim clock has passed the next scheduled arrival. Those events come from the CSV — if the sensor recorded 40 people entering a zone in a 15-minute window, the sim converts that to a **Poisson process** so agents trickle in naturally rather than all at once. When an agent spawns, there's a chance it arrives as part of a **social group** (Poisson-distributed size, average ~2.3 people). Each agent is also assigned a **persona** — Researcher (40%), Networker (25%), Student (20%), Industry (10%), BoothStaff (5%) — which sets their base speed, dwell behaviour, social tendencies, and topic interests.

### Each Agent's Life
Agents run a 6-state machine:

- **Transit** — walking to a zone. NavMesh handles pathfinding, but velocity each frame is computed by the **Social Force Model**: a pull toward the destination, repulsion from nearby agents (via spatial hash grid, O(n)), and repulsion from walls (via `NavMesh.FindClosestEdge`). Agents react more to people ahead of them than behind, which naturally produces lane formation in corridors.
- **Dwelling** — stationary at the zone. Duration sampled from a log-normal distribution (median ~5 min). Gets shorter with each subsequent booth visited — the 10th booth gets ~60% of the time the first one did (museum fatigue).
- **Roaming** — slow wandering near the zone. Heading is an Ornstein-Uhlenbeck process: random but mean-reverting toward the zone centre so agents don't drift into walls.
- **Socializing** — stopped for a conversation cluster (1–10 min). Contagious within social groups: if one member starts socializing, there's a 70% chance their companions join. Optionally fires a Claude Haiku API call to generate a conversation snippet.
- **Resting** — sitting down (5–20 min), partially recovering fatigue.
- **Exiting** — routes to the nearest exit and returns to the object pool.

### Zone Selection
When an agent finishes dwelling, it scores every zone using a **gravity model**:

```
score = attractiveness × exp(-β × distance) × visit_penalty × crowd_penalty
```

`β` (impedance) increases as the agent fatigues, so tired agents strongly prefer nearby booths. Attractiveness is boosted when zone topic tags match the agent's persona. Crowd penalty is a sigmoid — as density climbs past ~2 persons/m², the chance of entering drops smoothly to near zero. Hunger and thirst **override** all of this once they cross their thresholds, routing the agent to the nearest food or drink zone.

### Session Redirects
When a talk is scheduled to start, `SessionScheduleLoader` reroutes a subset of free agents to the session room. With `smoothSessionRedirects = true`, agents finish their current activity first (max 2-minute delay) before redirecting — no jarring teleports.

### Analytics & Validation
`AnalyticsManager` logs agent positions every 0.5 s, zone occupancy every 30 s, and flow counts every 60 s. On quit it writes the heatmap (CSV + false-colour PNG) and runs validation against the observed sensor data — computing RMSE, NRMSE, χ², and Pearson r. `CalibrationManager` reads those results and nudges the gravity β for the next run.

For full equations and parameter derivations, see `Pedestrian_Dynamics_Reference.md`.

---

## Running

1. Open in **Unity 6** (or 2022.3 LTS+).
2. Open `Assets/Scenes/SampleScene.unity`.
3. Set `timeScaleMultiplier` on `CrowdManager` (e.g. `60` = 1 conference minute per real second).
4. Press Play. Outputs write to `Assets/SimOutput/` on Stop.

All parameters are in `Assets/SimConfig.asset`. Fields can be overridden at runtime via `StreamingAssets/sim_config.json` without recompiling.
