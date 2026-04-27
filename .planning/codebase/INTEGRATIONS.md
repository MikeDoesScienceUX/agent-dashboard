# External Integrations

**Analysis Date:** 2026-04-27

## APIs & External Services

**Claude API (Anthropic):**
- Purpose: Generate contextually-grounded conversation snippets when agents socialize
- Endpoint: `https://api.anthropic.com/v1/messages`
- API Version: `2023-06-01`
- SDK/Client: `UnityEngine.Networking.UnityWebRequest` (no official Anthropic SDK)
- Auth: Bearer token via `x-api-key` header
- Configuration:
  - Environment variable: `ANTHROPIC_API_KEY`
  - Alternative: `SimConfig.llmApiKey` field (editor-assignable)
- Implementation: `Assets/Scripts/LLMConversationClient.cs`
  - Singleton MonoBehaviour
  - Fire-and-forget async coroutine pattern
  - Budget control: `SimConfig.llmCallsPerSimMinute` (default: caps LLM calls per simulated minute)
- Model: Configurable via `SimConfig.llmModel` (default: `claude-3-5-haiku-20241022`)
- Disabled by default: `SimConfig.llmEnabled = false` (enable only when key provided)
- Response parsing: Custom manual JSON extraction (no JSON library dependency)
  - Extracts `content[0].text` field from response
  - Handles escaped quotes and newlines
  - Returns `null` on parse failure

## Data Storage

**Databases:**
- None - Local in-memory storage only

**File Storage:**
- Local filesystem (Application.dataPath and Application.persistentDataPath)
- Ingestion:
  - `Assets/StreamingAssets/sensor_data.csv` - Ground-truth headcount timeseries from Posterbuddy sensors
    - Format: Timestamp, Zone ID, Headcount
    - Client: `DataLoader.cs` (custom CSV parser using `System.IO`)
    - Path resolution: `Application.streamingAssetsPath`
  
  - `Assets/StreamingAssets/session_schedule.csv` - Conference schedule
    - Format: Session ID, Zone ID, Start Hour, End Hour, Topic
    - Client: `SessionScheduleLoader.cs` (custom CSV parser)
    - Path resolution: `Application.streamingAssetsPath`
  
  - `Assets/StreamingAssets/sim_config.json` - Runtime parameter overrides
    - Format: JSON key-value pairs (optional)
    - Parsed by `ConfigLoader.cs` using `JsonUtility`

**Output Storage:**
- `Assets/SimOutput/` (created at runtime, written on application quit)
  - `spatial_log.csv` - Agent position timeseries (sampled every 0.5s)
  - `zone_timeseries.csv` - Zone occupancy every 30s
  - `flow_metrics.csv` - Flow counts (passage through zones) every 60s
  - `bottleneck_events.csv` - Congestion detection events
  - `heatmap.csv` - Spatial density grid (debug format)
  - `heatmap.png` - Heatmap visualization (false-color PNG generated via `Texture2D.EncodeToPNG()`)
  - `simulation_summary.json` - Summary statistics (via `JsonUtility.ToJson()`)
  - `validation_report.json` - RMSE, NRMSE, χ², Pearson r vs. sensor data
  - Calibration state: Stored in `Application.persistentDataPath/calibration_bias.json`

**Caching:**
- In-memory agent pool (~300 GameObjects) managed by `CrowdManager.cs`
- Conversation log: 200-entry queue in `LLMConversationClient.cs`
- No external cache layer (Redis, Memcached, etc.)

## Authentication & Identity

**Auth Provider:**
- None for agents - No user authentication system
- **Claude API Auth:**
  - Type: API Key (Bearer token)
  - Key source (in priority order):
    1. `SimConfig.llmApiKey` (Unity inspector field)
    2. `ANTHROPIC_API_KEY` environment variable
    3. Fallback: None (logs warning, LLM disabled)
  - Scope: Generate conversation snippets only

## Monitoring & Observability

**Error Tracking:**
- None - No external error tracking service
- Errors logged to Unity Console via `Debug.LogWarning()` and `Debug.LogError()`

**Logs:**
- Console logging (Debug.Log, Debug.LogWarning)
- File output:
  - CSV exports in `Assets/SimOutput/`
  - JSON validation/calibration reports
  - No structured logging framework (no Serilog, log4net, etc.)

**Profiling:**
- Unity Profiler integration (local only)
- ProfilerCaptures/ directory exists but unused in codebase

## CI/CD & Deployment

**Hosting:**
- Standalone builds only (no cloud deployment observed)
- Output: Windows .exe + data folder
- Manual build process (no CI pipeline in codebase)

**CI Pipeline:**
- None detected - No GitHub Actions, Jenkins, Azure Pipelines, etc.
- Manual testing and validation

## Environment Configuration

**Required env vars:**
- `ANTHROPIC_API_KEY` - Claude API key (optional, required only if `SimConfig.llmEnabled = true`)

**Optional env vars:**
- None detected

**Secrets location:**
- `.env` file: Not present in repository
- API keys: 
  - Preferred: `SimConfig.llmApiKey` (serialized in Unity Scene/Project)
  - Fallback: Environment variable
  - **Security note:** Storing API key in ScriptableObject is visible in version control; use environment variable for production

**Development config:**
- `.vsconfig` - Visual Studio workload configuration (ManagedGame)
- `.vscode/` - VS Code settings present but minimal
- `ProjectSettings/` - Unity project settings
- No Docker or containerization observed

## Webhooks & Callbacks

**Incoming:**
- None - Simulation runs standalone, no network inbound handlers

**Outgoing:**
- Claude API HTTP POST requests (one-directional, request-response pattern)
- Triggers: Agent socializing state (optional, controlled by `SimConfig.llmEnabled`)
- Async callback pattern: `LLMConversationClient.RequestConversation(agentA, agentB, callback)`

## Third-Party Assets

**3D Models:**
- Hall geometry (GLTF format, imported pre-built):
  - `Assets/Hall 7.gltf`
  - `Assets/Hall 8.gltf`
  - `Assets/Hall 8.1.gltf`
  - `Assets/Hall 8.1 Mezzanine.gltf`
  - Source: EASL venue (real floorplan scan)
  - Format: glTF 2.0 (cross-platform 3D standard)

**Agent Prefab:**
- `Assets/Agent.prefab` - Single humanoid agent model (mesh, collider, renderer)
- Instanced ~300 times at runtime (object pool pattern)

**Unity Asset Store Packages:**
- ProBuilder (6.0.9) - Scene geometry building tools
- Visual Scripting (1.9.9) - Optional node-based coding
- (Most other packages are first-party Unity modules, not Asset Store)

## Integrations Summary

| Service | Type | Status | Risk |
|---------|------|--------|------|
| Claude API (Anthropic) | LLM | Optional | Medium - API key exposed if stored in config; calls count toward rate limit |
| Posterbuddy Sensors | Data Source | Historical | Low - CSV import, offline processing |
| Conference Schedule | Data Source | Historical | Low - CSV import, static data |
| Unity Physics | Core | Required | Low - Embedded, stable |
| Unity Navigation | Core | Required | Low - Embedded, stable |
| URP (Rendering) | Core | Required | Low - Embedded, stable |

---

*Integration audit: 2026-04-27*
