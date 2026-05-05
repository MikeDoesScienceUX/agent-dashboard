<reference>
# Exhibition Behavior & Fatigue

## 3.1 Dwell Time at Posters
Dwell times follow a **log-normal distribution**:
```
f(t) = (1 / (t * sigma * sqrt(2*pi))) * exp(-(ln(t) - mu)^2 / (2 * sigma^2))
```
**Conference Poster Benchmarks:**
- `mu` ≈ 5.7 (for seconds, corresponds to ~5 min median)
- `sigma` ≈ 0.8
- Initial scan: 8–15 seconds
- Deep discussion: 5–15 minutes

## 3.2 Fatigue Model
**Walking speed decay:**
```
v(t) = v_0 * [alpha + (1 - alpha) * exp(-t / T_fatigue)]
```
- `alpha` (min speed fraction) ≈ 0.7–0.8
- `T_fatigue` (time constant) ≈ 90–120 minutes

**Dwell time decay over session:**
```
DwellTime(n) = DwellTime_0 * n^(-delta)
```
- `n` = visit number
- `delta` ≈ 0.15–0.30

## 3.3 Ambient Roaming Behavior
**States:**
- TRANSIT, DWELLING, ROAMING, SOCIALIZING, RESTING

**Roaming velocity model:**
- `v_roam = v_0 * Uniform(0.2, 0.5)`
- Direction: Ornstein-Uhlenbeck process (mean-reverting random walk)
</reference>