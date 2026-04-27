# Coding Conventions

**Analysis Date:** 2026-04-27

## Naming Patterns

**Files:**
- Pascal case for script names: `AgentController.cs`, `CrowdManager.cs`, `SimConfig.cs`
- One public class per file (with occasional nested structs for data containers)
- Editor-only scripts in `Assets/Scripts/Editor/` subdirectory: `ConferenceSetupWindow.cs`, `NavMeshBaker.cs`

**Classes:**
- Pascal case: `AgentController`, `CrowdManager`, `DataLoader`, `AnalyticsManager`
- Concrete MonoBehaviour suffixes reflect purpose: `Manager`, `Controller`, `Loader`, `Trigger`
- Data containers: `SimConfig` (ScriptableObject), `PersonaConfig`, `AgentGroup`, `ConferenceZone`

**Methods:**
- Pascal case for public methods: `Initialize()`, `TickTransit()`, `EnterState()`, `GetZoneInterest()`
- Camel case for private methods: `ApplySocialForces()`, `BuildAgenda()`, `CheckPhysiologicalOverride()`
- Property accessors use expression-body style: `public NavMeshAgent Nav => _nav;`
- Method names reflect state changes or queries: `EnterState()`, `ResetForPool()`, `HasPendingRedirect()`, `GetNearestExit()`

**Variables & Fields:**
- Private fields use underscore prefix: `_config`, `_nav`, `_neighbours`, `_personalSpeed`, `_visitedZones`
- Public properties (rare): `CurrentState`, `TargetSensorId`, `PersonaName`, `IsLoaded`
- Camel case for local variables: `dt`, `fatigueFactor`, `density`, `interest`, `visitPenalty`
- Booleans prefix with `is` or `has`: `_hasTransitWaypoint`, `_llmPending`, `IsActive`
- Collections use plural or descriptive names: `_neighbours`, `_exitPoints`, `_visitedZones`, `_agendaQueue`

**Enums:**
- Pascal case type name: `AgentState`, `FindObjectsSortMode`
- Pascal case members: `Transit`, `Dwelling`, `Roaming`, `Socializing`, `Resting`, `Exiting`

**Constants:**
- All caps with underscores: `_ColorId`, `_BaseColorId` (shader property IDs)
- Magic numbers embedded as named constants in SimConfig fields with `[Tooltip]` descriptions
- Example: `socialForceA = 2000f` with tooltip `"Repulsive interaction strength N. Helbing (2000): 2000"`

## Code Style

**Formatting:**
- 4-space indentation (standard C# convention)
- Opening braces on same line (Allman style close braces): `void Foo() { ... }`
- Line length typically 80–100 characters (relaxed for long formula comments)
- Blank lines separate logical sections within methods

**Linting:**
- No automated linter detected (Visual Studio / Rider defaults assumed)
- Code follows standard C# style guide: CamelCase, braces, whitespace
- XML documentation comments on public classes and methods

**Comments & Documentation:**

**Summary blocks (extensive use):**
```csharp
/// <summary>
/// Per-agent simulation controller.
/// Physics: Helbing Social Force Model (driving + agent repulsion + wall repulsion + group cohesion)
/// Behaviour: 6-state machine (Transit → Dwelling → Roaming → Socializing → Resting → Exiting)
/// ...
/// </summary>
```

All public classes have comprehensive XML `<summary>` blocks explaining purpose, physics models, state machines, and key algorithms.

**Inline comments (strategic use):**
```csharp
// ── Driving force:  F = m·(v₀·ê₀ − v) / τ ─────────────
_desiredDir = _nav.hasPath && _nav.path.corners.Length > 1
    ? (_nav.path.corners[1] - pos).normalized
    : (_nav.destination - pos).normalized;
```

Comments appear before significant algorithm blocks and explain the mathematical model or complex logic.

**Section headers:**
```csharp
// ── State Machine ────────────────────────────────────────────────
// ── Private ──────────────────────────────────────────────────────
// ── Visual state color ───────────────────────────────────────────
// ── Initialisation ───────────────────────────────────────────────
// ── Main Loop ────────────────────────────────────────────────────
```

Large scripts use ASCII dividers (`──`) to separate logical regions (state, lifecycle, physics, utilities).

## Import Organization

**Order (strictly followed):**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
```

1. System namespaces (System, System.Collections.Generic, System.IO, etc.)
2. Unity namespaces (UnityEngine, UnityEngine.AI, UnityEditor)
3. No custom namespaces used in this project

**Path Aliases:**
- No custom aliases detected
- All scripts in `Assets/Scripts/` reference each other directly by class name (no relative paths)
- Editor scripts automatically excluded from runtime builds via `/Editor` subdirectory convention

## Attribute Usage

**Component Registration:**
```csharp
[RequireComponent(typeof(NavMeshAgent))]
[DefaultExecutionOrder(0)]
[AddComponentMenu("Conference Sim/Agent Controller")]
public class AgentController : MonoBehaviour
```

- `[RequireComponent]`: Declares runtime dependencies on other components
- `[DefaultExecutionOrder]`: Controls execution timing (CrowdManager = -10, AgentController = 0)
- `[AddComponentMenu]`: Organizes "Add Component" menu under "Conference Sim" category

**ScriptableObject Creation:**
```csharp
[CreateAssetMenu(fileName = "SimConfig", menuName = "Conference Sim/SimConfig")]
public class SimConfig : ScriptableObject
```

- Enables "Assets > Create > Conference Sim > SimConfig" menu in Editor

**Inspector Organization:**
```csharp
[Header("Social Force Model")]
[Tooltip("Free-flow walking speed m/s. Weidmann (1993): 1.34 ± 0.26")]
public float desiredSpeed = 1.34f;

[Range(0f, 1f)]
public float anisotropyLambda = 0.50f;

[HideInInspector]
public List<Vector2> polyPoints = new List<Vector2>();
```

- `[Header]`: Groups related fields with visual separators
- `[Tooltip]`: Documents parameter purpose and academic source (e.g. "Helbing (1995): 0.5")
- `[Range]`: Constrains float/int fields to min/max bounds
- `[HideInInspector]`: Prevents internal data from cluttering Inspector

**Editor-Only Code:**
```csharp
#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        // Scene view visualization only
    }
