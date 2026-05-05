---
name: pedestrian-dynamics-expert
description: Research-grade knowledge on pedestrian dynamics, including Social Force Model parameters, O-D matrix estimation, and simulation validation. Use when designing, calibrating, or validating pedestrian simulations for conferences or exhibitions.
---

<objective>
Provide expert guidance on pedestrian dynamics modeling, parameter calibration, and validation methodologies for exhibition and conference simulations.
</objective>

<principles>
- **Empirical Grounding**: Prioritize parameters and algorithms from peer-reviewed research (Helbing, Seyfried, Wilson).
- **Contextual Adaptation**: Adjust baseline parameters (e.g., desired speed) for specific environments like poster sessions.
- **Statistical Rigor**: Use formal validation tests (KS-test, Chi-squared, Pearson correlation) to verify simulation accuracy.
</principles>

<quick_reference>
## Baseline Parameters (Unity SimConfig)
- **Desired Speed**: 1.34 m/s (Free-flow) / 0.80 m/s (Exhibition)
- **Relaxation Time**: 0.5s
- **Social Force A**: 2000N
- **Social Force B**: 0.08m
- **Dwell Time (Log-normal)**: mu=5.7, sigma=0.8
- **Fatigue Time Const**: 7200s (120 min)
</quick_reference>

<routing>
Based on the task, refer to the following domain-specific references:

- **O-D Matrix & Trip Estimation**: Read [references/od-matrix.md](references/od-matrix.md) for entropy maximization and IPF algorithms.
- **Social Force Model & Physics**: Read [references/social-force-model.md](references/social-force-model.md) for equations of motion and calibrated parameters.
- **Visitor Behavior & Fatigue**: Read [references/behavior-and-fatigue.md](references/behavior-and-fatigue.md) for dwell time distributions, fatigue models, and roaming states.
- **Simulation Validation**: Read [references/validation.md](references/validation.md) for formal goodness-of-fit tests and comparison metrics.
</routing>

<process>
1. **Identify the Core Problem**: Determine if the user is asking about model selection, parameter calibration, or validation.
2. **Consult Relevant Reference**: Load the specific markdown file from the `references/` directory.
3. **Apply Expertise**: Provide concrete values, equations, or methodologies tailored to the user's simulation context (e.g., Unity, specific venue).
4. **Recommend Validation**: Always suggest appropriate statistical tests to confirm the proposed changes or models.
</process>
