using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Per-agent simulation controller.
///
/// Physics:      Helbing Social Force Model (driving + agent repulsion + wall repulsion + group cohesion)
/// Behaviour:    6-state machine (Transit → Dwelling → Roaming → Socializing → Resting → Exiting)
/// Personas:     5 types (Researcher / Networker / Student / Industry / BoothStaff) with distinct parameters
/// Memory:       Visited-zone set + agenda queue bias zone selection away from already-seen booths
/// Physiology:   Hunger and thirst drives override zone gravity toward food/drink zones
/// Day-phase:    Speed, socialize, and rest probabilities shift with conference time-of-day
/// Groups:       Cohesion force keeps group members together; socializing is contagious within groups
/// Sigmoid crowd:Avoidance probability is a smooth sigmoid of density, modulated by topic interest
/// Smooth redirect: Session redirects queue until the current activity ends (or max-delay expires)
/// LLM:          Optional async conversation snippet generation during Socializing (via LLMConversationClient)
///
/// SFM uses CrowdManager.Grid (spatial hash) — O(n) not O(n²).
/// Wall forces use NavMesh.FindClosestEdge — no physics overhead.
/// NavMeshAgent handles pathfinding only; this script overrides its velocity every FixedUpdate.
/// Execution order: CrowdManager (-10) → AgentController (0).
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
[DefaultExecutionOrder(0)]
[AddComponentMenu("Conference Sim/Agent Controller")]
public class AgentController : MonoBehaviour
{
    // ── State Machine ────────────────────────────────────────────────

    public enum AgentState
    {
        Transit,        // Walking to assigned zone
        Dwelling,       // Stationary at zone (viewing / interacting)
        Roaming,        // Slow wandering near zone (browsing)
        Socializing,    // Stationary conversation cluster
        Resting,        // Seated rest — partial fatigue recovery
        Exiting         // Walking to exit → returned to pool
    }

    public AgentState CurrentState  { get; private set; } = AgentState.Transit;
    public string     TargetSensorId { get; private set; }

    // Expose Nav for SFM velocity read by neighbouring agents
    public NavMeshAgent Nav => _nav;

    // Persona index into SimConfig.personas array
    public int PersonaIndex { get; private set; } = 0;
    public string PersonaName => (_config != null && _config.personas != null &&
                                  PersonaIndex < _config.personas.Length)
                                  ? _config.personas[PersonaIndex].name : "Unknown";

    // ── Private ──────────────────────────────────────────────────────

    private SimConfig    _config;
    private NavMeshAgent _nav;
    private CrowdManager _manager;
    private Transform    _goalTransform;
    private Transform[]  _exitPoints;

    private Vector3 _desiredDir;
    private float   _desiredSpeed;
    private float   _walkTime;       // cumulative walk seconds (fatigue source)

    // ── Visual state color ───────────────────────────────────────────
    private Renderer              _renderer;
    private MaterialPropertyBlock _mpb;
    private int     _visitCount;

    private float _stateTimer;
    private float _avgDwellCSV;
    private float _roamHeading;

    // Per-transit path variation
    private Vector3 _transitDest;
    private Vector3 _transitWaypoint;
    private bool    _hasTransitWaypoint;

    // Per-agent personal speed drawn from N(desiredSpeed, 0.26) at spawn
    private float _personalSpeed;

    // Reusable neighbour list — allocated once per agent
    private readonly List<AgentController> _neighbours = new List<AgentController>(32);

    // ── Persona multipliers (cached from PersonaConfig at spawn) ─────
    private float _speedMult      = 1.0f;
    private float _dwellMult      = 1.0f;
    private float _socializeMult  = 1.0f;
    private float _fatigueMult    = 1.0f;
    private float _visitPenaltyMult = 1.0f;
    private string[] _preferredTopics;

    // ── Memory & Agenda ──────────────────────────────────────────────
    private readonly HashSet<string> _visitedZones = new HashSet<string>();
    private readonly Queue<string>   _agendaQueue  = new Queue<string>();

    // ── Physiological drives [0–1] ───────────────────────────────────
    private float _hunger;
    private float _thirst;

    // ── Social group ─────────────────────────────────────────────────
    private AgentGroup _group;

    // ── Smooth session redirect ───────────────────────────────────────
    private Transform _pendingRedirectGoal;
    private string    _pendingRedirectZoneId;
    private float     _pendingRedirectTimer;   // countdown; 0 = no pending redirect