#endif
```

Conditional compilation isolates Editor-only features from runtime code.

## Architecture & Design Patterns

**MonoBehaviour Hierarchy:**

1. **Managers** (singleton-pattern via FindFirstObjectByType):
   - `CrowdManager`: Owns simulation clock, object pool, spatial grid, spawning queue
   - `AnalyticsManager`: Logs positions, heatmaps, validation reports
   - `DataLoader`: Parses CSV and exposes TimeSliceQueue
   - `LLMConversationClient`: Static Instance singleton for async API calls

2. **Controllers** (per-entity, attached to prefab):
   - `AgentController`: Per-agent state machine, physics, decision-making
   - `FlowSensorTrigger`: OnTriggerEnter/Exit callbacks

3. **Data Holders** (no logic, pure data):
   - `ConferenceZone`: Zone metadata (ID, type, topics, spawn point)
   - `AgentGroup`: Lightweight group record (list of members, cohesion)
   - `SimConfig`: ScriptableObject with all tunable parameters
   - `SpatialGrid`: Spatial hash data structure

**State Machine Pattern:**

```csharp
public enum AgentState { Transit, Dwelling, Roaming, Socializing, Resting, Exiting }

void FixedUpdate() {
    switch (CurrentState) {
        case AgentState.Transit:     TickTransit(dt);         break;
        case AgentState.Dwelling:    TickDwelling(dt);        break;
        case AgentState.Roaming:     TickRoaming(dt, ff);     break;
        case AgentState.Socializing: TickSocializing(dt);     break;
        case AgentState.Resting:     TickResting(dt);         break;
        case AgentState.Exiting:     TickExiting();           break;
    }
}

private void EnterState(AgentState next) {
    CurrentState = next;
    ApplyStateColor(next);
    // ... state-specific setup
}
```

- Enum defines all possible states
- `switch` statement in main loop delegates to Tick* methods
- `EnterState()` centralizes entry logic (setup, animations, timers)
- Each state's exit is implicit (next state's entry is explicit)

**Object Pooling Pattern:**

```csharp
private Queue<GameObject> _pool = new Queue<GameObject>();

GameObject agent = _pool.Count > 0 ? _pool.Dequeue() : Instantiate(agentPrefab);
agent.SetActive(true);
GetComponent<AgentController>().Initialize(...);
```

- Pre-warm pool at Start (300 agents by default)
- Dequeue when needed, activate, initialize with parameters
- Return to pool via `ReturnToPool()` instead of Destroy

**Spatial Hashing for Neighbor Queries:**

```csharp
_manager.Grid.GetNeighbors(pos, 5f, _neighbours);
foreach (var other in _neighbours) { /* SFM calculation */ }
```

- O(1) insertion, O(k) queries instead of O(n²) brute force
- CrowdManager rebuilds grid each FixedUpdate, clears old entries
- Agents query within 5m radius for social force interactions

**Data Flow Pattern:**

```
CSV → DataLoader.TimeSliceQueue → CrowdManager.Spawn → AgentController.Initialize
                                                              ↓
                                                        AgentController.FixedUpdate
                                                              ↓
                                                        AnalyticsManager.LateUpdate (logs)
