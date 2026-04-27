# Codebase Concerns

**Analysis Date:** 2026-04-27

## Tech Debt

**AgentController size and complexity (HIGH)**
- Issue: Monolithic controller at 924 lines handles 6-state machine, SFM physics, persona logic, memory, physiology, groups, and LLM integration in a single class
- Files: `Assets/Scripts/AgentController.cs` (lines 1–924)
- Impact: Changes to any single behavior (e.g., fatigue model, roaming logic) require careful navigation through a massive file; hard to unit test individual behaviors; high risk of state mutation bugs
- Fix approach: Extract state behaviors into separate handler classes (`TransitBehavior`, `DwellingBehavior`, `RoamingBehavior`); move SFM calculation to `SocialForceCalculator` utility; isolate physiological logic into `PhysiologySystem`

**JSON parsing in LLM client (MEDIUM)**
- Issue: Custom string-based JSON parsing in `LLMConversationClient.ExtractContentText()` (lines 197–229) using `IndexOf()` and manual substring extraction instead of a JSON library
- Files: `Assets/Scripts/LLMConversationClient.cs` (lines 197–229)
- Impact: Brittle to API response format changes; will fail silently on malformed JSON; no validation of nested structure; difficult to debug; doesn't handle escaped quotes correctly in edge cases
- Fix approach: Use `Newtonsoft.Json` (already available in many Unity projects) or migrate to `System.Text.Json` (.NET 5+); add explicit schema validation; wrap API response parsing in try-catch with detailed logging

**Tight coupling between CrowdManager and AgentController (MEDIUM)**
- Issue: `AgentController` holds direct reference to `CrowdManager` and makes 40+ public calls to it (GetBoothDensity, GetZoneType, GetZoneTopics, MoveAgentToZone, etc.); any change to manager API breaks agents
- Files: `Assets/Scripts/AgentController.cs` (widespread), `Assets/Scripts/CrowdManager.cs` (widespread)
- Impact: Hard to test agents in isolation; increases chance of null-reference errors if manager not fully initialized; makes agent behavior non-portable
- Fix approach: Inject a minimal `IWorldState` interface providing only read-only zone/density/phase queries; move zone mutations to a separate `IAgentMovementController` service

**Unbounded memory in LLMConversationClient log (MEDIUM)**
- Issue: `_log` queue fixed at 200 entries (line 42) but unbounded conversation generation could still exceed memory if not properly capped in simulations with many agents
- Files: `Assets/Scripts/LLMConversationClient.cs` (lines 41–42)
- Impact: Long simulations with high LLM call rate could accumulate stale records; no TTL or auto-cleanup for old conversations
- Fix approach: Implement age-based eviction (remove entries older than X minutes of sim-time) or use ring buffer with explicit max capacity per minute

**Floating-point precision in time calculations (LOW)**
- Issue: SimClock accumulates as `float` across potentially hour-long simulations with 0.02s timesteps; cumulative rounding error could cause de-sync with CSV epoch after 1000s of ticks
- Files: `Assets/Scripts/CrowdManager.cs` (SimClock accumulation), `Assets/Scripts/DataLoader.cs` (EpochTime as DateTime)
- Impact: After 2+ hours of sim time, occupancy validation may diverge from CSV ground truth due to accumulated float precision loss
- Fix approach: Use `double` for SimClock instead of `float`, or track elapsed sim-time as integer milliseconds with periodic sync to DateTime

---

## Known Bugs

**Roaming state exit condition allows dwelling reentry (MEDIUM)**
- Symptoms: Agent in Roaming state can transition immediately back to Dwelling at the same zone via the random state exit logic (line 396–402 in AgentController)
- Files: `Assets/Scripts/AgentController.cs` (TickRoaming, lines 396–402)
- Trigger: When `_stateTimer` expires and the next random pick is Dwelling in the same zone
- Workaround: Agents eventually move on; no simulation blocker
- Root cause: State transitions don't check current zone before allowing re-entry to Dwelling; roaming is meant as a "browsing" phase, not a loop
- Fix approach: Track previous zone in state exit; if next zone == current zone, force gravity model pick instead

