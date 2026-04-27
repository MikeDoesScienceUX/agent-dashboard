# Testing Patterns

**Analysis Date:** 2026-04-27

## Current Test Infrastructure

**Status:** No formal unit testing framework detected. No NUnit, xUnit, or UnityTest Framework integration found.

**Rationale:** This is a real-time simulation project (requires FixedUpdate, Physics, NavMesh) that prioritizes:
- Play-mode validation over unit tests (cannot unit test agent pathfinding without NavMesh)
- CSV-driven validation against ground-truth sensor data (AnalyticsManager)
- Integration tests via Editor scripts and simulation runs
- Manual scene testing with Inspector parameter tweaking

## Test Organization

**No .Tests directory or test assembly definitions (`.asmdef`) found.**

**Editor-Only Validation Tools** (substitute for unit tests):
- `Assets/Scripts/Editor/ConferenceSetupWindow.cs` — Multi-tab wizard validates:
  - CSV data schema and zone ID matches
  - ConferenceZone objects discovered in scene
  - SimConfig parameters loaded and applied
  - Session schedule consistency
  - Potential issues flagged with color-coded messages (green/yellow/red)

- `Assets/Scripts/Editor/ConferenceSceneTool.cs` — Scene setup automation
- `Assets/Scripts/Editor/ConferenceZoneEditor.cs` — Visual polygon editing
- `Assets/Scripts/Editor/NavMeshBaker.cs` — NavMesh generation validation
- `Assets/Scripts/Editor/SceneAutoSetup.cs` — Automated object wiring

**No dedicated test scripts** in the project structure. These Editor tools serve as setup validation rather than test cases.

## Validation Strategy

**Play-Mode Validation (Primary):**

1. **CSV-Driven Ground Truth** (`AnalyticsManager`):
   ```
   Assets/StreamingAssets/sensor_data.csv (Posterbuddy sensor headcounts)
                                    ↓
                         AnalyticsManager logs simulation outputs
                                    ↓
   Assets/SimOutput/validation_report.json
   - RMSE (Root Mean Squared Error)
   - NRMSE (Normalized RMSE)
   - χ² (Chi-squared goodness of fit)
   - Pearson r (correlation coefficient)
   ```

   Run simulation → compare zone occupancies against CSV ground truth → compute error metrics.

2. **Heatmap Visualization** (`AnalyticsManager`):
   ```
   Assets/SimOutput/heatmap.csv (density per 0.5 m² cell)
   Assets/SimOutput/heatmap.png (false-color density visualization)
   ```

   Visual inspection of crowd distribution patterns against expected layout.

3. **Time Series Logs** (`AnalyticsManager`):
   ```
   Assets/SimOutput/spatial_log.csv (agent positions + state every 0.5s)
   Assets/SimOutput/zone_timeseries.csv (zone occupancy every 30s)
   Assets/SimOutput/flow_metrics.csv (enters/exits per zone per minute)
   ```

   Python/R analysis scripts can post-process these for statistical validation.

4. **Flow Sensor Validation** (`FlowSensorTrigger`):
   Collider-based entry/exit counting compared against sensor CSV data.

## Test Execution Workflow

**Manual Play-Mode Validation:**

1. Open `Assets/Scenes/SampleScene.unity`
2. In Inspector, set `CrowdManager.timeScaleMultiplier` (e.g., 60 = fast replay)
3. Click Play
4. Observe HUD (SimulationHUD):
   - Real-time occupancy per zone
   - Agent state distribution (transit/dwelling/roaming/socializing/resting/exiting)
   - Current day phase (morning/midday/afternoon)
   - Fatigue factor, active agents
5. Click Stop
6. Check `Assets/SimOutput/`:
   - `validation_report.json` shows RMSE vs. ground truth
   - `simulation_summary.json` shows peak occupancy, average dwell time, etc.
   - `heatmap.png` shows crowd density distribution

**Editor Validation Workflow:**

