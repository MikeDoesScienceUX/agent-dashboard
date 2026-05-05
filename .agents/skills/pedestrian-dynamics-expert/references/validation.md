<reference>
# Validation Framework

## 4.1 Fundamental Diagram Test
Plot simulated `speed vs. density` and compare against Seyfried et al. (2005).

## 4.2 Heatmap Comparison — Spatial Correlation
**Pearson Spatial Correlation Coefficient:**
```
r = sum_xy [(H_sim(x,y) - H_sim_bar) * (H_obs(x,y) - H_obs_bar)]
    / sqrt[ sum_xy (H_sim(x,y) - H_sim_bar)^2 * sum_xy (H_obs(x,y) - H_obs_bar)^2 ]
```

## 4.3 Node-Level Validation — Chi-Squared
```
chi^2 = sum_j [ (N_sim_j - N_obs_j)^2 / N_obs_j ]
```

## 4.4 Flow Rate Validation — KS Test
Compare cumulative distribution of flow rates using Kolmogorov-Smirnov test:
```
D = max_x | F_sim(x) - F_obs(x) |
```

## 4.5 Temporal Validation — RMSE/NRMSE
```
RMSE = sqrt( (1/T) * sum_t [ N_sim(t) - N_obs(t) ]^2 )
NRMSE = RMSE / N_obs_bar
```

## 4.6 Sensitivity Analysis
Use Latin Hypercube Sampling and report Sobol sensitivity indices.

## 4.7 Multi-Run Statistical Significance
Run N ≥ 30 replications with different random seeds.
</reference>