    // ── LLM conversation ─────────────────────────────────────────────
    private bool _llmPending;  // prevent double-fire for same socializing session

    // ── Initialisation ───────────────────────────────────────────────

    /// <summary>Called by CrowdManager immediately after obtaining an agent from the pool.</summary>
    public void Initialize(SimConfig cfg, Transform goal, string sensorId,
                           float avgDwellSec, Transform[] exits, CrowdManager mgr,
                           int personaIndex = 0, AgentGroup group = null)
    {
        _config        = cfg;
        _goalTransform = goal;
        TargetSensorId = sensorId;
        _avgDwellCSV   = avgDwellSec;
        _exitPoints    = exits;
        _manager       = mgr;
        PersonaIndex   = personaIndex;
        _group         = group;

        _nav = GetComponent<NavMeshAgent>();
        _nav.updatePosition    = true;
        _nav.updateRotation    = false;
        _nav.speed             = cfg.desiredSpeed;

        // Cache renderer for state-color updates
        if (_renderer == null) _renderer = GetComponentInChildren<Renderer>();
        if (_mpb == null)      _mpb = new MaterialPropertyBlock();
        _nav.radius            = cfg.agentRadius;
        _nav.avoidancePriority = Random.Range(0, 100);

        // Cache persona multipliers
        if (cfg.personas != null && personaIndex < cfg.personas.Length)
        {
            var p = cfg.personas[personaIndex];
            _speedMult       = p.speedMult;
            _dwellMult       = p.dwellMult;
            _socializeMult   = p.socializeMult;
            _fatigueMult     = p.fatigueMult;
            _visitPenaltyMult = p.visitPenaltyMult;
            _preferredTopics  = p.preferredTopics ?? new string[0];
        }

        // Personal speed: Weidmann (1993) — N(1.34, 0.26), scaled by persona
        _personalSpeed = Mathf.Clamp(
            (cfg.desiredSpeed + GaussianRandom() * 0.26f) * _speedMult,
            0.5f, 2.5f);
        _desiredSpeed   = _personalSpeed;

        _walkTime  = 0f;
        _visitCount = 0;
        _roamHeading = Random.Range(0f, 360f);
        _stateTimer  = 0f;

        // Initialise physiology at a random starting point (not everyone starts fresh)
        _hunger = Random.Range(0f, 0.3f);
        _thirst = Random.Range(0f, 0.3f);

        // Clear memory
        _visitedZones.Clear();
        _agendaQueue.Clear();
        _pendingRedirectGoal   = null;
        _pendingRedirectZoneId = null;
        _pendingRedirectTimer  = 0f;
        _llmPending = false;

        // Build interest-weighted agenda from available zones
        BuildAgenda();

        EnterState(AgentState.Transit);
    }

    /// <summary>Called by CrowdManager when the agent is returned to the pool.</summary>
    public void ResetForPool()
    {
        if (_group != null) { _group.Remove(this); _group = null; }

        CurrentState   = AgentState.Transit;
        TargetSensorId = string.Empty;
        _config        = null;
        _manager       = null;
        _walkTime      = 0f;
        _visitCount    = 0;
        _stateTimer    = 0f;
        _hunger        = 0f;
        _thirst        = 0f;
        _pendingRedirectGoal   = null;
        _pendingRedirectZoneId = null;
        _pendingRedirectTimer  = 0f;
        _llmPending = false;
        _visitedZones.Clear();
        _agendaQueue.Clear();

        if (_nav != null) { _nav.isStopped = true; _nav.ResetPath(); }
    }

    /// <summary>Immediate reassignment (existing sessions not yet running).</summary>
    public void ReassignTarget(Transform newGoal, string newSensorId)
    {
        _goalTransform = newGoal;
        TargetSensorId = newSensorId;
        _pendingRedirectGoal   = null;
        _pendingRedirectZoneId = null;
        _pendingRedirectTimer  = 0f;
        EnterState(AgentState.Transit);
    }

    /// <summary>
    /// Smooth redirect: queues a redirect that the agent will act on once the current
    /// state finishes, or immediately if forced by redirectMaxDelay.
    /// Called by CrowdManager when smoothSessionRedirects is enabled.
    /// </summary>
    public void QueueRedirect(Transform newGoal, string newSensorId)
    {
        _pendingRedirectGoal   = newGoal;
        _pendingRedirectZoneId = newSensorId;
        _pendingRedirectTimer  = _config != null ? _config.redirectMaxDelay : 120f;
    }

