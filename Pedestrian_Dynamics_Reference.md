# Pedestrian Dynamics Reference for Conference Digital Twin

## A Research-Grade Parameter & Algorithm Reference for Unity Simulation

---

## 1. Origin-Destination (O-D) Matrix Estimation from Local Counts

Your Posterbuddy sensors give you **node-level headcounts** (density snapshots at booths), not full origin-destination paths. The standard approach to reconstruct an O-D matrix from incomplete observations uses constrained entropy maximization, gravity models, or iterative proportional fitting (IPF).

### 1.1 The Entropy Maximization Model (Wilson, 1967/1970)

Wilson showed that the statistically most likely trip matrix is the one that maximizes the entropy function subject to known constraints. This is the foundational algorithm for your use case.

**Objective function — maximize:**

```
S = -sum_ij [ T_ij * ln(T_ij) - T_ij ]
```

Subject to:

```
sum_j T_ij = O_i    (total trips originating from zone i)
sum_i T_ij = D_j    (total trips arriving at zone j — your Posterbuddy headcounts)
sum_ij T_ij * c_ij = C  (total system travel cost constraint)
```

Where `T_ij` is the number of trips from origin `i` to destination `j`, and `c_ij` is the generalized cost (distance or travel time) between zones.

**Solution (doubly-constrained gravity form):**

```
T_ij = A_i * B_j * O_i * D_j * f(c_ij)
```

Where:

- `f(c_ij) = exp(-beta * c_ij)` — the deterrence function (exponential or power form)
- `A_i = 1 / sum_j [ B_j * D_j * f(c_ij) ]` — origin balancing factor
- `B_j = 1 / sum_i [ A_i * O_i * f(c_ij) ]` — destination balancing factor
- `beta` — impedance parameter calibrated to your venue (typically 0.1–0.5 for indoor venues where distances are short, in units of 1/meter)

**Iterative Proportional Fitting (IPF) — Furness method:**

Initialize `A_i = B_j = 1.0`, then iterate:

```
Step 1: A_i = O_i / sum_j [ B_j * D_j * f(c_ij) ]
Step 2: B_j = D_j / sum_i [ A_i * O_i * f(c_ij) ]
Repeat until |A_i(k) - A_i(k-1)| < epsilon for all i
```

Convergence is typically achieved in 5–15 iterations.

**Citation:** Wilson, A.G. (1967). "A statistical theory of spatial distribution models." *Transportation Research*, 1(3), 253–269. Wilson, A.G. (1970). *Entropy in Urban and Regional Modelling*. Pion, London.

### 1.2 Van Zuylen & Willumsen Maximum Entropy (ME2)

When you only have **link counts** (people passing through hallway segments) and **destination counts** but no origin totals, Van Zuylen & Willumsen's formulation is more appropriate.

**Objective — maximize:**

```
S = -sum_ij [ T_ij * ln(T_ij / t_ij) - T_ij + t_ij ]
```

Where `t_ij` is a prior (seed) matrix — your initial estimate or uniform distribution.

Subject to:

```
sum_ij T_ij * p_ij_a = V_a   for all observed links a
```

Where `p_ij_a` is the proportion of trips from `i` to `j` using link `a`, and `V_a` is the observed count on link `a`.

**Solution via Lagrange multipliers:**

```
T_ij = t_ij * product_a [ exp(-lambda_a * p_ij_a) ]
```

The `lambda_a` multipliers are solved iteratively.

**Citation:** Van Zuylen, H.J. & Willumsen, L.G. (1980). "The most likely trip matrix estimated from traffic counts." *Transportation Research Part B*, 14(3), 281–293.

### 1.3 Practical Application to Posterbuddy Data

Your specific constraint set:

- **Known:** `D_j` (headcount at each booth from Posterbuddy cameras)
- **Known:** `c_ij` (walking distance between nodes, computable from your Unity NavMesh)
- **Unknown:** `O_i` (spawn rates at each entrance)
- **Unknown:** Full `T_ij` matrix

**Recommended approach:**

1. Estimate total attendance `N` from venue records.
2. Set `O_i` proportional to entrance capacity (or assume single entrance as shown in the video).
3. Use `D_j` from Posterbuddy as destination constraints.
4. Compute `c_ij` as NavMesh path distances between every entrance-booth pair.
5. Run IPF with `f(c_ij) = exp(-beta * c_ij)`.
6. Calibrate `beta` by adjusting until simulated hallway densities match any hallway-level observations you have.

---

## 2. Social Force Model — Calibrated Parameters