**Pending redirect can be lost if agent exits before redirect timer fires (MEDIUM)**
- Symptoms: If agent enters Exiting state before pending redirect timeout, the redirect goal is cleared (line 195 in ResetForPool), silently discarding the redirect
- Files: `Assets/Scripts/AgentController.cs` (ResetForPool, line 195; Exiting state)
- Trigger: Agent queues redirect, then session schedule ends while agent is still transiting → agent exits before redirect applies
- Workaround: Increase `redirectMaxDelay` in SimConfig to avoid timeouts during long transits
- Root cause: Redirect queue-and-apply design doesn't persist across agent lifecycle phases; should apply before reset
- Fix approach: Apply pending redirect in ExitState before transitioning to pool; or persist redirect in CrowdManager's next-spawn queue

**Division by zero in CalibrationManager zone bias (LOW)**
- Symptoms: If a zone has `meanObserved == 0`, line 90 will attempt division by zero (normalised bias calculation)
- Files: `Assets/Scripts/CalibrationManager.cs` (line 90)
- Trigger: A zone appears in CSV but has zero observed occupancy across entire experiment
- Workaround: Skip zones with occupancy < 1 (line 87 check)
- Root cause: Guard condition skips zones with `meanObs < 1f`, but comparison should use > or == 0 for clarity
- Fix approach: Explicitly check `meanObs <= 0` before division; add clarifying comment

**Heatmap bounds not validated against agent positions (LOW)**
- Symptoms: If agent wanders outside heatmap bounds (floorMin/floorMax), density calculation in AnalyticsManager will silently fail to record or cause array index out of bounds
- Files: `Assets/Scripts/AnalyticsManager.cs` (lines 87–89, 300+)
- Trigger: NavMesh includes areas outside the configured floor area
- Workaround: Ensure heatmap bounds encompass entire walkable area; use Scene gizmos to verify
- Root cause: No bounds checking in density grid indexing
- Fix approach: Add Mathf.Clamp in grid coordinate calculation; log warnings if agents found outside bounds

---

## Security Considerations

**API key exposure in SimConfig (HIGH)**
- Risk: SimConfig ScriptableObject holds `llmApiKey` as plain text in memory and potentially serialized in Unity project files; version control may accidentally include it
- Files: `Assets/Scripts/SimConfig.cs` (llmApiKey field), `Assets/Scripts/LLMConversationClient.cs` (line 235 fallback)
- Current mitigation: Code supports env variable fallback (`ANTHROPIC_API_KEY`), but doesn't warn if key is hardcoded in asset
- Recommendations:
  - Remove `llmApiKey` field from ScriptableObject; require environment variable or external secrets manager
  - Add Editor script to validate that no API key is present in SimConfig before Build
  - Document in README that `ANTHROPIC_API_KEY` is mandatory for LLM features
  - If key must be stored in Editor, use TextAsset in a .gitignored folder instead of serialized field

**CSV file injection risk (MEDIUM)**
- Risk: DataLoader parses CSV with no validation of cell content; zone_id strings are used directly as dictionary keys without sanitization
- Files: `Assets/Scripts/DataLoader.cs` (lines 146–157)
- Current mitigation: No filtering or validation of input
- Recommendations:
  - Add whitelist of allowed zone IDs (cross-check against ConferenceZone components)
  - Validate that zone_id matches pattern `^[A-Za-z0-9_-]{1,64}$`
  - Log a warning if CSV contains unknown zone_ids; skip those rows
  - Add schema validation: ensure enters/exits are non-negative, occupancy_snapshot > 0

**Unbounded memory growth in AnalyticsManager buffers (MEDIUM)**
- Risk: Spatial log buffer (`_spatialBuf`) is unbounded; if flush to disk fails (e.g., permissions), buffer will grow until OOM
- Files: `Assets/Scripts/AnalyticsManager.cs` (lines 50–67, 140+)
- Current mitigation: Flush every 5000 lines, but no error handling if flush fails
- Recommendations:
  - Add try-catch around File.AppendAllText; log error and clear buffer on failure
  - Pre-allocate buffer size limit (e.g., 1 MB); auto-flush when exceeded
  - Add telemetry: log flush frequency and buffer peak size

