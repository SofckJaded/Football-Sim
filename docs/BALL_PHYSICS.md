# Ball Physics Model & Calibration

Derived from IFAB Law 2 and sports-aerodynamics literature (Asai et al., *Procedia Engineering* 2010):
- **Mass & Geometry:** $m = 0.43\text{ kg}$, $r = 0.11\text{ m}$, $A = \pi r^2 \approx 0.0380\text{ m}^2$, thin-shell inertia $I = \frac{2}{3} m r^2$.
- **Aerodynamic Drag:** $\mathbf{F}_d = -\frac{1}{2} \rho C_d(v) A \|\mathbf{v}\| \mathbf{v}$, smoothly transitioning from $C_d = 0.42$ (subcritical) to $C_d = 0.24$ (supercritical around $v_c = 12.5\text{ m/s}$).
- **Magnus Lift Force:** $\mathbf{F}_m = \frac{1}{2} \rho C_L(S) A \|\mathbf{v}\|^2 (\widehat{\boldsymbol{\omega} \times \mathbf{v}})$ where spin ratio $S = \frac{r \|\boldsymbol{\omega}\|}{\|\mathbf{v}\|}$ and $C_L(S) = \min(0.36, 1.15 S)$.
- **Ground Interaction & Swept Collision:** Restitution $e_n = 0.60$, sliding friction $\mu_s = 0.34$ transitioning to rolling resistance $\mu_r = 0.048$, and adaptive substepping with 3D segment-to-segment swept collision against goalposts and crossbars.
