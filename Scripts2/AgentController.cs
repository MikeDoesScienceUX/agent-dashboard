using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Phase 3, Task 3.3 + Phase 4, Tasks 4.1–4.3 — AgentController.cs
///
/// Drives a single pedestrian agent with:
///   • Helbing's Social Force Model for collision avoidance (overrides NavMesh steering)
///   • A 5-state behavioral state machine (Transit → Dwelling → Roaming → Socializing → Resting)
///   • Fatigue accumulation that decays speed and alters dwell times
///   • Density-aware booth approach (triggers Roaming if too crowded)
///
/// The NavMeshAgent provides path planning only. We override its velocity
/// every FixedUpdate with the SFM resultant force vector.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class AgentController : MonoBehaviour
{
    // ── Public State ────────────────────────────────────────────────
    public enum AgentState
    {
        Transit,       // Walking toward assigned booth
        Dwelling,      // Stationary at booth, viewing poster
        Roaming,       // Slow random walk near current area
        Socializing,   // Stationary cluster with nearby agents
        Resting,       // Stationary at rest area
        Exiting        // Walking toward exit, then destroyed
    }

    public AgentState CurrentState { get; private set; } = AgentState.Transit;
    public string TargetSensorId { get; private set; }

    // ── Private References ──────────────────────────────────────────
    private SimConfig _config;
    private NavMeshAgent _nav;
    private CrowdManager _manager;
    private Transform _goalTransform;
    private Transform[] _exitPoints;

    // ── SFM working variables ───────────────────────────────────────
    private Vector3 _desiredDirection;
    private float _currentDesiredSpeed;
    private float _cumulativeWalkTime;   // for fatigue
    private int _visitCount;             // how many booths visited (for dwell decay)

    // ── State-machine timers ────────────────────────────────────────
    private float _stateTimer;
    private float _dwellDuration;        // sampled when entering Dwelling
    private float _roamHeading;          // Ornstein-Uhlenbeck angle
    private float _avgDwellFromCSV;      // from Posterbuddy row

    // ── Static agent registry for SFM neighbor queries ──────────────
    private static List<AgentController> _allAgents = new List<AgentController>();

    // ── Initialization ──────────────────────────────────────────────

    /// <summary>
    /// Called by CrowdManager immediately after Instantiate.
    /// </summary>
    public void Initialize(SimConfig config, Transform goal, string sensorId,
                           float avgDwellSec, Transform[] exits, CrowdManager manager)
    {
        _config = config;
        _goalTransform = goal;
        TargetSensorId = sensorId;
        _avgDwellFromCSV = avgDwellSec;
        _exitPoints = exits;
        _manager = manager;

        _nav = GetComponent<NavMeshAgent>();

        // NavMeshAgent provides path only — we drive velocity via SFM
        _nav.updatePosition = true;
        _nav.updateRotation = true;
        _nav.speed = config.desiredSpeed;        // ceiling, SFM overrides
        _nav.radius = config.agentRadius;
        _nav.avoidancePriority = Random.Range(0, 100);

        _currentDesiredSpeed = config.desiredSpeed;
        _cumulativeWalkTime = 0f;
        _visitCount = 0;
        _roamHeading = Random.Range(0f, 360f);

        // Set initial NavMesh destination
        SetNavDestination(_goalTransform.position);
        CurrentState = AgentState.Transit;

        _allAgents.Add(this);
    }

    void OnDestroy()
    {
        _allAgents.Remove(this);
        if (_manager != null)
            _manager.UnregisterAgent(this);
    }


    // ── Main Simulation Loop (FixedUpdate for deterministic physics) ─

    void FixedUpdate()
    {
        if (_config == null) return;

        float dt = Time.fixedDeltaTime;

        // ── Accumulate fatigue ──────────────────────────────────────
        if (CurrentState == AgentState.Transit || CurrentState == AgentState.Roaming ||
            CurrentState == AgentState.Exiting)
        {
            _cumulativeWalkTime += dt;
        }

        // ── Compute fatigue-adjusted desired speed ──────────────────
        // v(t) = v_0 * [ alpha + (1 - alpha) * exp(-t / T_fatigue) ]
        float alpha = _config.minSpeedFraction;
        float fatigueFactor = alpha + (1f - alpha) *
            Mathf.Exp(-_cumulativeWalkTime / _config.fatigueTimeConstant);
        _currentDesiredSpeed = _config.desiredSpeed * fatigueFactor;

        // ── State machine tick ──────────────────────────────────────
        switch (CurrentState)
        {
            case AgentState.Transit:  TickTransit(dt);  break;
            case AgentState.Dwelling: TickDwelling(dt); break;
            case AgentState.Roaming:  TickRoaming(dt);  break;
            case AgentState.Socializing: TickSocializing(dt); break;
            case AgentState.Resting:  TickResting(dt);  break;
            case AgentState.Exiting:  TickExiting(dt);  break;
        }

        // ── Apply Social Force Model (only when moving) ─────────────
        if (CurrentState == AgentState.Transit || CurrentState == AgentState.Roaming ||
            CurrentState == AgentState.Exiting)
        {
            ApplySocialForces(dt);
        }
    }


    // ── STATE: Transit ──────────────────────────────────────────────

    private void TickTransit(float dt)
    {
        if (_goalTransform == null) return;

        // Keep NavMesh destination updated
        SetNavDestination(_goalTransform.position);

        float distToGoal = Vector3.Distance(transform.position, _goalTransform.position);

        if (distToGoal < _config.boothDensityRadius)
        {
            // ── Phase 4.3: Density check before entering ────────────
            float density = _manager.GetBoothDensity(TargetSensorId);
            if (density >= _config.boothCrowdThreshold)
            {
                // Booth is too crowded — bleed into Roaming
                EnterState(AgentState.Roaming);
                return;
            }
        }

        // Close enough to booth center → start dwelling
        if (distToGoal < 1.5f)
        {
            EnterState(AgentState.Dwelling);
        }
    }


    // ── STATE: Dwelling (Phase 4.2) ─────────────────────────────────

    private void TickDwelling(float dt)
    {
        // Agent is stationary at the booth
        _nav.isStopped = true;

        _stateTimer -= dt;
        if (_stateTimer <= 0f)
        {
            // Dwell complete — decide what to do next
            _nav.isStopped = false;
            float roll = Random.value;

            if (roll < 0.3f)
            {
                EnterState(AgentState.Roaming);
            }
            else if (roll < 0.5f)
            {
                EnterState(AgentState.Socializing);
            }
            else
            {
                // Default: agent stays managed by CrowdManager departures
                // If no departure order comes, they keep dwelling indefinitely
                // until CrowdManager calls RouteToExit().
                // Re-enter dwelling with a short secondary dwell.
                _stateTimer = Random.Range(30f, 120f);
            }
        }
    }


    // ── STATE: Roaming ──────────────────────────────────────────────

    private void TickRoaming(float dt)
    {
        _nav.isStopped = false;

        // Ornstein-Uhlenbeck process for heading
        // d(theta)/dt = -k * (theta - theta_mean) + sigma * dW
        float thetaMean = Mathf.Atan2(
            _goalTransform.position.z - transform.position.z,
            _goalTransform.position.x - transform.position.x) * Mathf.Rad2Deg;

        _roamHeading += (-_config.roamReorientRate * (_roamHeading - thetaMean)
                         + _config.roamDirectionNoise * GaussianRandom()) * dt;

        float speed = Random.Range(_config.roamSpeedMin, _config.roamSpeedMax) * fatigueFactor();
        Vector3 dir = new Vector3(
            Mathf.Cos(_roamHeading * Mathf.Deg2Rad), 0,
            Mathf.Sin(_roamHeading * Mathf.Deg2Rad));

        Vector3 roamTarget = transform.position + dir * 3f;

        // Clamp to NavMesh
        NavMeshHit hit;
        if (NavMesh.SamplePosition(roamTarget, out hit, 5f, NavMesh.AllAreas))
        {
            SetNavDestination(hit.position);
        }

        _nav.speed = speed;

        _stateTimer -= dt;
        if (_stateTimer <= 0f)
        {
            // Transition out of roaming
            float roll = Random.value;
            float density = _manager.GetBoothDensity(TargetSensorId);

            if (density < _config.boothCrowdThreshold && roll < 0.4f)
            {
                // Re-approach booth if it's less crowded now
                EnterState(AgentState.Transit);
            }
            else if (roll < 0.6f)
            {
                EnterState(AgentState.Socializing);
            }
            else if (_cumulativeWalkTime / _config.fatigueTimeConstant > 0.5f && roll < 0.7f)
            {
                EnterState(AgentState.Resting);
            }
            else
            {
                // Keep roaming with a fresh timer
                _stateTimer = Random.Range(30f, 120f);
            }
        }
    }


    // ── STATE: Socializing ──────────────────────────────────────────

    private void TickSocializing(float dt)
    {
        _nav.isStopped = true;
        _stateTimer -= dt;
        if (_stateTimer <= 0f)
        {
            _nav.isStopped = false;
            EnterState(Random.value < 0.5f ? AgentState.Transit : AgentState.Roaming);
        }
    }


    // ── STATE: Resting ──────────────────────────────────────────────

    private void TickResting(float dt)
    {
        _nav.isStopped = true;
        _stateTimer -= dt;

        // Resting partially recovers fatigue
        _cumulativeWalkTime = Mathf.Max(0f, _cumulativeWalkTime - dt * 0.5f);

        if (_stateTimer <= 0f)
        {
            _nav.isStopped = false;
            EnterState(AgentState.Transit);
        }
    }


    // ── STATE: Exiting ──────────────────────────────────────────────

    private void TickExiting(float dt)
    {
        if (_nav.remainingDistance < 1.0f && !_nav.pathPending)
        {
            // Reached exit — destroy agent
            Destroy(gameObject);
        }
    }


    // ── State Transitions ───────────────────────────────────────────

    private void EnterState(AgentState newState)
    {
        CurrentState = newState;

        switch (newState)
        {
            case AgentState.Transit:
                _nav.isStopped = false;
                _nav.speed = _currentDesiredSpeed;
                SetNavDestination(_goalTransform.position);
                break;

            case AgentState.Dwelling:
                _visitCount++;
                _nav.isStopped = true;
                // ── Phase 4.2: Dwell timer ──────────────────────────
                // Use CSV avg_dwell_sec if available, otherwise sample log-normal
                // Apply visit-number decay: DwellTime(n) = DwellTime_0 * n^(-delta)
                float baseDwell = _avgDwellFromCSV > 0f
                    ? _avgDwellFromCSV
                    : SampleLogNormalDwell();
                float decayMultiplier = Mathf.Pow(_visitCount, -_config.dwellDecayExponent);
                _dwellDuration = baseDwell * decayMultiplier;
                _stateTimer = _dwellDuration;
                break;

            case AgentState.Roaming:
                _nav.isStopped = false;
                _stateTimer = Random.Range(30f, 180f);
                _roamHeading = Random.Range(0f, 360f);
                break;

            case AgentState.Socializing:
                _nav.isStopped = true;
                _stateTimer = Random.Range(_config.socializeDurationMin, _config.socializeDurationMax);
                break;

            case AgentState.Resting:
                _nav.isStopped = true;
                _stateTimer = Random.Range(_config.restDurationMin, _config.restDurationMax);
                break;

            case AgentState.Exiting:
                _nav.isStopped = false;
                _nav.speed = _currentDesiredSpeed;
                Transform nearestExit = GetNearestExit();
                if (nearestExit != null)
                    SetNavDestination(nearestExit.position);
                break;
        }
    }

    /// <summary>Called by CrowdManager when a departure is scheduled for this agent.</summary>
    public void RouteToExit()
    {
        EnterState(AgentState.Exiting);
    }


    // ══════════════════════════════════════════════════════════════════
    // ██  SOCIAL FORCE MODEL IMPLEMENTATION  ██████████████████████████
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes the resultant Social Force and overrides NavMeshAgent.velocity.
    /// Called every FixedUpdate when the agent is in a moving state.
    ///
    /// F_total = F_drive + Σ F_ij(social) + Σ F_iW(wall)
    ///
    /// References:
    ///   Helbing & Molnar (1995) — Physical Review E, 51(5), 4282–4286
    ///   Helbing, Farkas & Vicsek (2000) — Nature, 407, 487–490
    /// </summary>
    private void ApplySocialForces(float dt)
    {
        Vector3 pos = transform.position;
        Vector3 vel = _nav.velocity;

        // ── 1. DRIVING FORCE ────────────────────────────────────────
        // F_drive = m * (v0 * e0 - v) / tau
        Vector3 desiredVelocity;
        if (_nav.hasPath && _nav.path.corners.Length > 1)
        {
            // Use NavMesh next corner as immediate desired direction
            Vector3 nextCorner = _nav.path.corners[Mathf.Min(1, _nav.path.corners.Length - 1)];
            _desiredDirection = (nextCorner - pos).normalized;
        }
        else
        {
            _desiredDirection = (_nav.destination - pos).normalized;
        }

        desiredVelocity = _desiredDirection * _currentDesiredSpeed;
        Vector3 drivingForce = _config.agentMass *
            (desiredVelocity - vel) / _config.relaxationTime;

        // ── 2. AGENT-AGENT REPULSIVE FORCES ─────────────────────────
        Vector3 socialForce = Vector3.zero;
        float myRadius = _config.agentRadius;

        foreach (var other in _allAgents)
        {
            if (other == this || other == null) continue;

            Vector3 diff = pos - other.transform.position;
            float dist = diff.magnitude;
            if (dist > 5f) continue;  // skip distant agents for performance
            if (dist < 0.001f) continue; // skip self-overlap

            Vector3 nij = diff / dist; // unit normal from j to i
            float rij = myRadius + _config.agentRadius; // sum of radii
            float gap = rij - dist; // positive = overlapping

            // ── Exponential repulsion ───────────────────────────────
            float repulsionMag = _config.socialForceA *
                Mathf.Exp(gap / _config.socialForceB);

            // ── Anisotropy weighting ────────────────────────────────
            // w = lambda + (1 - lambda) * (1 + cos(phi)) / 2
            float cosPhi = -Vector3.Dot(_desiredDirection, nij);
            float w = _config.anisotropyLambda +
                (1f - _config.anisotropyLambda) * (1f + cosPhi) / 2f;

            Vector3 fSocial = w * repulsionMag * nij;

            // ── Body compression + friction (only when overlapping) ─
            if (gap > 0f)
            {
                // Compression: k * g(rij - dij) * n_ij
                fSocial += _config.bodyCompressionK * gap * nij;

                // Sliding friction: kappa * g(rij - dij) * Δv_t * t_ij
                Vector3 tij = new Vector3(-nij.z, 0, nij.x); // tangent
                float deltaVt = Vector3.Dot(other.GetComponent<NavMeshAgent>().velocity - vel, tij);
                fSocial += _config.slidingFrictionKappa * gap * deltaVt * tij;
            }

            socialForce += fSocial;
        }

        // ── 3. WALL REPULSIVE FORCES ────────────────────────────────
        Vector3 wallForce = ComputeWallForce(pos, vel);

        // ── 4. RESULTANT → VELOCITY ─────────────────────────────────
        Vector3 totalForce = drivingForce + socialForce + wallForce;
        Vector3 acceleration = totalForce / _config.agentMass;

        Vector3 newVel = vel + acceleration * dt;

        // Clamp to maximum speed (1.3x desired to allow brief spurts)
        float maxSpeed = _currentDesiredSpeed * 1.3f;
        if (newVel.magnitude > maxSpeed)
            newVel = newVel.normalized * maxSpeed;

        // Keep on ground plane
        newVel.y = 0f;

        // Override NavMeshAgent velocity
        _nav.velocity = newVel;
    }

    /// <summary>
    /// Compute repulsive wall force using NavMesh edge sampling.
    /// Casts short rays in a fan to detect nearby walls/obstacles.
    ///
    /// F_iW = A * exp((r_i - d_iW) / B) * n_iW
    /// </summary>
    private Vector3 ComputeWallForce(Vector3 pos, Vector3 vel)
    {
        Vector3 totalWallForce = Vector3.zero;
        int rayCount = 8;
        float rayLength = 2.0f;

        for (int r = 0; r < rayCount; r++)
        {
            float angle = (360f / rayCount) * r;
            Vector3 dir = Quaternion.Euler(0, angle, 0) * Vector3.forward;
            Ray ray = new Ray(pos + Vector3.up * 0.5f, dir);

            if (Physics.Raycast(ray, out RaycastHit hit, rayLength))
            {
                float distToWall = hit.distance;
                float gap = _config.agentRadius - distToWall;
                Vector3 niW = -dir; // normal pointing away from wall

                // Exponential repulsion
                float repulsion = _config.socialForceA *
                    Mathf.Exp((_config.agentRadius - distToWall) / _config.socialForceB);
                totalWallForce += repulsion * niW;

                // Body compression if overlapping wall
                if (gap > 0f)
                {
                    totalWallForce += _config.bodyCompressionK * gap * niW;

                    // Sliding friction against wall
                    Vector3 tiW = new Vector3(-niW.z, 0, niW.x);
                    float vTangent = Vector3.Dot(vel, tiW);
                    totalWallForce -= _config.slidingFrictionKappa * gap * vTangent * tiW;
                }
            }
        }

        return totalWallForce;
    }


    // ── Utility Methods ─────────────────────────────────────────────

    private void SetNavDestination(Vector3 target)
    {
        NavMeshHit hit;
        if (NavMesh.SamplePosition(target, out hit, 5f, NavMesh.AllAreas))
        {
            _nav.SetDestination(hit.position);
        }
    }

    private Transform GetNearestExit()
    {
        if (_exitPoints == null || _exitPoints.Length == 0) return null;
        Transform nearest = _exitPoints[0];
        float minDist = float.MaxValue;
        foreach (var exit in _exitPoints)
        {
            float d = Vector3.Distance(transform.position, exit.position);
            if (d < minDist) { minDist = d; nearest = exit; }
        }
        return nearest;
    }

    /// <summary>
    /// Sample a dwell time from a log-normal distribution.
    /// Parameters from SimConfig: mu ≈ 5.7 (ln(300s)), sigma ≈ 0.8
    /// </summary>
    private float SampleLogNormalDwell()
    {
        float z = GaussianRandom();
        float lnT = _config.dwellMu + _config.dwellSigma * z;
        return Mathf.Exp(lnT);
    }

    /// <summary>Box-Muller transform for standard normal variate.</summary>
    private static float GaussianRandom()
    {
        float u1 = Random.Range(0.0001f, 1f);
        float u2 = Random.Range(0.0001f, 1f);
        return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
    }

    private float fatigueFactor()
    {
        float alpha = _config.minSpeedFraction;
        return alpha + (1f - alpha) * Mathf.Exp(-_cumulativeWalkTime / _config.fatigueTimeConstant);
    }
}
