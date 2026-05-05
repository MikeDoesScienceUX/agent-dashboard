<reference>
# Social Force Model — Calibrated Parameters

## 2.1 Core Equation of Motion
```
m_i * (dv_i/dt) = F_i_drive + sum_j F_ij_social + sum_W F_iW_wall
```

## 2.2 Driving Force (Destination Attraction)
```
F_i_drive = m_i * (v_i_0 * e_i_0 - v_i) / tau_i
```

## 2.3 Pedestrian-Pedestrian Repulsive Force
```
F_ij = { A_i * exp[(r_ij - d_ij) / B_i] + k * g(r_ij - d_ij) } * n_ij
       + kappa * g(r_ij - d_ij) * Delta_v_ji_t * t_ij
```

## 2.4 Wall Repulsive Force
```
F_iW = { A_i * exp[(r_i - d_iW) / B_i] + k * g(r_i - d_iW) } * n_iW
       + kappa * g(r_i - d_iW) * (v_i . t_iW) * t_iW
```

## 2.5 Baseline Parameters
| Parameter | Symbol | Value | Unit |
|---|---|---|---|
| **Desired speed (free flow)** | `v_0` | **1.34 ± 0.26** | m/s |
| **Desired speed (exhibition)** | `v_0` | **0.6 – 1.0** | m/s |
| **Relaxation time** | `tau` | **0.5** | s |
| **Agent radius** | `r_i` | **0.2 – 0.3** | m |
| **Social force strength** | `A_i` | **2000** | N |
| **Social force range** | `B_i` | **0.08** | m |
| **Body compression coeff.** | `k` | **1.2 × 10^5** | kg/s^2 |
| **Sliding friction coeff.** | `kappa` | **2.4 × 10^5** | kg/(m·s) |
| **Anisotropy factor** | `lambda` | **0.5** | — |

## 2.6 Speed-Density Relationship
```
v(rho) = v_0 * (1 - exp(-gamma * (1/rho - 1/rho_max)))
```
- `rho_max` ≈ 5.4 persons/m^2
- `gamma` ≈ 1.913 m^2
</reference>