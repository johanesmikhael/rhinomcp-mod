# Simplification plan

The evaluator grew to 6,853 lines, 24 parameters, 4 modes and 2 integrators. Every piece was
added because a measurement showed the previous simplification was wrong, and each was
justified on its own. Together they are more than the problem needs.

This plan is subtractive. Nothing here removes capability: every deletion is superseded
code, a known defect, or a derived quantity replaced by a stated one. Everything is
recoverable from git if a cut proves wrong.

Target: **2 modes, ~10 parameters**.

## 1. Delete the relaxed pinned solver - DONE

`pinned` is now an alias for the dynamic solver; `SolvePinnedFromGraph` and its 407 lines are
gone.

It asked the same question of the same model and reached its verdict through a divergence
trend rather than a displacement, which fired on the one structure where the displacement
test was right: it called the unbraced bridge unstable at 1.47 mm of pin motion against a
60.8 mm limit, while an integrator, a lateral load test and the mode shape all said it
stands. The regression case that recorded that false positive now passes without its
assertion moving.

## 2. Delete one integrator

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

## 3. Collapse the stiffness chain

Today a joint's stiffness is derived: mass -> density -> area -> `E` -> `EA/L`, with `E` and
density global for the whole model. Four knobs (`youngs_modulus`, `material_density`,
`joint_slip`, `rigid_strength`) and three chances to be wrong.

Replace with **one stated stiffness per joint**, in kN/mm. Material leaves the model, because
the connection is what is flexible - for a screwed CLT panel the timber's `E` is nearly
irrelevant. Simpler *and* more correct, and it is where a capacity in kN would live.

## 4. Fold contact and welded into per-joint types

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

Needs item 2 first: a one-sided joint only means something if a body can separate and move,
which is gross rigid-body motion.

## End state

- **`welded`** - whole scope as one rigid body, no joints. A cheap, genuinely independent
  upper bound with an analytic `support_margin` cross-check that has caught real defects.
- **One multi-body solver** - per-element bodies, real time, joint type per joint.

Once the multi-body solver is trustworthy, `welded` is itself a candidate for removal: it
exists mainly as a cross-check on a solver we do not yet fully trust. One mode is the honest
long-term target.

## Not deletions, still needed

- `torque_gain` justified or removed (falls out of item 4).
- Imperial units, never tested end to end.
- Joint capacity checking - for mass timber this *is* the design check.
- Per-element material.
- **Test coverage.** 13 cases, mostly one bridge and one stack family. Every verdict is
  "correct on the cases we have". This is the least glamorous gap and probably the largest.