```

- CSV parsed once at Start, dequeued during simulation
- Each agent initialized with persona, zone, group
- Managers execute in order: CrowdManager (-10) → Agents (0) → Analytics (late)

## Error Handling

**Strategy:** Silent fallbacks with optional Debug.Log warnings

**Patterns:**

```csharp
if (config == null) {
    config = ScriptableObject.CreateInstance<SimConfig>();
    Debug.LogWarning("[CrowdManager] No SimConfig assigned — using defaults.");
}

if (agentPrefab == null) {
    Debug.LogError("[CrowdManager] agentPrefab not assigned.");
    // ... continue with fallback
}

// Null-safe property access
public string PersonaName => (_config != null && _config.personas != null &&
                              PersonaIndex < _config.personas.Length)
                              ? _config.personas[PersonaIndex].name : "Unknown";

// Guard clauses for early exit
if (_goalTransform == null) return;
if (string.IsNullOrEmpty(driveZone)) return false;

// Safe array/list access
if (idx < 0 || _config.dayPhases == null || idx >= _config.dayPhases.Length) return 1f;
```

- No exceptions thrown (simulation-critical: exceptions would crash real-time loop)
- Null checks on every external reference
- Fallback values (empty string, 1.0f multiplier, Unknown) for graceful degradation
- Debug.Log with `[ClassName]` prefix for filtering

**Validation:**

```csharp
_hunger = Mathf.Clamp01(_hunger);  // Clamp to [0,1]
_personalSpeed = Mathf.Clamp(_personalSpeed, 0.5f, 2.5f);  // Clamp to range
```

Values clamped before use to prevent NaN/Inf in physics calculations.

## Logging

**Framework:** Console.log (built-in Debug.Log)

**Patterns:**

```csharp
Debug.Log($"[LLM] Agent {PersonaName} at {TargetSensorId}: \"{topic}\"");
Debug.LogWarning("[CrowdManager] No SimConfig assigned — using defaults.");
Debug.LogError("[CrowdManager] agentPrefab not assigned.");
```

- Prefix with `[ClassName]` for filtering in Console
- Log on significant events: state changes, spawning, errors
- No spam logging in tight loops (e.g., FixedUpdate position tracking deferred to AnalyticsManager)

## Function Design

**Size Guidelines:**
- Tight helper methods (5–15 lines): `GetNearestExit()`, `HasPendingRedirect()`, `Sigmoid()`
- Medium state tick methods (30–50 lines): `TickDwelling()`, `TickRoaming()`
- Large controllers (600+ lines): `AgentController.cs` (justified by complex physics + state machine)

**Parameters:**
- Initialization methods accept multiple parameters bundled as pass-by-reference when possible
- Example: `Initialize(SimConfig cfg, Transform goal, string sensorId, float avgDwellSec, ...)`
- Keeps agent initialization explicit and traceable

**Return Values:**
- Boolean for queries: `HasPendingRedirect() → bool`, `ZoneExists() → bool`
- Nullable returns for optional lookups: `GetZoneType() → string` (null if not found)
- Struct returns for lightweight data: `SensorSnapshot`, `(float t, int occ)` tuples

**Math & Physics Functions:**
```csharp
private static float Sigmoid(float x) => 1f / (1f + Mathf.Exp(-x));

private float SampleLogNormal() {
    float z = GaussianRandom();
    return Mathf.Exp(_config.dwellMu + _config.dwellSigma * z);
}

private static float GaussianRandom() {
    float u1 = Random.Range(0.0001f, 1f);
    float u2 = Random.Range(0.0001f, 1f);
    return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
}
```

- Utility functions static when they don't need instance state
- Math expressions inline with explanatory comments
- Gaussian sampling via Box-Muller transform for social force stochasticity

## Module Design

**Exports:**
- Public classes export only necessary interface (Initialize, public properties)
- Internal state marked `private` with `_` prefix
- Nested data types (PersonaConfig, SensorSnapshot) nested inside container class

**Barrel Files:**
- None used; each class in its own file
- Direct imports: `AgentController ac = GetComponent<AgentController>();`

**Dependencies:**
- Loose coupling: Managers passed to agents via Initialize(), not grabbed via FindObjectOfType
- Spatial grid queried via manager: `_manager.Grid.GetNeighbors(...)`
- Zone lookups via manager API: `_manager.GetZoneType()`, `_manager.FindNearestZoneByType()`

---

*Convention analysis: 2026-04-27*
