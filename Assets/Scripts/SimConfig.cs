using System;
using UnityEngine;

/// <summary>
/// ScriptableObject holding every tunable parameter for the pedestrian simulation.
/// Create via: Assets > Create > Conference Sim > SimConfig
///
/// Drag the created asset onto CrowdManager's "Config" field in the Inspector.
/// If no asset is assigned, CrowdManager creates a default instance automatically.
/// </summary>
[CreateAssetMenu(fileName = "SimConfig", menuName = "Conference Sim/SimConfig")]
public class SimConfig : ScriptableObject
{
    // ── SOCIAL FORCE MODEL (Helbing et al.) ─────────────────────────

    [Header("Social Force Model")]
    [Tooltip("Free-flow walking speed m/s. Weidmann (1993): 1.34 ± 0.26")]
    public float desiredSpeed = 1.34f;

    [Tooltip("Relaxation time s — how quickly agents adjust velocity. Helbing (1995): 0.5")]
    public float relaxationTime = 0.50f;

    [Tooltip("Agent shoulder half-width m. Weidmann (1993): 0.2–0.3")]
    public float agentRadius = 0.25f;

    [Tooltip("Repulsive interaction strength N. Helbing (2000): 2000")]
    public float socialForceA = 2000f;

    [Tooltip("Repulsive interaction range m. Helbing (2000): 0.08")]
    public float socialForceB = 0.08f;

    [Tooltip("Body compression coefficient kg/s². Helbing (2000): 1.2e5")]
    public float bodyCompressionK = 1.2e5f;

    [Tooltip("Sliding friction coefficient kg/(m·s). Helbing (2000): 2.4e5")]
    public float slidingFrictionKappa = 2.4e5f;

    [Tooltip("Anisotropy — agents react more to what is ahead. Helbing (1995): 0.5")]
    [Range(0f, 1f)]
    public float anisotropyLambda = 0.50f;

    [Tooltip("Agent mass kg.")]
    public float agentMass = 80f;


    // ── FATIGUE MODEL ───────────────────────────────────────────────

    [Header("Fatigue Model")]
    [Tooltip("Fatigue time constant s (~120 min active walking).")]
    public float fatigueTimeConstant = 7200f;

    [Tooltip("Minimum speed fraction (asymptotic floor). 0.75 = never below 75% of v₀.")]
    [Range(0.5f, 1f)]
    public float minSpeedFraction = 0.75f;

    [Tooltip("Dwell decay exponent per visit. DwellTime(n) = DwellTime₀ × n^(−δ), δ ≈ 0.2")]
    public float dwellDecayExponent = 0.20f;


    // ── DWELL TIME (Log-Normal) ─────────────────────────────────────

    [Header("Dwell Time (Log-Normal)")]
    [Tooltip("μ parameter for log-normal dwell. ln(300s) ≈ 5.7")]
    public float dwellMu = 5.70f;

    [Tooltip("σ parameter for log-normal dwell.")]
    public float dwellSigma = 0.80f;


    // ── ROAMING BEHAVIOR ────────────────────────────────────────────

    [Header("Roaming (Ornstein-Uhlenbeck)")]
    [Tooltip("Exhibition browsing speed range m/s. Teknomo (2006): 0.6–1.0")]
    public float roamSpeedMin = 0.40f;
    public float roamSpeedMax = 0.90f;

    [Tooltip("β₀ impedance for gravity-model zone selection (1/m). Higher = agents prefer nearer zones.")]
    public float gravityBeta0 = 0.15f;

    [Tooltip("Fatigue bias on zone selection (γ_f). Multiplies β₀ as agent tires. Reference: 0.5–1.0")]
    [Range(0f, 2f)]
    public float fatigueZoneBias = 0.75f;

    [Tooltip("Mean reversion rate toward zone center (1/s).")]
    public float roamReorientRate = 0.50f;

    [Tooltip("Angular noise intensity (rad/s).")]
    public float roamDirectionNoise = 0.30f;


    // ── DENSITY & STATE THRESHOLDS ──────────────────────────────────

    [Header("Density Thresholds")]
    [Tooltip("Density (persons/m²) at the centre of the sigmoid avoidance curve.")]
    public float boothCrowdThreshold = 2.0f;

    [Tooltip("Sigmoid sensitivity — higher = sharper cliff between 'enter' and 'avoid'.")]
    public float crowdingSigmoidSensitivity = 3.0f;

    [Tooltip("Approach radius m — density check starts at this distance from zone center.")]
    public float boothDensityRadius = 3.0f;

    [Tooltip("Socializing duration min/max (s).")]
    public float socializeDurationMin = 60f;
    public float socializeDurationMax = 600f;

    [Tooltip("Resting duration min/max (s).")]
    public float restDurationMin = 300f;
    public float restDurationMax = 1200f;


    // ── AGENT PERSONAS ──────────────────────────────────────────────

