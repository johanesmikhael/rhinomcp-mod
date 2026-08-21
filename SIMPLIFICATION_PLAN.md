# Simplification plan

The evaluator grew to 6,853 lines, 24 parameters, 4 modes and 2 integrators. Every piece was
added because a measurement showed the previous simplification was wrong, and each was
justified on its own. Together they are more than the problem needs.

This plan is subtractive. Nothing here removes capability: every deletion is superseded
code, a known defect, or a derived quantity replaced by a stated one. Everything is
recoverable from git if a cut proves wrong.

It runs in **two phases**, and the order matters. Phase 1 simplifies *inside* the three
modes and changes nothing about how they are called - same names, same questions, same
answers, less machinery behind them. Phase 2 is the integration that makes the modes
themselves unnecessary.

Keeping the three modes through phase 1 is deliberate. They are the only cross-check the
evaluator has: three ways of asking about one structure, and the disagreements between them
have found every real defect so far. Removing machinery is safe while they still disagree
out loud. Merging them is a change of answer, not a change of housekeeping, and it should
not happen in the same breath.

---

# Phase 1 - simplify, keep the three modes

Same `welded`, `contact`, `pinned_dynamic`. Target: **~12 parameters, one integrator, no
derived stiffness chain.**

## 1.1 Delete the relaxed pinned solver - DONE

`pinned` is now an alias for the dynamic solver; `SolvePinnedFromGraph` and its 407 lines are
gone.

It asked the same question of the same model and reached its verdict through a divergence
trend rather than a displacement, which fired on the one structure where the displacement
test was right: it called the unbraced bridge unstable at 1.47 mm of pin motion against a
60.8 mm limit, while an integrator, a lateral load test and the mode shape all said it
stands. The regression case that recorded that false positive now passes without its
assertion moving.

## 1.2 Delete one integrator

`pinned_dynamic` carries two: particles (default, calibrated, cannot represent free fall) and
rigid bodies (opt-in, falls correctly to one part in ten thousand, not yet agreeing on
stiffness).

**Blocked on one measurement**, not on preference. Hand-calculate the braced bridge's midspan
sag from `EA/L` and see which of 0.623 mm (particles) and 0.656 mm (rigid bodies) it lands
nearer. The unbraced case differs by 6.5x and the rigid-body answer may well be the more
correct one - it updates geometry honestly where the particle model holds each body to a
fitted frame that may be stiffening the very mode in question.

Removes: one solver, `integrator`, `timestep_safety`, and the free-fall regression pair
collapses to one case.

## 1.3 Collapse the stiffness chain

Today a joint's stiffness is derived: mass -> density -> area -> `E` -> `EA/L`, with `E` and
density global for the whole model. Four knobs (`youngs_modulus`, `material_density`,
`joint_slip`, `rigid_strength`) and three chances to be wrong.

Replace with **one stated stiffness per joint**, in kN/mm. Material leaves the model, because
the connection is what is flexible - for a screwed CLT panel the timber's `E` is nearly
irrelevant. Simpler *and* more correct, and it is where a capacity in kN would live.

## 1.4 Prune the parameter list

24 parameters today. Once 1.2 and 1.3 are done, these go with them: `integrator`,
`timestep_safety`, `youngs_modulus`, `material_density`, `joint_slip`, `rigid_strength`, and
the `solver_substeps` / `current_step` pair that only the deleted relaxation paths needed.

`floor_strength` stays but is renamed for what it is. It is not a subgrade modulus - it is
divided by summed vertex tributary areas, which include those corners' share of the side
faces, so a 0.3 x 0.4 m pedestal base sums to about 0.47 m2 rather than 0.12. The product
`ground_support_stiffness_n_per_m` is the quantity with meaning and should be the input.

## 1.5 Justify or remove `torque_gain`

Contact mode's last unexplained constant, default 0.25. Either it is derivable from the
bearing geometry, in which case derive it, or it is a fudge, in which case it goes and the
mode is re-verified without it. It does not need to wait for phase 2.

---

# Phase 2 - integration

Only once phase 1 has landed and the three modes still agree with the regression suite.

## 2.1 Fold contact and welded into per-joint types

They are not different physics. One spring, three switches: which degrees of freedom it
restrains, whether it is one-sided, and its stiffness and capacity.

| type | translation | rotation | one-sided |
| --- | --- | --- | --- |
| free | no | no | - |
| contact | yes | no | yes, plus friction |
| pin | yes | no | no |
| welded | yes | yes | no |

Removes `torque_gain` and `contact_strength` - the last arbitrary constants - and ends the
practice of running three modes on one structure and reconciling three answers by hand.

Needs 1.2 first: a one-sided joint only means something if a body can separate and move,
which is gross rigid-body motion.

## 2.2 End state

- **`welded`** - whole scope as one rigid body, no joints. A cheap, genuinely independent
  upper bound with an analytic `support_margin` cross-check that has caught real defects.
- **One multi-body solver** - per-element bodies, real time, joint type per joint.

Once the multi-body solver is trustworthy, `welded` is itself a candidate for removal: it
exists mainly as a cross-check on a solver we do not yet fully trust. One mode is the honest
long-term target.

---

# Not deletions, still needed

- `torque_gain` justified or removed (falls out of item 4).
- Imperial units, never tested end to end.
- Joint capacity checking - for mass timber this *is* the design check.
- Per-element material.
- **Test coverage.** 13 cases, mostly one bridge and one stack family. Every verdict is
  "correct on the cases we have". This is the least glamorous gap and probably the largest.
