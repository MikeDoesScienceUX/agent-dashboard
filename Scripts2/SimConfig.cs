using UnityEngine;

/// <summary>
/// ScriptableObject holding every tunable parameter for the pedestrian simulation.
/// Create via Assets > Create > Simulation > SimConfig.
/// All baseline values are sourced from peer-reviewed pedestrian dynamics literature.
/// See: Pedestrian_Dynamics_Reference.md for full citations.
/// </summary>
[CreateAssetMenu(fileName = "SimConfig", menuName = "Simulation/SimConfig")]
public class SimConfig : ScriptableObject
{
    [Header("=== SOCIAL FORCE MODEL (Helbing et al.) ===")]

    [Tooltip("Free-flow desired walking speed in m/s. Weidmann (1993): 1.34 ± 0.26")]
    public float desiredSpeed = 1.34f;

    [Tooltip("Browsing/exhibition desired speed in m/s. Reduced for indoor scanning.")]
    public float desiredSpeedExhibit = 0.80f;

    [Tooltip("Relaxation time in seconds. How quickly agents adjust velocity. Helbing & Molnar (1995): 0.5s")]
    public float relaxationTime = 0.50f;

    [Tooltip("Agent shoulder half-width in meters. Weidmann (1993): 0.2–0.3m")]
    public float agentRadius = 0.25f;

    [Tooltip("Repulsive interaction strength in Newtons. Helbing et al. (2000): 2000N")]
    public float socialForceA = 2000f;

    [Tooltip("Repulsive interaction range in meters. Helbing et al. (2000): 0.08m")]
    public float socialForceB = 0.08f;

    [Tooltip("Body compression coefficient in kg/s^2. Helbing et al. (2000): 1.2e5")]
    public float bodyCompressionK = 1.2e5f;

    [Tooltip("Sliding friction coefficient in kg/(m*s). Helbing et al. (2000): 2.4e5")]
    public float slidingFrictionKappa = 2.4e5f;

    [Tooltip("Anisotropy factor. Agents react more to what's ahead. Helbing (1995): 0.5")]
    [Range(0f, 1f)]
    public float anisotropyLambda = 0.50f;

    [Tooltip("Jam density in persons/m^2. Seyfried et al. (2005): 5.4")]
    public float jamDensity = 5.4f;

    [Tooltip("Agent mass in kg. Average adult.")]
    public float agentMass = 80f;


    [Header("=== FATIGUE MODEL ===")]

    [Tooltip("Fatigue time constant in seconds. ~120 min of active walking.")]
    public float fatigueTimeConstant = 7200f;

    [Tooltip("Minimum speed fraction (asymptotic floor). Agents never slow below this fraction of v_0.")]
    [Range(0.5f, 1f)]
    public float minSpeedFraction = 0.75f;

    [Tooltip("Power-law exponent for dwell time decay per visit number. delta ≈ 0.15–0.30")]
    public float dwellDecayExponent = 0.20f;

    [Tooltip("Fatigue sensitivity multiplier for gravity model beta increase over session.")]
    public float fatigueGammaF = 0.75f;


    [Header("=== DWELL TIME (Log-Normal Distribution) ===")]

    [Tooltip("Mu parameter for log-normal dwell time (in seconds). ln(300) ≈ 5.7")]
    public float dwellMu = 5.70f;

    [Tooltip("Sigma parameter for log-normal dwell time.")]
    public float dwellSigma = 0.80f;


    [Header("=== ROAMING BEHAVIOR ===")]

    [Tooltip("Minimum roaming speed in m/s.")]
    public float roamSpeedMin = 0.30f;

    [Tooltip("Maximum roaming speed in m/s.")]
    public float roamSpeedMax = 0.60f;

    [Tooltip("Ornstein-Uhlenbeck mean reversion rate for roaming direction (1/s).")]
    public float roamReorientRate = 0.50f;

    [Tooltip("Ornstein-Uhlenbeck noise intensity for roaming direction (rad/s).")]
    public float roamDirectionNoise = 0.30f;


    [Header("=== O-D GRAVITY MODEL ===")]

    [Tooltip("Impedance parameter for deterrence function f(c) = exp(-beta * c). Units: 1/meter. Calibrate to venue.")]
    public float betaImpedance = 0.15f;


    [Header("=== SPAWNING ===")]

    [Tooltip("Simulation time step in seconds. Decoupled from Unity framerate.")]
    public float simTickInterval = 0.10f;

    [Tooltip("CSV polling interval — how often CrowdManager checks for new data rows (seconds).")]
    public float csvPollInterval = 5.0f;


    [Header("=== DENSITY & STATE THRESHOLDS ===")]

    [Tooltip("Maximum occupancy density (persons/m^2) at a booth before agents begin roaming away.")]
    public float boothCrowdThreshold = 2.0f;

    [Tooltip("Radius around booth center used to compute local density (meters).")]
    public float boothDensityRadius = 3.0f;

    [Tooltip("Socializing cluster duration range — minimum (seconds).")]
    public float socializeDurationMin = 60f;

    [Tooltip("Socializing cluster duration range — maximum (seconds).")]
    public float socializeDurationMax = 600f;

    [Tooltip("Resting duration range — minimum (seconds).")]
    public float restDurationMin = 300f;

    [Tooltip("Resting duration range — maximum (seconds).")]
    public float restDurationMax = 1200f;


    [Header("=== ANALYTICS ===")]

    [Tooltip("How often AnalyticsManager logs all agent positions (seconds).")]
    public float analyticsLogInterval = 0.50f;

    [Tooltip("File path for exported spatial log (relative to Application.dataPath).")]
    public string analyticsExportPath = "SimOutput/spatial_log.csv";

    [Tooltip("File path for exported RMSE validation report.")]
    public string validationExportPath = "SimOutput/validation_report.json";
}