**LLM response timeout unbounded (LOW)**
- Risk: `CallClaudeAPI()` coroutine (line 160) uses `yield return request.SendWebRequest()` with no timeout; could hang indefinitely if network is slow
- Files: `Assets/Scripts/LLMConversationClient.cs` (line 184)
- Current mitigation: None
- Recommendations:
  - Set `request.timeout` to 10 seconds
  - Implement coroutine timeout wrapper; cancel request if no response after 15 seconds
  - Log warning if API is slow; disable LLM calls for remainder of sim if 3+ timeouts occur

---

## Performance Bottlenecks

**Gravity model zone selection: O(n) linear search per agent state change (MEDIUM)**
- Problem: PickDestinationZone() (line 682) iterates all zones every time agent decides to move; with 50+ zones and 300 agents, this is 15,000 iterations per second during roaming phases
- Files: `Assets/Scripts/AgentController.cs` (PickDestinationZone, lines 700–735)
- Cause: Weighted random selection via linear scan; no precomputation of zone weights
- Improvement path:
  - Pre-sort zones by distance once per day-phase change
  - Cache zone interest scores in CrowdManager (update only when zone topics change)
  - Use cumulative weight array for O(log n) binary search instead of O(n) linear scan

**NavMesh edge lookup in SFM: O(1) per agent, but 300 agents × 50 Hz = 15,000 calls/sec (MEDIUM)**
- Problem: `NavMesh.FindClosestEdge()` (line 622) called every FixedUpdate for every moving agent; no spatial caching
- Files: `Assets/Scripts/AgentController.cs` (ApplySocialForces, line 622)
- Cause: Wall repulsion is important, but querying NavMesh every frame is expensive on large meshes
- Improvement path:
  - Cache closest edge per agent; only update when agent has moved >0.5 m
  - Precompute wall repulsion zones as a separate spatial grid of NavMesh edges
  - Reduce update frequency to every 0.5s if acceptable for your simulation precision

**Spatial grid rebuild: O(n) per FixedUpdate where n = agent count (LOW)**
- Problem: CrowdManager rebuilds entire spatial grid every FixedUpdate (Grid.Clear() + Insert all agents); with 300 agents, this is 300 dictionary operations per tick
- Files: `Assets/Scripts/CrowdManager.cs` (spatial grid update), `Assets/Scripts/SpatialGrid.cs` (Clear/Insert)
- Cause: Simple implementation; no incremental updates
- Improvement path:
  - Track agent position delta; only re-insert agents that moved >cellSize distance
  - Use object pooling for List<AgentController> buckets to reduce GC
  - Consider quad-tree for better spatial coherence over time

**Heatmap aggregation: O(n) grid scan per sample interval (LOW)**
- Problem: Heatmap accumulation iterates all 50+ zones to sample density every 0.5 seconds; with 300 agents and 100×100 grid, this could be CPU-bound in long runs
- Files: `Assets/Scripts/AnalyticsManager.cs` (lines 140+)
- Cause: No spatial indexing; brute-force density lookup per grid cell
- Improvement path:
  - Cache zone occupancy counts in CrowdManager; query only affected zones per heatmap sample
  - Downsample heatmap grid to 1.0 m² cells instead of 0.5 m² (reduces 4× samples)
  - Lazy-evaluate heatmap; only write cells with agents within 2 m

---

## Fragile Areas

**Agent state machine: No state guards on transitions (MEDIUM)**
- Files: `Assets/Scripts/AgentController.cs` (TickTransit, TickDwelling, TickRoaming, TickSocializing, TickResting, TickExiting)
- Why fragile: Each state's Tick() assumes current state is correct; no assertion or guard. If a bug causes state confusion (e.g., multiple EnterState calls), behavior becomes undefined
- Safe modification:
  - Add `if (CurrentState != AgentState.Transit) return;` at start of each Tick method
  - Use a state machine library (e.g., Stateless) or enum with explicit allowed transitions
  - Unit test each state transition pair
- Test coverage: No unit tests for state transitions; 100% dependent on scene-based integration tests

**Redirect queue logic: Multiple pending redirect locations (MEDIUM)**
- Files: `Assets/Scripts/AgentController.cs` (QueueRedirect, ApplyPendingRedirect)
- Why fragile: Only one pending redirect is tracked (`_pendingRedirectGoal`, `_pendingRedirectZoneId`); if two redirects are queued, the first is silently replaced
- Safe modification:
  - Use a Queue<(Transform goal, string zoneId)> instead of single pending slot
  - Apply oldest redirect first (FIFO discipline)
  - Test with SessionScheduleLoader redirecting agents during transit