    [Header("Agent Personas")]
    [Tooltip("One entry per persona type. Spawn weights are normalised at runtime.")]
    public PersonaConfig[] personas = new PersonaConfig[]
    {
        new PersonaConfig
        {
            name = "Researcher", spawnWeight = 0.40f,
            speedMult = 0.95f, dwellMult = 1.40f, socializeMult = 0.80f,
            fatigueMult = 1.00f, visitPenaltyMult = 1.00f,
            preferredTopics = new[] { "NASH", "HCC", "cirrhosis" }
        },
        new PersonaConfig
        {
            name = "Networker", spawnWeight = 0.25f,
            speedMult = 1.05f, dwellMult = 0.80f, socializeMult = 1.80f,
            fatigueMult = 0.85f, visitPenaltyMult = 0.60f,
            preferredTopics = new[] { "transplant", "industry", "networking" }
        },
        new PersonaConfig
        {
            name = "Student", spawnWeight = 0.20f,
            speedMult = 1.10f, dwellMult = 1.00f, socializeMult = 1.20f,
            fatigueMult = 1.10f, visitPenaltyMult = 0.80f,
            preferredTopics = new[] { "viral-hepatitis", "metabolic", "NASH" }
        },
        new PersonaConfig
        {
            name = "Industry", spawnWeight = 0.10f,
            speedMult = 1.00f, dwellMult = 0.90f, socializeMult = 1.40f,
            fatigueMult = 0.90f, visitPenaltyMult = 0.70f,
            preferredTopics = new[] { "industry", "transplant", "HCC" }
        },
        new PersonaConfig
        {
            name = "BoothStaff", spawnWeight = 0.05f,
            speedMult = 0.70f, dwellMult = 3.00f, socializeMult = 0.50f,
            fatigueMult = 0.50f, visitPenaltyMult = 2.00f,
            preferredTopics = new[] { "exhibit" }
        },
    };


    // ── DAY-PHASE RHYTHM ────────────────────────────────────────────

    [Header("Day-Phase Rhythm")]
    [Tooltip("Defines how agent behaviour changes across the conference day. Hours are 24-h clock floats.")]
    public DayPhaseConfig[] dayPhases = new DayPhaseConfig[]
    {
        new DayPhaseConfig { name = "Opening",   startHour =  8.0f, endHour =  9.5f, speedMult = 1.15f, socializeMult = 0.70f, restMult = 0.50f },
        new DayPhaseConfig { name = "Morning",   startHour =  9.5f, endHour = 12.0f, speedMult = 1.00f, socializeMult = 1.00f, restMult = 1.00f },
        new DayPhaseConfig { name = "Lunch",     startHour = 12.0f, endHour = 14.0f, speedMult = 0.80f, socializeMult = 1.30f, restMult = 1.50f },
        new DayPhaseConfig { name = "Afternoon", startHour = 14.0f, endHour = 17.0f, speedMult = 0.90f, socializeMult = 1.10f, restMult = 1.20f },
        new DayPhaseConfig { name = "Evening",   startHour = 17.0f, endHour = 22.0f, speedMult = 0.75f, socializeMult = 1.50f, restMult = 1.80f },
    };


    // ── PHYSIOLOGICAL DRIVES ────────────────────────────────────────

    [Header("Physiological Drives")]
    [Tooltip("Hunger accumulation rate per second. Default ≈ 0.000028 → threshold reached in ~6.5 h.")]
    public float hungerRate = 0.000028f;

    [Tooltip("Thirst accumulation rate per second. Default ≈ 0.000056 → threshold reached in ~3 h.")]
    public float thirstRate = 0.000056f;

    [Tooltip("Hunger [0-1] at which agent overrides gravity model to seek a food zone.")]
    [Range(0f, 1f)]
    public float hungerThreshold = 0.65f;

    [Tooltip("Thirst [0-1] at which agent overrides gravity model to seek a drink zone.")]
    [Range(0f, 1f)]
    public float thirstThreshold = 0.60f;

    [Tooltip("Amount hunger is reduced when agent dwells at a food zone (0–1).")]
    [Range(0f, 1f)]
    public float hungerResetAmount = 0.70f;

    [Tooltip("Amount thirst is reduced when agent dwells at a drink zone (0–1).")]
    [Range(0f, 1f)]
    public float thirstResetAmount = 0.80f;

    [Tooltip("Zone type tag that identifies food zones (must match ConferenceZone.zoneType).")]
    public string foodZoneTag = "food";

    [Tooltip("Zone type tag that identifies drink zones (must match ConferenceZone.zoneType).")]
    public string drinkZoneTag = "drink";


    // ── SOCIAL GROUPS ───────────────────────────────────────────────

    [Header("Social Groups")]
    [Tooltip("Poisson λ for group size. 1.0 = mostly solo; 2.3 ≈ pairs dominate.")]
    public float groupSizeLambda = 2.3f;