1. Open `Window > Conference Sim > Zone Setup` (via `ConferenceSetupWindow`)
2. Navigate tabs:
   - **① Wizard**: One-click setup buttons (create zones, set SimConfig, load CSV)
   - **② Zones**: Add/edit/remove ConferenceZone objects, visualize in Scene
   - **③ CSV Data**: Validate sensor CSV schema, preview rows, check zone ID matches
   - **④ Config**: Review/create SimConfig, adjust time scale, parameter presets
3. Each tab shows color-coded status (green = OK, yellow = warning, red = error)

**Example Tab Feedback:**
```
✓ CSV file loaded: 1200 rows, 5 zones
✓ All zone IDs match ConferenceZone objects
⚠ Session schedule: 3 missing zones
✗ Time scale not set (defaults to 1.0)
```

## Test Coverage Assessment

**Well-Tested Areas** (via play-mode validation):

- **Agent State Machine**: Each state transition tested via visual inspection (color coding in scene)
  - Transit → pathfinding via NavMesh
  - Dwelling → dwell timer countdown
  - Roaming → Ornstein-Uhlenbeck heading
  - Socializing → contagion within groups
  - Resting → fatigue recovery
  - Exiting → object pool return

- **Social Force Model**: Verified against ground truth zone occupancies (RMSE metric)
  - Driving force, agent repulsion, wall repulsion computed each frame
  - Spatial grid neighbor queries (O(n) not O(n²)) validated implicitly

- **CSV Spawning**: Poisson-process agent generation compared to sensor data
  - Enters/exits per zone per minute logged
  - Flow metrics vs. observed spawn rate

- **Persona System**: Agent behavior differentiation by type
  - Researcher (slow, dwells long)
  - Networker (fast, socializes often)
  - Student (high energy, low visit penalty)
  - Industry (networking-focused)
  - BoothStaff (stationary)

- **Session Redirects**: Smooth queuing tested via `smoothSessionRedirects` flag
  - Agents complete current activity before redirecting
  - Max delay enforced (2 minutes default)

**Untested/Partially Tested Areas** (would require unit tests):

- **Gravity Model Weights**: Zone selection algorithm (`PickDestinationZone()`) computed but not directly validated
  - *Risk:* Incorrect weighting could cause agents to avoid certain zones
  - *Current validation:* Implicitly tested via occupancy RMSE (if weights wrong, RMSE high)
  - *Gap:* No isolated unit test of interest/distance/fatigue/visit-penalty calculations

- **Fatigue Function**: Speed degradation formula `v(t) = v₀ · [α + (1−α) · exp(−t/T)]`
  - *Risk:* Typo in exponent could break fatigue dynamics
  - *Current validation:* Visual inspection of agent slowing over time
  - *Gap:* No direct test of exponential decay math

- **Physiological Drives**: Hunger/thirst accumulation and zone overrides
  - *Risk:* Drive thresholds incorrect or not properly triggering zone redirects
  - *Current validation:* Manual observation of agents seeking food/drink zones
  - *Gap:* No automated test of drive state progression

- **Group Cohesion**: Centroid calculation and contagion probability
  - *Risk:* Centroid computation could fail with null members or inactive agents
  - *Current validation:* Groups spawn correctly, members stay near centroid (visual)
  - *Gap:* No unit test of GetCentroid() edge cases (empty group, null member pointers)

- **LLM Conversation Client**: Async API call, conversation generation
  - *Risk:* Network failure, malformed response, timeout not handled
  - *Current validation:* Debug log shows conversation topics, but no error cases tested
  - *Gap:* No error handling tests for network failures

- **Editor Tools**: Wizard tabs, CSV validation, auto-wiring
  - *Risk:* Import bugs (wrong zone ID format, missing columns)
  - *Current validation:* Manual wizard walkthrough
  - *Gap:* No automated tests for CSV parsing edge cases (empty files, malformed dates, duplicates)

## How Quality is Validated

**Primary Validation Method: Simulation vs. Ground Truth**

```
sensor_data.csv (real conference data)
        ↓
    Sim runs
        ↓
simulation_summary.json + zone_timeseries.csv
        ↓
    Compute RMSE, NRMSE, χ², Pearson r
        ↓
validation_report.json
```

**Acceptable Thresholds:**
- RMSE < 10% of peak occupancy
- Pearson r > 0.85
- χ² p-value > 0.05 (not significantly different)