### 2.1 Core Equation of Motion

The movement of pedestrian `i` is governed by:

```
m_i * (dv_i/dt) = F_i_drive + sum_j F_ij_social + sum_W F_iW_wall
```

Where `m_i` is typically set to 80 kg (average adult).

### 2.2 Driving Force (Destination Attraction)

```
F_i_drive = m_i * (v_i_0 * e_i_0 - v_i) / tau_i
```

- `v_i_0` — desired speed (see below)
- `e_i_0` — unit vector toward current goal (from NavMesh path)
- `v_i` — current velocity
- `tau_i` — relaxation time

### 2.3 Pedestrian-Pedestrian Repulsive Force

From Helbing, Farkas & Vicsek (2000):

```
F_ij = { A_i * exp[(r_ij - d_ij) / B_i] + k * g(r_ij - d_ij) } * n_ij
       + kappa * g(r_ij - d_ij) * Delta_v_ji_t * t_ij
```

Where:

- `r_ij = r_i + r_j` — sum of agent radii
- `d_ij` — center-to-center distance between agents
- `n_ij` — unit normal vector pointing from `j` to `i`
- `t_ij` — unit tangential vector perpendicular to `n_ij`
- `g(x) = x` if `x > 0` (agents overlapping), else `g(x) = 0`
- `Delta_v_ji_t` — tangential velocity difference (sliding friction)

### 2.4 Wall Repulsive Force

```
F_iW = { A_i * exp[(r_i - d_iW) / B_i] + k * g(r_i - d_iW) } * n_iW
       + kappa * g(r_i - d_iW) * (v_i . t_iW) * t_iW
```

### 2.5 Accepted Baseline Parameters

The following values are drawn from Helbing & Molnar (1995), Helbing et al. (2000), Johansson et al. (2007), and calibration studies reviewed in Chraibi et al. (2018):

| Parameter | Symbol | Value | Unit | Source |
|---|---|---|---|---|
| **Desired speed (free flow)** | `v_0` | **1.34 ± 0.26** | m/s | Weidmann (1993); Seyfried et al. (2005) |
| **Desired speed (exhibition)** | `v_0` | **0.6 – 1.0** | m/s | Reduced for browsing; Teknomo (2006) |
| **Relaxation time** | `tau` | **0.5** | s | Helbing & Molnar (1995) |
| **Agent radius** | `r_i` | **0.2 – 0.3** | m | Shoulder half-width; Weidmann (1993) |
| **Social force strength** | `A_i` | **2000** | N | Helbing et al. (2000) |
| **Social force range** | `B_i` | **0.08** | m | Helbing et al. (2000) |
| **Body compression coeff.** | `k` | **1.2 × 10^5** | kg/s^2 | Helbing et al. (2000) |
| **Sliding friction coeff.** | `kappa` | **2.4 × 10^5** | kg/(m·s) | Helbing et al. (2000) |
| **Anisotropy factor** | `lambda` | **0.5** | — | Agents respond more to what's ahead; Helbing & Molnar (1995) |
| **Anisotropy exponent** | — | cos(phi_ij) weighted | — | Field of view ≈ 200° |

**Note on the anisotropy:** The repulsive force is modulated by a direction-dependent weight:

```
w(cos(phi_ij)) = lambda + (1 - lambda) * (1 + cos(phi_ij)) / 2
```

This means agents react more strongly to people in front of them than behind.

### 2.6 Speed-Density Relationship (Fundamental Diagram)

From Seyfried et al. (2005), for unidirectional flow:

```
v(rho) = v_0 * (1 - exp(-gamma * (1/rho - 1/rho_max)))
```

Where:

- `rho` — local density (persons/m^2)
- `rho_max` ≈ 5.4 persons/m^2 (jam density)
- `gamma` ≈ 1.913 m^2 (fitted parameter)
- `v_0` ≈ 1.34 m/s

At conference densities (typically 0.3–1.5 persons/m^2), this yields comfortable walking speeds between 0.8–1.3 m/s.

**Citation:** Seyfried, A., Steffen, B., Klingsch, W. & Boltes, M. (2005). "The fundamental diagram of pedestrian movement revisited." *Journal of Statistical Mechanics*, P10002.

### 2.7 Key References