    [Tooltip("Hard cap on group size.")]
    public int groupSizeMax = 6;

    [Tooltip("Cohesion force strength (N) applied to keep group members together.")]
    public float groupCohesionStrength = 80f;

    [Tooltip("Separation at which cohesion force starts (m).")]
    public float groupSeparationMin = 1.5f;

    [Tooltip("Separation at which cohesion force is fully engaged (m).")]
    public float groupSeparationMax = 4.0f;

    [Tooltip("Probability that a group member joins a peer who starts socializing.")]
    [Range(0f, 1f)]
    public float groupSocializeContagion = 0.70f;


    // ── SESSION REDIRECT ────────────────────────────────────────────

    [Header("Session Redirects")]
    [Tooltip("If true, agents finish their current activity before acting on a session redirect.")]
    public bool smoothSessionRedirects = true;

    [Tooltip("After this many seconds an agent will force-apply a pending redirect even if still busy.")]
    public float redirectMaxDelay = 120f;


    // ── AUTO-CALIBRATION ────────────────────────────────────────────

    [Header("Auto-Calibration")]
    [Tooltip("If true, CalibrationManager adjusts gravity parameters after each run.")]
    public bool calibrationEnabled = true;

    [Tooltip("Proportional control learning rate (fraction of bias corrected per run).")]
    [Range(0.001f, 0.2f)]
    public float calibrationLearningRate = 0.05f;

    [Tooltip("Max change allowed per calibration step as fraction of current value.")]
    [Range(0.01f, 0.5f)]
    public float calibrationMaxAdjustment = 0.15f;


    // ── AGENT STATE COLORS ─────────────────────────────────────────

    [Header("Agent State Colors")]
    [Tooltip("Color applied to agents in Transit state.")]
    public Color colorTransit = new Color(0.20f, 0.60f, 1.00f, 1f);      // blue

    [Tooltip("Color applied to agents in Dwelling state.")]
    public Color colorDwelling = new Color(0.20f, 0.85f, 0.40f, 1f);     // green

    [Tooltip("Color applied to agents in Roaming state.")]
    public Color colorRoaming = new Color(1.00f, 0.75f, 0.20f, 1f);      // amber

    [Tooltip("Color applied to agents in Socializing state.")]
    public Color colorSocializing = new Color(0.90f, 0.30f, 0.70f, 1f);  // pink

    [Tooltip("Color applied to agents in Resting state.")]
    public Color colorResting = new Color(0.60f, 0.45f, 0.90f, 1f);      // purple

    [Tooltip("Color applied to agents in Exiting state.")]
    public Color colorExiting = new Color(0.85f, 0.25f, 0.25f, 1f);      // red


    // ── ANALYTICS ───────────────────────────────────────────────────

    [Header("Analytics")]
    [Tooltip("How often positions are logged (s).")]
    public float analyticsLogInterval = 0.50f;

    [Tooltip("Output path relative to Application.dataPath.")]
    public string analyticsExportPath = "SimOutput/spatial_log.csv";

    public string validationExportPath  = "SimOutput/validation_report.json";
    public string calibrationExportPath = "SimOutput/calibration_log.json";
}


// ── Supporting structs ───────────────────────────────────────────────

[Serializable]
public class PersonaConfig
{
    [Tooltip("Display name shown in HUD and logs.")]
    public string name = "Persona";

    [Tooltip("Relative spawn probability (normalised with other personas at runtime).")]
    [Range(0f, 1f)]
    public float spawnWeight = 0.20f;

    [Tooltip("Multiplier on base desiredSpeed.")]
    public float speedMult = 1.0f;

    [Tooltip("Multiplier on log-normal dwell duration.")]
    public float dwellMult = 1.0f;

    [Tooltip("Multiplier on socializing duration.")]
    public float socializeMult = 1.0f;

    [Tooltip("Multiplier on fatigueTimeConstant (>1 = less fatigue).")]
    public float fatigueMult = 1.0f;

    [Tooltip("Multiplier on the visit-repeat penalty in the gravity model (>1 = avoids revisiting more strongly).")]
    public float visitPenaltyMult = 1.0f;

    [Tooltip("Topic tags this persona is interested in. Must match ConferenceZone.topicTags entries.")]
    public string[] preferredTopics = new string[0];
}

[Serializable]
public class DayPhaseConfig
{
    [Tooltip("Display name.")]
    public string name = "Phase";

    [Tooltip("Phase begins at this hour of day (24-h float, e.g. 9.5 = 09:30).")]
    public float startHour = 9.0f;

    [Tooltip("Phase ends at this hour of day.")]
    public float endHour = 12.0f;

    [Tooltip("Walking speed multiplier during this phase.")]
    public float speedMult = 1.0f;

    [Tooltip("Socializing duration multiplier.")]
    public float socializeMult = 1.0f;

    [Tooltip("Resting probability multiplier.")]
    public float restMult = 1.0f;
}