- Test coverage: No test for multiple rapid redirects

**Persona configuration: Hardcoded in SimConfig (LOW)**
- Files: `Assets/Scripts/SimConfig.cs` (personas array, lines 116–151)
- Why fragile: Adding a new persona type requires:
  1. Edit SimConfig array
  2. Update PersonaConfig struct if new fields
  3. Ensure spawn weights sum to 1.0
  4. Update CrowdManager persona selection (line 152 cumulative weights)
  5. Update AgentColorizer if new persona needs a color
- Safe modification:
  - Load personas from ScriptableObject array asset instead of hardcoded struct array
  - Validate spawn weights sum to ~1.0 on load; log warning if not
  - Add persona registry utility that auto-discovers PersonaConfig ScriptableObjects
- Test coverage: No validation of spawn weight sum

**LLM disabled without warning (LOW)**
- Files: `Assets/Scripts/LLMConversationClient.cs` (lines 78–79)
- Why fragile: If `config.llmEnabled == false`, RequestConversation() silently returns without logging; developer may think LLM is broken, not disabled
- Safe modification:
  - Log.Log(Level.Info) on Start if LLM is disabled
  - Add a public `IsEnabled` property so HUD/tests can query state
  - Require explicit opt-in in SimConfig with checkbox label explaining performance cost
- Test coverage: No test that LLM is actually being invoked during agent conversations

---

## Scaling Limits

**Agent count ceiling: ~500 agents (300 currently tested) (MEDIUM)**
- Current capacity: Tested to 300 agents; spatial grid and SFM forces work well
- Limit: At ~500 agents:
  - Spatial grid neighbor queries become O(k²) dominated (k = ~16 neighbors per cell)
  - NavMesh pathfinding becomes noticeably slower
  - FindClosestEdge() wall repulsion scales with agent density (more calls)
  - Unity physics (if using rigidbodies) becomes a bottleneck
- Scaling path:
  - Profile with 500 agents; identify frame time bottlenecks (use Unity Profiler)
  - Switch from NavMeshAgent pathfinding to pre-computed waypoint graphs for static routes
  - Implement level-of-detail (LOD) for distant agents: reduce SFM frequency, remove wall forces, static paths
  - Split agents across multiple CrowdManagers (spatial sharding) if >1000 agents needed

**Zone count ceiling: ~100 zones (50 currently) (MEDIUM)**
- Current capacity: Tested with 20–50 zones; zone selection completes in <1 ms
- Limit: At ~100 zones:
  - PickDestinationZone() linear scan becomes 100 weight calculations per agent state change
  - Heatmap burns more memory (depends on grid resolution)
  - CSV parsing time increases (O(n) rows)
- Scaling path:
  - Pre-sort zones spatially (k-d tree or grid); limit zone selection to nearest 10–15
  - Cache zone weights per persona/fatigue level (memoization)
  - Compress heatmap to 1.0 m² cells instead of 0.5 m²; or use sparse matrix instead of dense array

**CSV duration: 10+ hours of sim time (MEDIUM)**
- Current capacity: Tested with 6 hours of real data; no memory leaks observed
- Limit: At 12+ hours:
  - Accumulated float precision loss in SimClock (see Floating-point precision concern above)
  - AnalyticsManager buffers may grow if disk I/O is slow
  - Calibration report JSON grows unbounded (one entry per simulation)
- Scaling path:
  - Switch SimClock to double, or track time as long milliseconds + DateTime epoch
  - Flush analytics buffers every 1000 lines instead of 5000 (trade memory for more I/O)
  - Archive calibration history: keep only last 5 runs in live memory, older runs in timestamped files

**Concurrent LLM calls: Rate-limited to config.llmCallsPerSimMinute (LOW)**
- Current capacity: Configured at 10 calls per sim-minute by default (line 118 check)
- Limit: If 30+ agents are socializing simultaneously and budget is 10/min, 20 calls are silently dropped
- Scaling path:
  - Log a warning if budget is exceeded; suggest increasing llmCallsPerSimMinute or disabling LLM for large crowds
  - Batch LLM calls: collect all socializing pairs, make one call with multiple prompts (if API supports it)
  - Implement async queue: dropped calls are retried on next minute, not lost