- Helbing, D. & Molnar, P. (1995). "Social force model for pedestrian dynamics." *Physical Review E*, 51(5), 4282–4286.
- Helbing, D., Farkas, I. & Vicsek, T. (2000). "Simulating dynamical features of escape panic." *Nature*, 407, 487–490.
- Johansson, A., Helbing, D. & Shukla, P.K. (2007). "Specification of the social force pedestrian model by evolutionary adjustment to video tracking data." *Advances in Complex Systems*, 10(S2), 271–288.
- Weidmann, U. (1993). "Transporttechnik der Fussgänger." *Schriftenreihe des IVT*, 90, ETH Zürich.

---

## 3. Exhibition Behavior & Fatigue

### 3.1 Dwell Time at Posters

Research on museum and exhibition visitor behavior provides the closest analogs to conference poster sessions.

**Serrell's Sweep Rate Index (SRI):**

```
SRI = Exhibition_Area_sqft / Average_Total_Time_minutes
```

An SRI below 300 with >51% diligent visitors indicates exceptionally thorough engagement. For conference posters specifically:

| Metric | Value | Source |
|---|---|---|
| **Initial scan (walk-by)** | 8–15 seconds | "10-10 rule" (10 sec from 10 feet); poster design literature |
| **Engaged viewing (interested)** | 2–5 minutes | Serrell (1997); museum tracking studies |
| **Deep discussion at poster** | 5–15 minutes | Conference observation studies; Zacks & Friedman (2020) |
| **Percentage who stop** | 20–40% of passers-by | Bitgood (2006); exhibition attracting power |
| **Percentage of diligent visitors** | 25–51% | Serrell (1997) benchmark |

**Probability distribution for dwell time:** Dwell times at individual exhibits follow a **log-normal distribution**:

```
f(t) = (1 / (t * sigma * sqrt(2*pi))) * exp(-(ln(t) - mu)^2 / (2 * sigma^2))
```

For conference posters:

- `mu` ≈ 1.6 (ln of median ≈ 5 minutes = 300 seconds → mu ≈ 5.7 for seconds, or 1.6 for minutes)
- `sigma` ≈ 0.8

**Citation:** Serrell, B. (1997). "Paying attention: The duration and allocation of visitors' time in museum exhibitions." *Curator: The Museum Journal*, 40(2), 108–125. Bitgood, S. (2006). "An analysis of visitor circulation: Movement patterns and the general value principle." *Curator*, 49(4), 463–475.

### 3.2 Fatigue Model

Physical and cognitive fatigue over a multi-hour conference produces measurable effects on walking speed and route choice.

**Walking speed decay function:**

```
v(t) = v_0 * [alpha + (1 - alpha) * exp(-t / T_fatigue)]
```

Where:

- `v_0` — initial desired speed
- `t` — cumulative walking time (not clock time)
- `alpha` — minimum speed fraction (asymptotic floor), typically **0.7–0.8** (people don't slow below 70–80% of their starting pace)
- `T_fatigue` — fatigue time constant, approximately **90–120 minutes** of active walking

This means after 2 hours of continuous walking, an agent's speed has dropped to roughly 75–80% of their initial speed.

**Cognitive fatigue and route choice (Museum Fatigue — Gilman, 1916; Bitgood, 2009):**

Museum fatigue research shows that over time, visitors:

- Spend less time at each subsequent exhibit (dwell time decay)
- Skip more exhibits (selectivity increases)
- Gravitate toward exits or rest areas
- Show right-turn bias in navigation

**Dwell time decay over session:**

```
DwellTime(n) = DwellTime_0 * n^(-delta)
```

Where `n` is the exhibit visit number (1st, 2nd, 3rd...) and `delta` ≈ 0.15–0.30 (power-law decay). The 10th poster visited gets roughly 50–70% of the dwell time of the 1st poster.

**Route choice fatigue modifier:** As fatigue accumulates, increase the `beta` parameter in the gravity model (Section 1), making agents more sensitive to distance. Fatigued agents will preferentially visit nearby booths rather than distant ones:

```
beta_effective(t) = beta_0 * (1 + gamma_f * t / T_session)
```

Where `gamma_f` ≈ 0.5–1.0 and `T_session` is the total session duration (4–8 hours).

**Citation:** Bitgood, S. (2009). "Museum fatigue: A critical review." *Visitor Studies*, 12(2), 93–111. Gilman, B.I. (1916). "Museum fatigue." *The Scientific Monthly*, 2(1), 62–74.

### 3.3 Ambient Roaming Behavior

Not all movement is goal-directed. At conferences, a significant fraction of agent time is spent in "ambient roaming" — lingering, socializing, or passively scanning.

**Behavioral state machine for each agent:**

```
States:
  TRANSIT     — Moving to a specific goal (booth). Uses NavMesh + SFM.
  DWELLING    — Stationary at a booth. Duration sampled from log-normal.
  ROAMING     — Slow random walk in current zone. Speed = 0.3–0.6 m/s.
  SOCIALIZING — Stationary cluster of 2–4 agents. Duration = 1–10 min.
  RESTING     — Stationary at rest area. Duration = 5–20 min.
```

**Transition probabilities (per decision point):**

| From → To | Probability | Condition |
|---|---|---|
| TRANSIT → DWELLING | 0.7–0.9 | Arrived at goal booth |
| TRANSIT → ROAMING | 0.1–0.3 | Passed goal without stopping |
| DWELLING → TRANSIT | 0.5 | Has more goals in itinerary |
| DWELLING → ROAMING | 0.3 | Finished at booth, browsing nearby |
| DWELLING → SOCIALIZING | 0.2 | Engages in conversation |
| ROAMING → TRANSIT | 0.4 | Selects new goal |
| ROAMING → DWELLING | 0.3 | Discovers interesting nearby poster |
| ROAMING → SOCIALIZING | 0.2 | Encounters someone |
| ROAMING → RESTING | 0.1 | Fatigue threshold exceeded |
| SOCIALIZING → TRANSIT | 0.5 | Resumes schedule |
| SOCIALIZING → ROAMING | 0.5 | Continues browsing area |

**Roaming velocity model:**

```
v_roam = v_0 * Uniform(0.2, 0.5)
direction: Ornstein-Uhlenbeck process (mean-reverting random walk)
  d(theta)/dt = -k_theta * (theta - theta_mean) + sigma_theta * dW
```

Where `k_theta` ≈ 0.5/s (how quickly they reorient), `sigma_theta` ≈ 0.3 rad/s (randomness), and `theta_mean` is the bearing toward the zone centroid (keeps them from wandering into walls).

---

## 4. Validation Framework

### 4.1 The Fundamental Diagram Test

Your simulation MUST reproduce the empirical fundamental diagram. Plot simulated `speed vs. density` and compare against Seyfried et al.'s empirical curves.

**Acceptance criterion:** The simulated v(rho) curve should fall within one standard deviation of the empirical data from Seyfried et al. (2005) across the density range 0–4 persons/m^2.

### 4.2 Heatmap Comparison — Spatial Correlation

To compare your simulated spatial density heatmap `H_sim(x,y)` against a Posterbuddy-derived ground truth `H_obs(x,y)`:

**Pearson Spatial Correlation Coefficient:**

```
r = sum_xy [(H_sim(x,y) - H_sim_bar) * (H_obs(x,y) - H_obs_bar)]
    / sqrt[ sum_xy (H_sim(x,y) - H_sim_bar)^2 * sum_xy (H_obs(x,y) - H_obs_bar)^2 ]
```

- `r > 0.8` — strong spatial agreement
- `r > 0.9` — excellent agreement

**Note:** Since Posterbuddy only gives you point observations, you'll need to interpolate the observed heatmap using kernel density estimation (KDE) from your booth-level counts, or compare only at the observed node locations.

### 4.3 Node-Level Validation — Chi-Squared Goodness of Fit

Compare simulated vs. observed headcounts at each Posterbuddy sensor location:

```
chi^2 = sum_j [ (N_sim_j - N_obs_j)^2 / N_obs_j ]
```

Degrees of freedom = (number of sensor locations) - (number of calibrated parameters) - 1.

Reject the null hypothesis (simulation matches reality) if `chi^2 > chi^2_critical` at your chosen significance level (typically p = 0.05).

### 4.4 Flow Rate Validation — KS Test

Place virtual sensors (trigger colliders) at hallway cross-sections. Compare the cumulative distribution of simulated flow rates against observed flow rates using the **Kolmogorov-Smirnov test**:

```
D = max_x | F_sim(x) - F_obs(x) |
```

The KS test is non-parametric and works well with small samples. Reject if `D > D_critical(n_sim, n_obs, alpha)`.

### 4.5 Temporal Validation — RMSE of Time Series

If Posterbuddy provides time-stamped counts, compare the temporal profile:

```
RMSE = sqrt( (1/T) * sum_t [ N_sim(t) - N_obs(t) ]^2 )
```

Normalize by the mean observed count to get **NRMSE**:

```
NRMSE = RMSE / N_obs_bar
```

- NRMSE < 0.15 — good temporal agreement
- NRMSE < 0.10 — excellent

### 4.6 Sensitivity Analysis

Run a **Latin Hypercube Sampling** across your key parameters (`beta`, `v_0`, `tau`, `A_i`, `B_i`, `T_fatigue`, dwell time distribution) to understand which parameters most affect your outputs. Report the **Sobol sensitivity indices** to identify which parameters require the most careful calibration.

### 4.7 Multi-Run Statistical Significance

Because your simulation is stochastic, run **N ≥ 30 replications** with different random seeds. Report:

- Mean and 95% confidence intervals for all metrics
- Coefficient of variation (CV) — if CV > 0.15, you need more runs

**Citation:** Seyfried, A., Steffen, B. & Lippert, T. (2006). "Basics of modelling the pedestrian flow." *Physica A*, 368, 232–238. Law, A.M. (2015). *Simulation Modeling and Analysis*, 5th ed. McGraw-Hill (for validation methodology).

---

## 5. Quick-Reference Parameter Cheat Sheet

For direct hardcoding into your Unity `SimConfig`:

```csharp
// === SOCIAL FORCE MODEL ===
float desiredSpeed        = 1.34f;    // m/s (free-flow, Weidmann 1993)
float desiredSpeedExhibit = 0.80f;    // m/s (browsing mode)
float relaxationTime      = 0.50f;    // seconds (Helbing 1995)
float agentRadius         = 0.25f;    // meters (shoulder half-width)
float socialForceA        = 2000f;    // Newtons (Helbing 2000)
float socialForceB        = 0.08f;    // meters (Helbing 2000)
float bodyCompK           = 1.2e5f;   // kg/s^2 (Helbing 2000)
float slidingFrictionK    = 2.4e5f;   // kg/(m*s) (Helbing 2000)
float anisotropyLambda    = 0.50f;    // dimensionless
float jamDensity          = 5.4f;     // persons/m^2

// === FATIGUE ===
float fatigueTimeConst    = 7200f;    // seconds (120 min)
float minSpeedFraction    = 0.75f;    // floor at 75% of v_0
float dwellDecayExponent  = 0.20f;    // power-law per visit number

// === DWELL TIME (log-normal, in seconds) ===
float dwellMu             = 5.70f;    // ln(300) ≈ 5.7
float dwellSigma          = 0.80f;

// === ROAMING ===
float roamSpeedMin        = 0.30f;    // m/s
float roamSpeedMax        = 0.60f;    // m/s
float roamReorientRate    = 0.50f;    // 1/s

// === O-D GRAVITY MODEL ===
float betaImpedance       = 0.15f;    // 1/m (calibrate to venue)
```

---

## 6. Complete Bibliography

1. Bitgood, S. (2006). "An analysis of visitor circulation." *Curator*, 49(4), 463–475.
2. Bitgood, S. (2009). "Museum fatigue: A critical review." *Visitor Studies*, 12(2), 93–111.
3. Gilman, B.I. (1916). "Museum fatigue." *The Scientific Monthly*, 2(1), 62–74.
4. Helbing, D. & Molnar, P. (1995). "Social force model for pedestrian dynamics." *Physical Review E*, 51(5), 4282–4286.
5. Helbing, D., Farkas, I. & Vicsek, T. (2000). "Simulating dynamical features of escape panic." *Nature*, 407, 487–490.
6. Johansson, A., Helbing, D. & Shukla, P.K. (2007). "Specification of the social force pedestrian model." *Advances in Complex Systems*, 10(S2), 271–288.
7. Law, A.M. (2015). *Simulation Modeling and Analysis*, 5th ed. McGraw-Hill.
8. Serrell, B. (1997). "Paying attention." *Curator*, 40(2), 108–125.
9. Seyfried, A., Steffen, B., Klingsch, W. & Boltes, M. (2005). "The fundamental diagram of pedestrian movement revisited." *J. Stat. Mech.*, P10002.
10. Seyfried, A., Steffen, B. & Lippert, T. (2006). "Basics of modelling the pedestrian flow." *Physica A*, 368, 232–238.
11. Van Zuylen, H.J. & Willumsen, L.G. (1980). "The most likely trip matrix estimated from traffic counts." *Transportation Research Part B*, 14(3), 281–293.
12. Weidmann, U. (1993). "Transporttechnik der Fussgänger." *Schriftenreihe des IVT*, 90, ETH Zürich.
13. Wilson, A.G. (1967). "A statistical theory of spatial distribution models." *Transportation Research*, 1(3), 253–269.
14. Wilson, A.G. (1970). *Entropy in Urban and Regional Modelling*. Pion, London.