If validation fails, `CalibrationManager` nudges the gravity model β and reruns.

**Manual Scene Inspection:**

1. **Visual Behavior Check**: Agents should form lanes in corridors, cluster at booths, avoid overcrowding
2. **HUD Telemetry**: Zone occupancies, agent state distribution, day phases should match expected patterns
3. **Performance**: 300 agents + physics + pathfinding should run at 60+ FPS on desktop

**Output File Inspection** (post-simulation):

```bash
python3 analyze_validation.py Assets/SimOutput/validation_report.json
# Plot: simulated vs. observed occupancy over time
# Statistics: RMSE, correlation, KL divergence
```

## Known Testing Gaps

**Critical Gaps (would block production deployment):**

1. **No unit test for agent pool state tracking**
   - Risk: Pool corruption (agents spawned but never returned, or returned twice)
   - Workaround: Observe agent count on HUD doesn't spike unexpectedly
   - Fix: Implement NUnit test suite with mock CrowdManager

2. **No test for NavMesh path failure recovery**
   - Risk: Agent stuck on unreachable NavMesh island
   - Workaround: Scene layout carefully designed to avoid islands
   - Fix: Add fallback path planning or silent agent return-to-pool

3. **No test for CSV parsing edge cases**
   - Risk: Malformed CSV (missing columns, wrong date format) crashes on load
   - Workaround: CSV manually validated before run
   - Fix: Robust CSV parser with error recovery in DataLoader

4. **No thread safety tests for spatial grid**
   - Risk: Race condition if agents query grid while it's being rebuilt
   - Workaround: Single-threaded execution (FixedUpdate order strictly controlled)
   - Fix: Lock grid during rebuild or use copy-on-write structure

**Nice-to-Have Improvements:**

- Unit tests for SpatialGrid (edge cases: agents at cell boundary, grid at origin)
- Unit tests for zone selection gravity model weighting
- Fuzzing tests for CSV parsing (random valid/invalid inputs)
- Integration tests for multi-session redirects (session_schedule.csv with overlapping times)
- CI/CD pipeline (GitHub Actions) to run full sim + validation on each commit

## CI/CD Integration

**Current:** None detected. No GitHub Actions, Jenkins, or build server configuration.

**Manual Workflow:**
1. Developer modifies physics parameters in `SimConfig.asset`
2. Opens scene, clicks Play, observes behavior
3. Checks `validation_report.json` after run
4. If RMSE acceptable, commits changes

**Recommended Setup (not yet implemented):**
```yaml
# .github/workflows/validate.yml
on: [push]
jobs:
  simulate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v2
      - uses: game-ci/unity-test-runner@v2
        with:
          projectPath: .
          testMode: playMode
      - name: Run full simulation
        run: ./run_sim.sh 60 # timeScale 60
      - name: Check validation_report.json
        run: python3 check_rmse.py SimOutput/validation_report.json
```

Not currently in place. Would require automated Unity headless builds and Python validation scripts.

## Test Framework Recommendations

**Why No Unit Tests Currently:**

1. Simulation is inherently real-time (FixedUpdate timing matters)
2. Physics (NavMesh, Rigidbody colliders) require play-mode
3. Focus is on aggregate metrics (occupancy RMSE) not individual agent paths
4. Play-mode + CSV validation provides sufficient confidence for research

**If Unit Tests Were Added:**

Would use **Unity Test Framework (UTF)** with NUnit:
```csharp
[Test]
public void TestSpatialGridInsert()
{
    var grid = new SpatialGrid(5f);
    var agent = new GameObject().AddComponent<AgentController>();
    grid.Insert(agent);
    
    var results = new List<AgentController>();
    grid.GetNeighbors(agent.transform.position, 5f, results);
    
    Assert.That(results, Contains.Item(agent));
}
```

Would test:
- SpatialGrid edge cases (boundary inserts, radius queries)
- DataLoader CSV parsing (valid/invalid/edge case rows)
- SimConfig value ranges (speed > 0, angles normalized)
- AgentGroup centroid calculation (empty, single member, inactive members)

---

*Testing analysis: 2026-04-27*