---

## Dependencies at Risk

**Unity NavMesh API: No fallback path (MEDIUM)**
- Risk: If NavMesh is missing or corrupted, `NavMesh.SamplePosition()` and `NavMesh.FindClosestEdge()` will fail silently
- Impact: Agents will not move; they'll return Zero velocity; scene appears frozen
- Mitigation in code: SimValidator checks for NavMesh at startup (line 176)
- Migration plan: If NavMesh becomes unavailable in future Unity versions:
  - Implement simple grid-based pathfinding as fallback
  - Use pre-computed visibility graphs instead of dynamic pathfinding
  - Switch to Recast NavMesh plugin (third-party) with more configuration options

**Anthropic Claude API: No graceful degradation (MEDIUM)**
- Risk: If API goes offline or changes response format, LLM features break but simulation continues
- Impact: Agents socializing will have no generated conversations; validation may fail if expected field is missing
- Mitigation in code: Try-catch in ExtractContentText (line 200); callback receives null on error
- Migration plan:
  - Implement mock LLM client that generates placeholder conversations
  - Cache successful API responses locally; use cache as fallback
  - Add feature flag to disable LLM in code; don't retry if consecutive failures exceed N

**Weidmann/Helbing parameters: Not tuned to EASL (LOW)**
- Risk: SFM parameters (A=2000, B=0.08, etc.) are from 1995 literature; may not match EASL attendee behavior
- Impact: Simulated flow patterns may not match observed Posterbuddy sensor data
- Mitigation in code: CalibrationManager adjusts zone gravity bias (lines 85–98) to fit observed data
- Migration plan:
  - Run sensitivity analysis: vary A, B, lambda by ±20%; measure RMSE change
  - Collect inverse-calibration baseline: given observed occupancy, what SFM params would produce it?
  - If RMSE doesn't improve below 15% after calibration, document as empirical limitation

---

## Missing Critical Features

**No inter-zone routing / exit routing (MEDIUM)**
- Problem: Agents pick zones via gravity model but have no pre-computed route; NavMeshAgent finds path dynamically every frame
- Blocks: Cannot model congestion on corridors (only zones); cannot predict agent arrival times
- Implementation: Pre-compute shortest path graphs between all zone pairs; use A* with a pre-built road network instead of dynamic pathfinding

**No session attendance prediction (MEDIUM)**
- Problem: SessionScheduleLoader fires redirects at session start/end, but doesn't know how many agents will actually attend
- Blocks: Cannot validate if a session room capacity is exceeded; cannot model standing-room-only scenarios
- Implementation: Query agents for attendance intent; cap redirect count to room capacity; model queue outside room

**No persistent agent memory across respawns (LOW)**
- Problem: When agent is reset to pool, all memory is cleared; no carry-over of learned preferences
- Blocks: Agents don't "remember" which booths were boring after respawn; no learning-curve realism
- Implementation: Persist `_visitedZones`, `_preferredTopics` usage history to a persistent agent profile; hydrate on spawn

**No accessibility modeling (LOW)**
- Problem: All agents move at same base speed; no elderly, mobility-impaired, or parent-with-stroller personas
- Blocks: Cannot model real-world bottlenecks caused by slower agents; cannot test accessible routing
- Implementation: Add PersonaConfig fields for max_speed override, stopping_frequency, and accessibility_route_preference

---

## Test Coverage Gaps

**AgentController state machine not unit-tested (HIGH)**
- What's not tested: Individual state transitions (Transit→Dwelling, Dwelling→Roaming, etc.), dwell time sampling, socializing contagion
- Files: `Assets/Scripts/AgentController.cs` (all state methods)
- Risk: Changes to state logic can introduce silent behavioral bugs; easy to break transition conditions
- Priority: HIGH — foundational behavior, affects all agents
- Mitigation: Add MonoBehaviour unit tests with mock CrowdManager; test each state's Tick() in isolation