    /// <summary>Called by AgentGroup when a peer starts socializing (contagion).</summary>
    public void JoinGroupSocialize()
    {
        if (CurrentState == AgentState.Roaming || CurrentState == AgentState.Dwelling)
            EnterState(AgentState.Socializing);
    }

    void OnDisable() => _manager?.UnregisterAgent(this);

    // ── Main Loop ────────────────────────────────────────────────────

    void FixedUpdate()
    {
        if (_config == null) return;

        float dt = Mathf.Min(Time.fixedDeltaTime, 0.05f);

        // Accumulate walk time for fatigue
        if (CurrentState == AgentState.Transit  ||
            CurrentState == AgentState.Roaming  ||
            CurrentState == AgentState.Exiting)
            _walkTime += dt;

        // Physiological drives accumulate regardless of state
        _hunger += _config.hungerRate * dt;
        _thirst += _config.thirstRate * dt;
        _hunger  = Mathf.Clamp01(_hunger);
        _thirst  = Mathf.Clamp01(_thirst);

        // Smooth redirect countdown — force-apply if max delay reached
        if (_pendingRedirectTimer > 0f)
        {
            _pendingRedirectTimer -= dt;
            if (_pendingRedirectTimer <= 0f)
                ApplyPendingRedirect();
        }

        // Fatigue factor:  v(t) = v₀ · [α + (1−α) · exp(−t / T_f·fatigueMult)]
        float alpha        = _config.minSpeedFraction;
        float effectiveT   = _config.fatigueTimeConstant * _fatigueMult;
        float fatigueFactor = alpha + (1f - alpha) * Mathf.Exp(-_walkTime / effectiveT);

        // Day-phase speed modifier
        float phaseSpeed  = GetDayPhaseSpeedMult();
        _desiredSpeed     = _personalSpeed * fatigueFactor * phaseSpeed;

        // Tick state machine
        switch (CurrentState)
        {
            case AgentState.Transit:     TickTransit(dt);         break;
            case AgentState.Dwelling:    TickDwelling(dt);        break;
            case AgentState.Roaming:     TickRoaming(dt, fatigueFactor); break;
            case AgentState.Socializing: TickSocializing(dt);     break;
            case AgentState.Resting:     TickResting(dt);         break;
            case AgentState.Exiting:     TickExiting();           break;
        }

        // Apply SFM forces while moving
        if (CurrentState == AgentState.Transit  ||
            CurrentState == AgentState.Roaming  ||
            CurrentState == AgentState.Exiting)
            ApplySocialForces(dt);

        // Smooth facing
        Vector3 flatVel = new Vector3(_nav.velocity.x, 0f, _nav.velocity.z);
        if (flatVel.sqrMagnitude > 0.04f)
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(flatVel.normalized),
                8f * dt);
    }

    // ── States ───────────────────────────────────────────────────────

    void TickTransit(float dt)
    {
        if (_goalTransform == null) return;

        // Advance to final destination once the intermediate waypoint is reached
        if (_hasTransitWaypoint &&
            Vector3.Distance(transform.position, _transitWaypoint) < 2f)
        {
            _hasTransitWaypoint = false;
            SetNavDest(_transitDest);
        }

        float d = Vector3.Distance(transform.position, _transitDest);

        // Sigmoid crowding avoidance (replaces old hard threshold)
        if (d < _config.boothDensityRadius)
        {
            float density         = _manager.GetBoothDensity(TargetSensorId);
            float interest        = GetZoneInterest(TargetSensorId);     // 0..1 persona interest
            float sigInput        = (density - _config.boothCrowdThreshold) * _config.crowdingSigmoidSensitivity;
            float avoidProb       = Sigmoid(sigInput) * (1f - interest * 0.5f); // interest dampens avoidance
            if (Random.value < avoidProb * dt * 8f)
            { EnterState(AgentState.Roaming); return; }
        }

        if (d < 1.5f) EnterState(AgentState.Dwelling);
    }

    void TickDwelling(float dt)
    {
        _nav.isStopped = true;
        _stateTimer   -= dt;

        // Check for pending redirect at natural exit point
        if (_stateTimer <= 0f && HasPendingRedirect())
        {
            ApplyPendingRedirect();
            return;
        }

        if (_stateTimer <= 0f)
        {
            _nav.isStopped  = false;
            float fatigueRatio = Mathf.Clamp01(_walkTime / (_config.fatigueTimeConstant * _fatigueMult));
            float phaseRest    = GetDayPhaseRestMult();
            float r            = Random.value;

            // Physiological need check — seek food/drink first if drives are high
            if (CheckPhysiologicalOverride()) return;

            if (r < 0.30f)
            {
                if (fatigueRatio > 0f && Random.value < fatigueRatio * 0.6f)
                {
                    string next = PickDestinationZone();
                    if (next != TargetSensorId) { _manager.MoveAgentToZone(this, next); return; }
                }
                EnterState(AgentState.Roaming);
            }
            else if (r < 0.50f) EnterState(AgentState.Socializing);
            else if (r < 0.50f + fatigueRatio * 0.30f * phaseRest) EnterState(AgentState.Resting);
            else _stateTimer = Random.Range(30f, 120f);
        }
    }

    void TickRoaming(float dt, float ff)
    {
        _nav.isStopped = false;

        // Ornstein-Uhlenbeck heading that drifts back toward zone center
        float meanHeading = Mathf.Atan2(
            _goalTransform.position.z - transform.position.z,
            _goalTransform.position.x - transform.position.x) * Mathf.Rad2Deg;

        _roamHeading += (-_config.roamReorientRate * (_roamHeading - meanHeading)
                         + _config.roamDirectionNoise * GaussianRandom()) * dt;

        float   spd    = Random.Range(_config.roamSpeedMin, _config.roamSpeedMax) * ff;
        Vector3 dir    = new Vector3(Mathf.Cos(_roamHeading * Mathf.Deg2Rad), 0,
                                     Mathf.Sin(_roamHeading * Mathf.Deg2Rad));
        Vector3 target = transform.position + dir * 3f;

        if (NavMesh.SamplePosition(target, out var hit, 5f, NavMesh.AllAreas))
            SetNavDest(hit.position);

        _nav.speed  = spd;
        _stateTimer -= dt;

        // Check for pending redirect at natural exit point
        if (_stateTimer <= 0f && HasPendingRedirect())
        {
            ApplyPendingRedirect();
            return;
        }

        if (_stateTimer <= 0f)
        {
            float fatigueRatio = Mathf.Clamp01(_walkTime / (_config.fatigueTimeConstant * _fatigueMult));
            float phaseRest    = GetDayPhaseRestMult();
            float r            = Random.value;
            float density      = _manager.GetBoothDensity(TargetSensorId);

            if (CheckPhysiologicalOverride()) return;

            if (density < _config.boothCrowdThreshold && r < 0.35f)
            {
                string next = PickDestinationZone();
                if (next != TargetSensorId) { _manager.MoveAgentToZone(this, next); return; }
                EnterState(AgentState.Transit);
            }
            else if (r < 0.55f)                                       EnterState(AgentState.Socializing);
            else if (r < 0.55f + fatigueRatio * 0.35f * phaseRest)   EnterState(AgentState.Resting);
            else _stateTimer = Random.Range(30f, 120f);
        }
    }

    void TickSocializing(float dt)
    {
        _nav.isStopped = true;
        _stateTimer   -= dt;

        // Fire LLM conversation once per socializing session
        if (!_llmPending && _config.llmEnabled)
        {
            _llmPending = true;
            TryFireLLMConversation();
        }

        if (_stateTimer <= 0f)
        {
            _nav.isStopped = false;
            _llmPending    = false;

            if (HasPendingRedirect()) { ApplyPendingRedirect(); return; }
            EnterState(Random.value < 0.5f ? AgentState.Transit : AgentState.Roaming);
        }
    }

    void TickResting(float dt)
    {
        _nav.isStopped = true;
        _stateTimer   -= dt;
        _walkTime      = Mathf.Max(0f, _walkTime - dt * 0.5f); // partial fatigue recovery

        if (_stateTimer <= 0f)
        {
            _nav.isStopped = false;
            if (HasPendingRedirect()) { ApplyPendingRedirect(); return; }
            EnterState(AgentState.Transit);
        }
    }

    void TickExiting()
    {
        if (!_nav.pathPending && _nav.remainingDistance < 1.0f)
            _manager.ReturnToPool(gameObject);
    }

    // ── Transitions ──────────────────────────────────────────────────

    private void EnterState(AgentState next)
    {
        CurrentState = next;
        ApplyStateColor(next);

        switch (next)
        {
            case AgentState.Transit:
                _nav.isStopped = false;
                _nav.speed     = _desiredSpeed;
                if (_goalTransform != null)
                {
                    // Jitter final destination within 3 m of zone center
                    Vector3 jitter = new Vector3(Random.Range(-3f, 3f), 0f, Random.Range(-3f, 3f));
                    Vector3 dest   = _goalTransform.position + jitter;
                    _transitDest   = NavMesh.SamplePosition(dest, out var dHit, 4f, NavMesh.AllAreas)
                                     ? dHit.position : _goalTransform.position;

                    // 75% of agents take a lateral detour waypoint on longer trips
                    _hasTransitWaypoint = false;
                    Vector3 toGoal = _transitDest - transform.position;
                    if (Random.value < 0.75f && toGoal.magnitude > 8f)
                    {
                        Vector3 perp = new Vector3(-toGoal.normalized.z, 0f, toGoal.normalized.x);
                        Vector3 mid  = transform.position
                                       + toGoal * Random.Range(0.25f, 0.55f)
                                       + perp   * Random.Range(-6f, 6f);
                        if (NavMesh.SamplePosition(mid, out var wHit, 8f, NavMesh.AllAreas))
                        {
                            _transitWaypoint    = wHit.position;
                            _hasTransitWaypoint = true;
                        }
                    }
                    SetNavDest(_hasTransitWaypoint ? _transitWaypoint : _transitDest);
                }
                break;

            case AgentState.Dwelling:
                _visitCount++;
                _nav.isStopped = true;
                _visitedZones.Add(TargetSensorId);

                // Apply physiological reset if at appropriate zone
                ApplyPhysiologicalReset();

                // Dwell duration: CSV value or log-normal; decay with visit count; scaled by persona
                float baseDwell = _avgDwellCSV > 0f ? _avgDwellCSV : SampleLogNormal();
                _stateTimer     = baseDwell * Mathf.Pow(_visitCount, -_config.dwellDecayExponent)
                                           * _dwellMult;
                break;

            case AgentState.Roaming:
                _nav.isStopped = false;
                _stateTimer    = Random.Range(30f, 180f);
                _roamHeading   = Random.Range(0f, 360f);
                break;

            case AgentState.Socializing:
                _nav.isStopped = true;
                float phaseS   = GetDayPhaseSocializeMult();
                _stateTimer    = Random.Range(_config.socializeDurationMin, _config.socializeDurationMax)
                                 * _socializeMult * phaseS;
                _llmPending    = false;

                // Trigger group contagion — nearby group peers may join
                if (_group != null)
                    _group.TriggerSocializeContagion(this, _config.groupSocializeContagion);
                break;

            case AgentState.Resting:
                _nav.isStopped = true;
                float phaseR   = GetDayPhaseRestMult();
                _stateTimer    = Random.Range(_config.restDurationMin, _config.restDurationMax)
                                 * phaseR;
                break;

            case AgentState.Exiting:
                _nav.isStopped = false;
                _nav.speed     = _desiredSpeed;
                var exit       = GetNearestExit();
                if (exit != null) SetNavDest(exit.position);
                break;
        }
    }

    /// <summary>Called by CrowdManager when a CSV departure is assigned to this agent.</summary>
    public void RouteToExit() => EnterState(AgentState.Exiting);

    // ── Smooth Redirect Helpers ───────────────────────────────────────

    private bool HasPendingRedirect() =>
        _pendingRedirectGoal != null && !string.IsNullOrEmpty(_pendingRedirectZoneId);

    private void ApplyPendingRedirect()
    {
        if (!HasPendingRedirect()) return;

        var  goal = _pendingRedirectGoal;
        var  zone = _pendingRedirectZoneId;
        _pendingRedirectGoal   = null;
        _pendingRedirectZoneId = null;
        _pendingRedirectTimer  = 0f;

        // Update tracking in CrowdManager then change destination
        _manager.MoveAgentToZoneImmediate(this, goal, zone);
    }

    // ══════════════════════════════════════════════════════════════════
    // ██  SOCIAL FORCE MODEL  █████████████████████████████████████████
    // ══════════════════════════════════════════════════════════════════

    private void ApplySocialForces(float dt)
    {
        if (_manager == null || _manager.Grid == null) return;

        Vector3 pos = transform.position;
        Vector3 vel = _nav.velocity;

        // ── 1. Driving force:  F = m·(v₀·ê₀ − v) / τ ─────────────
        _desiredDir = _nav.hasPath && _nav.path.corners.Length > 1
            ? (_nav.path.corners[1] - pos).normalized
            : (_nav.destination - pos).normalized;

        Vector3 driving = _config.agentMass *
            (_desiredDir * _desiredSpeed - vel) / _config.relaxationTime;

        // ── 2. Agent-agent repulsion (spatial grid) ────────────────
        Vector3 social = Vector3.zero;
        _manager.Grid.GetNeighbors(pos, 5f, _neighbours);

        foreach (var other in _neighbours)
        {
            if (other == this || other == null) continue;

            Vector3 diff = pos - other.transform.position;
            float   dist = diff.magnitude;
            if (dist < 0.001f) continue;

            Vector3 nij = diff / dist;
            float   gap = _config.agentRadius * 2f - dist;

            float cosPhi = -Vector3.Dot(_desiredDir, nij);
            float w      = _config.anisotropyLambda +
                           (1f - _config.anisotropyLambda) * (1f + cosPhi) * 0.5f;

            Vector3 fij = w * _config.socialForceA *
                          Mathf.Exp(gap / _config.socialForceB) * nij;

            if (gap > 0f)
            {
                fij += _config.bodyCompressionK * gap * nij;
                Vector3 tij    = new Vector3(-nij.z, 0, nij.x);
                float   deltaV = Vector3.Dot(other.Nav.velocity - vel, tij);
                fij += _config.slidingFrictionKappa * gap * deltaV * tij;
            }

            social += fij;
        }

        // ── 3. Wall repulsion via NavMesh edge ─────────────────────
        Vector3 wall = Vector3.zero;
        if (NavMesh.FindClosestEdge(pos, out var edge, NavMesh.AllAreas))
        {
            Vector3 toEdge   = edge.position - pos;
            float   distEdge = toEdge.magnitude;
            if (distEdge < 2f && distEdge > 0.001f)
            {
                Vector3 niW = -toEdge.normalized;
                wall = _config.socialForceA *
                       Mathf.Exp((_config.agentRadius - distEdge) / _config.socialForceB) * niW;

                float gap = _config.agentRadius - distEdge;
                if (gap > 0f)
                {
                    wall += _config.bodyCompressionK * gap * niW;
                    Vector3 tiW = new Vector3(-niW.z, 0, niW.x);
                    wall -= _config.slidingFrictionKappa * gap * Vector3.Dot(vel, tiW) * tiW;
                }
            }
        }

        // ── 4. Group cohesion force ────────────────────────────────
        Vector3 cohesion = Vector3.zero;
        if (_group != null && _group.Members.Count > 1)
        {
            Vector3 centroid   = _group.GetCentroid();
            float   separation = Vector3.Distance(pos, centroid);
            if (separation > _config.groupSeparationMin)
            {
                float t = Mathf.Clamp01(
                    (separation - _config.groupSeparationMin) /
                    Mathf.Max(0.01f, _config.groupSeparationMax - _config.groupSeparationMin));
                Vector3 toCenter = (centroid - pos).normalized;
                cohesion = toCenter * (_config.groupCohesionStrength * t);
            }
        }

        // ── 5. Integrate → clamp → override NavMeshAgent velocity ──
        Vector3 newVel = vel + ((driving + social + wall + cohesion) / _config.agentMass) * dt;
        newVel.y = 0f;

        // Micro steering noise
        if (newVel.sqrMagnitude > 0.01f)
        {
            Vector3 perp = new Vector3(-newVel.normalized.z, 0f, newVel.normalized.x);
            newVel += perp * (GaussianRandom() * 0.04f * _desiredSpeed);
        }

        float maxSpd = _desiredSpeed * 1.3f;
        if (newVel.magnitude > maxSpd) newVel = newVel.normalized * maxSpd;

        _nav.velocity = newVel;
    }

    // ── Zone Selection (Gravity Model + Memory + Physiology) ─────────

    /// <summary>
    /// Picks next destination zone.
    /// Priority order: (1) physiological need override, (2) agenda queue, (3) gravity model.
    /// Gravity weight = areaM2 · interest · exp(-β·dist) / (1 + density) · visitPenalty.
    /// </summary>
    private string PickDestinationZone()
    {
        if (_manager.Zones == null || _manager.Zones.Length == 0) return TargetSensorId;

        // Physiological override handled separately — CheckPhysiologicalOverride() is called before this
        // Agenda: 60% chance to pick from agenda queue when non-empty
        if (_agendaQueue.Count > 0 && Random.value < 0.60f)
        {
            string candidate = _agendaQueue.Peek();
            // Validate zone still exists
            if (_manager.ZoneExists(candidate))
            {
                _agendaQueue.Dequeue();
                return candidate;
            }
            _agendaQueue.Dequeue(); // discard stale entry
        }

        // Gravity model
        float beta  = _config.gravityBeta0 *
                      (1f + _config.fatigueZoneBias * _walkTime /
                       Mathf.Max(1f, _config.fatigueTimeConstant * _fatigueMult));

        var   weights = new float[_manager.Zones.Length];
        float total   = 0f;

        for (int i = 0; i < _manager.Zones.Length; i++)
        {
            var   z       = _manager.Zones[i];
            float dist    = Vector3.Distance(transform.position, z.GoalTransform.position);
            float dens    = _manager.GetBoothDensity(z.sensorId);
            float interest = GetZoneInterest(z.sensorId);

            // Visit penalty: previously-visited zones are less attractive
            float visitPenalty = _visitedZones.Contains(z.sensorId)
                ? Mathf.Pow(0.30f, _visitPenaltyMult)
                : 1.0f;

            weights[i] = Mathf.Max(
                z.areaM2 * (0.5f + interest * 0.5f) *
                Mathf.Exp(-beta * dist) *
                visitPenalty /
                (1f + dens),
                0.001f);
            total += weights[i];
        }

        float pick = Random.Range(0f, total);
        for (int i = 0; i < weights.Length; i++)
        {
            pick -= weights[i];
            if (pick <= 0f) return _manager.Zones[i].sensorId;
        }
        return _manager.Zones[_manager.Zones.Length - 1].sensorId;
    }

    // ── Physiological Drive Helpers ───────────────────────────────────

    /// <summary>
    /// Checks if hunger or thirst are above threshold. If so, overrides the next zone
    /// choice with a food/drink zone. Returns true if an override was applied.
    /// </summary>
    private bool CheckPhysiologicalOverride()
    {
        bool hungry = _hunger >= _config.hungerThreshold;
        bool thirsty = _thirst >= _config.thirstThreshold;
        if (!hungry && !thirsty) return false;

        // Pick the stronger drive
        bool seekFood = hungry && (!thirsty || _hunger >= _thirst);
        string targetTag = seekFood ? _config.foodZoneTag : _config.drinkZoneTag;

        string driveZone = _manager.FindNearestZoneByType(transform.position, targetTag);
        if (string.IsNullOrEmpty(driveZone)) return false;

        _manager.MoveAgentToZone(this, driveZone);
        return true;
    }

    /// <summary>Reduces hunger/thirst when dwelling in a food/drink zone.</summary>
    private void ApplyPhysiologicalReset()
    {
        string zoneType = _manager.GetZoneType(TargetSensorId);
        if (zoneType == _config.foodZoneTag)
            _hunger = Mathf.Max(0f, _hunger - _config.hungerResetAmount);
        else if (zoneType == _config.drinkZoneTag)
            _thirst = Mathf.Max(0f, _thirst - _config.thirstResetAmount);
    }

    // ── Persona Interest Helpers ──────────────────────────────────────

    /// <summary>
    /// Returns [0,1] interest score for a zone based on this agent's persona topic preferences.
    /// Falls back to 0.5 (neutral) if zone has no topics.
    /// </summary>
    private float GetZoneInterest(string zoneId)
    {
        if (_preferredTopics == null || _preferredTopics.Length == 0) return 0.5f;
        string[] zoneTags = _manager.GetZoneTopics(zoneId);
        if (zoneTags == null || zoneTags.Length == 0) return 0.5f;

        foreach (var topic in _preferredTopics)
            foreach (var tag in zoneTags)
                if (string.Equals(topic, tag, System.StringComparison.OrdinalIgnoreCase))
                    return 1.0f;  // direct match → maximum interest

        return 0.2f; // no overlap → low interest
    }

    /// <summary>
    /// Builds an agenda queue of preferred zones ordered by persona interest score.
    /// Called once at spawn.
    /// </summary>
    private void BuildAgenda()
    {
        if (_manager == null || _manager.Zones == null) return;

        // Sort zones by interest descending, take top 4, shuffle slightly for variety
        var scored = new List<(string id, float score)>();
        foreach (var z in _manager.Zones)
        {
            float interest = GetZoneInterest(z.sensorId);
            if (interest > 0.3f)
                scored.Add((z.sensorId, interest + GaussianRandom() * 0.1f));
        }
        scored.Sort((a, b) => b.score.CompareTo(a.score));

        int agendaLen = Mathf.Min(scored.Count, 4);
        for (int i = 0; i < agendaLen; i++)
            _agendaQueue.Enqueue(scored[i].id);
    }

    // ── Day-Phase Helpers ─────────────────────────────────────────────

    private float GetDayPhaseSpeedMult()
    {
        int idx = _manager != null ? _manager.CurrentDayPhaseIndex : -1;
        if (idx < 0 || _config.dayPhases == null || idx >= _config.dayPhases.Length) return 1f;
        return _config.dayPhases[idx].speedMult;
    }

    private float GetDayPhaseSocializeMult()
    {
        int idx = _manager != null ? _manager.CurrentDayPhaseIndex : -1;
        if (idx < 0 || _config.dayPhases == null || idx >= _config.dayPhases.Length) return 1f;
        return _config.dayPhases[idx].socializeMult;
    }

    private float GetDayPhaseRestMult()
    {
        int idx = _manager != null ? _manager.CurrentDayPhaseIndex : -1;
        if (idx < 0 || _config.dayPhases == null || idx >= _config.dayPhases.Length) return 1f;
        return _config.dayPhases[idx].restMult;
    }

    // ── LLM Conversation ─────────────────────────────────────────────

    private void TryFireLLMConversation()
    {
        var llmClient = LLMConversationClient.Instance;
        if (llmClient == null) return;

        // Find the nearest socializing neighbour as conversation partner
        AgentController partner = null;
        float bestDist = 4f;
        foreach (var n in _neighbours)
        {
            if (n == null || n == this) continue;
            if (n.CurrentState != AgentState.Socializing) continue;
            float d = Vector3.Distance(transform.position, n.transform.position);
            if (d < bestDist) { bestDist = d; partner = n; }
        }

        llmClient.RequestConversation(this, partner, (topic) =>
        {
            // Conversation topic logged — could drive follow-up zone selection
            if (!string.IsNullOrEmpty(topic))
                Debug.Log($"[LLM] Agent {PersonaName} at {TargetSensorId}: \"{topic}\"");
        });
    }

    // ── State Color ──────────────────────────────────────────────────

    private static readonly int _ColorId     = Shader.PropertyToID("_Color");
    private static readonly int _BaseColorId = Shader.PropertyToID("_BaseColor"); // URP

    private void ApplyStateColor(AgentState state)
    {
        if (_renderer == null || _mpb == null || _config == null) return;

        Color c = state switch
        {
            AgentState.Transit     => _config.colorTransit,
            AgentState.Dwelling    => _config.colorDwelling,
            AgentState.Roaming     => _config.colorRoaming,
            AgentState.Socializing => _config.colorSocializing,
            AgentState.Resting     => _config.colorResting,
            AgentState.Exiting     => _config.colorExiting,
            _                      => Color.white
        };

        _renderer.GetPropertyBlock(_mpb);
        _mpb.SetColor(_ColorId,     c);
        _mpb.SetColor(_BaseColorId, c); // URP
        _renderer.SetPropertyBlock(_mpb);
    }

    // ── Utilities ────────────────────────────────────────────────────

    private void SetNavDest(Vector3 target)
    {
        if (NavMesh.SamplePosition(target, out var hit, 5f, NavMesh.AllAreas))
            _nav.SetDestination(hit.position);
    }

    private Transform GetNearestExit()
    {
        if (_exitPoints == null || _exitPoints.Length == 0) return null;
        Transform best = _exitPoints[0];
        float minD = float.MaxValue;
        foreach (var e in _exitPoints)
        {
            float d = Vector3.Distance(transform.position, e.position);
            if (d < minD) { minD = d; best = e; }
        }
        return best;
    }

    private float SampleLogNormal()
    {
        float z = GaussianRandom();
        return Mathf.Exp(_config.dwellMu + _config.dwellSigma * z);
    }

    private static float Sigmoid(float x) => 1f / (1f + Mathf.Exp(-x));

    private static float GaussianRandom()
    {
        float u1 = Random.Range(0.0001f, 1f);
        float u2 = Random.Range(0.0001f, 1f);
        return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
    }
}
