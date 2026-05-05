<reference>
# Origin-Destination (O-D) Matrix Estimation from Local Counts

## 1.1 The Entropy Maximization Model (Wilson, 1967/1970)

Wilson showed that the statistically most likely trip matrix is the one that maximizes the entropy function subject to known constraints.

**Objective function — maximize:**
```
S = -sum_ij [ T_ij * ln(T_ij) - T_ij ]
```
Subject to:
- `sum_j T_ij = O_i` (total trips originating from zone i)
- `sum_i T_ij = D_j` (total trips arriving at zone j)
- `sum_ij T_ij * c_ij = C` (total system travel cost constraint)

**Solution (doubly-constrained gravity form):**
```
T_ij = A_i * B_j * O_i * D_j * f(c_ij)
```
Where:
- `f(c_ij) = exp(-beta * c_ij)` — deterrence function
- `A_i = 1 / sum_j [ B_j * D_j * f(c_ij) ]` — origin balancing factor
- `B_j = 1 / sum_i [ A_i * O_i * f(c_ij) ]` — destination balancing factor
- `beta` — impedance parameter (typically 0.1–0.5 for indoor venues)

**Iterative Proportional Fitting (IPF):**
Initialize `A_i = B_j = 1.0`, then iterate:
1. `Step 1: A_i = O_i / sum_j [ B_j * D_j * f(c_ij) ]`
2. `Step 2: B_j = D_j / sum_i [ A_i * O_i * f(c_ij) ]`
Repeat until convergence.

## 1.2 Van Zuylen & Willumsen Maximum Entropy (ME2)

Used when only link counts and destination counts are known.

**Objective — maximize:**
```
S = -sum_ij [ T_ij * ln(T_ij / t_ij) - T_ij + t_ij ]
```
Subject to:
`sum_ij T_ij * p_ij_a = V_a` for all observed links a.

**Solution:**
```
T_ij = t_ij * product_a [ exp(-lambda_a * p_ij_a) ]
```

## 1.3 Practical Application to Posterbuddy Data

1. Estimate total attendance `N`.
2. Set `O_i` proportional to entrance capacity.
3. Use `D_j` from Posterbuddy as destination constraints.
4. Compute `c_ij` as NavMesh path distances.
5. Run IPF with `f(c_ij) = exp(-beta * c_ij)`.
6. Calibrate `beta` to match hallway densities.
</reference>