**Spatial grid correctness not validated (MEDIUM)**
- What's not tested: Neighbor queries with various agent densities, edge cases (agent on cell boundary, query radius at exact cell size)
- Files: `Assets/Scripts/SpatialGrid.cs`
- Risk: Silent bugs in neighbor lookup could cause agents to miss nearby obstacles
- Priority: MEDIUM — correctness is high-impact, but current implementation is simple enough to visually inspect
- Mitigation: Add unit tests for GetNeighbors() with synthetic agent placement; verify false positives/negatives

**CSV parsing error handling not tested (MEDIUM)**
- What's not tested: Malformed rows, missing columns, invalid dates, negative counts
- Files: `Assets/Scripts/DataLoader.cs` (lines 132–165)
- Risk: Bad CSV can silently produce empty TimeSliceQueue or misaligned data; simulation appears to run but with no spawns
- Priority: MEDIUM — easy to miss bad CSV format before running long experiment
- Mitigation: Add integration test with sample malformed CSVs; verify warnings are logged and safe defaults applied

**Calibration math not unit-tested (MEDIUM)**
- What's not tested: P-controller bias calculation, weight normalization, edge cases (zero bias, extreme adjustment)
- Files: `Assets/Scripts/CalibrationManager.cs` (lines 82–98)
- Risk: Calibration tuning could oscillate or diverge if learning rate is wrong; no way to validate without full simulation
- Priority: MEDIUM — only used in post-run analysis, but easy to break with typo
- Mitigation: Add unit tests for bias calculation logic; test with synthetic validation reports

**Persona spawn distribution not validated (MEDIUM)**
- What's not tested: Spawn weight distribution; verify 1000 agents spawn in expected persona ratios
- Files: `Assets/Scripts/CrowdManager.cs` (persona selection logic)
- Risk: If persona cumulative weights are calculated wrong (rounding errors), distribution drifts away from config
- Priority: MEDIUM — affects statistical validity of results
- Mitigation: Add test that spawns 1000 agents, verifies distribution matches config weights ±3%

**LLM integration tests missing (MEDIUM)**
- What's not tested: API connectivity, response parsing, budget enforcement, callback invocation
- Files: `Assets/Scripts/LLMConversationClient.cs`
- Risk: LLM features could be silently broken; developers would only notice if they enable llmEnabled=true and watch HUD
- Priority: MEDIUM — only used if explicitly enabled, but silent failure is bad for debugging
- Mitigation: Add mock API test; test with real API (mock only for CI; real key in local test); validate budget is enforced

**Heatmap accumulation accuracy not verified (LOW)**
- What's not tested: Heatmap cell assignment for agents on cell boundaries, density sampling frequency error
- Files: `Assets/Scripts/AnalyticsManager.cs` (heatmap logic, ~lines 300+)
- Risk: Heatmap may undercount density in grid cells due to sampling bias
- Priority: LOW — visualization only; not critical for simulation logic
- Mitigation: Add test that spawns 100 agents at known positions, samples heatmap, verifies counts ±1

---

## Documentation Gaps

**No math reference document (MEDIUM)**
- Missing: Formal definitions of Helbing SFM equations, fatigue model derivation, gravity model weights, lognormal dwell distribution
- Impact: New developers can't verify SFM implementation against literature; hard to justify parameter choices
- Recommendation: Create `/docs/MATH_MODELS.md` with LaTeX equations, parameter sources, and calibration procedure

**No behavioral flow diagrams (MEDIUM)**
- Missing: Flowchart of agent state machine, zone selection decision tree, session redirect queuing logic
- Impact: Hard to understand why agents transition between states; difficult to debug state-related bugs
- Recommendation: Create Mermaid diagrams in `/docs/BEHAVIOR.md` showing all state paths and conditions

**No profiling guide (LOW)**
- Missing: Which metrics to measure (FPS, agent frame time, pathfinding cost), how to use Unity Profiler for this project
- Impact: Contributors don't know how to measure if their changes regressed performance
- Recommendation: Create `/docs/PROFILING.md` with frame budget targets and common profiling workflows

**Persona configuration lacks examples (LOW)**
- Missing: Guidance on how to create and tune new personas; what multiplier ranges are realistic
- Impact: Hard to add domain-specific personas (e.g., "conference organizer") without guessing multiplier values
- Recommendation: Add SimConfig field tooltips with reference values from literature (e.g., "Networker socializeMult=1.8 from Granovetter weak-tie theory")

---

*Concerns audit: 2026-04-27*
