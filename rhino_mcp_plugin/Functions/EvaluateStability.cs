using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;
using KangarooSolver;
using KangarooSolver.Goals;
using rhinomcp_mod.Serializers;
using Rhino.DocObjects;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public const string GraphKey = "rhinomcp-mod:connectivity-graph";
    public const string EvaluationGraphKey = "rhinomcp-mod:connectivity-graph-eva";
    public const string StabilityKey = "rhinomcp.stability.v1";
    public const string AfterEvaluationKey = "rhinomcp.after_eva.v1";
    public const string EvaluationMode = "single_rigid_assembly";

    // Raised from 50: a toppling assembly has barely started moving after 50 iterations,
    // so the old default reported collapses as stable. Early exit on settled or clearly
    // collapsed motion keeps the common cases from paying for the larger budget.
    public const int DefaultCurrentStep = 2000;
    public const double DefaultStabilityThresholdMeters = 0.01;
    public const double DefaultRigidStrength = 10000.0;
    // Contact stiffness per square metre of bearing area, not per mesh vertex. Floor2
    // holds one scalar strength shared by every point it is given, so a single Floor2 over
    // all vertices made a footing's stiffness track its mesh density: a column resting on
    // 33 vertices over 0.085 m2 was 90x stiffer per unit area than a slab resting on 12
    // vertices over 2.82 m2, purely because it was meshed finer. Contact is now handled by
    // AreaFloor, which carries a per-point stiffness of floor_strength * tributary area, so
    // remeshing no longer changes a structural result.
    // A subgrade modulus in Pa/m: penetration = 0.83 * bearing pressure / floor_strength,
    // exactly linear over the four decades measured in Rhino.
    //
    // The choice is governed by one ratio. Settling depth falls as 1/floor_strength while a
    // topple turns at a rate proportional to 1/rigid_strength, and rigidity forces the rigid
    // goal above the floor, so a stiff floor buys shallow settling at the price of a slow
    // collapse. At 1e7 a tower carrying its upper storeys on a single line of columns stood
    // up for the whole run: the assembly is not welded in reality, but the solver welds it,
    // and the residual eccentricity turned it at 5e-8 deg per step - a real failure that
    // reads as rest. At 1e5 the same assembly turns 0.18 to 0.36 deg and both trends catch
    // it, while a sound four-column tower still converges by step 300 and a squat block
    // control converges by step 200.
    //
    // 1e5 settles this catalogue about 125 mm. That is deep to look at, but it is pure
    // translation and no part of the verdict reads it.
    public const double DefaultFloorStrength = 1e5;
    public const double DefaultFloorZ = 0.0;


    // Share of the stability threshold that an auto-sized floor is allowed to spend on
    // settling. Keeping it at a tenth leaves the rest of the budget for real motion, while
    // staying clear of the ~1 mm residual that rigid-body compliance contributes anyway.
    public const double AutoFloorPenetrationFraction = 10.0;

    // RigidBody2 and Floor2 are blended by weight, so whichever goal weighs more wins
    // where they disagree. The floor is sized from the assembly's mass, so a fixed rigid
    // strength is outweighed by every assembly heavier than a few kilograms: the floor
    // then reshapes the body it is meant to support, and a sound structure settles and
    // tilts its way past the stability threshold. Keep the rigid goal dominant by
    // deriving it from whatever floor strength is actually in force.
    // The floor calibration in StabilityUnitMath was measured at floor_strength 1000
    // against the fixed rigid default of 10000 - a ratio of 10. Auto-sizing the floor from
    // mass without moving the rigid goal with it is what pushed the pair three orders of
    // magnitude out of that regime, so track the ratio the calibration actually assumed.
    public const double AutoRigidFloorRatio = 10.0;

    // Motion-history sampling. The verdict comes from the trend across a run, so the run
    // needs enough samples to have a trend at all.
    public const int MotionSampleCount = 32;
    public const double SettledEpsilonMeters = 1e-7;

    // Absolute floor under the divergence test. Area-weighted contact leaves a resting
    // assembly drifting by nanometres, and at 1e-7 m that drift was read as collapse: a
    // tower whose centre of mass sat 1.48 m inside its support was failed on a growth of
    // 1.07e-7 m - a tenth of a micron. A real topple grows by hundreds of millimetres over
    // the same window, so 1e-5 m sits a thousand times above the noise and four orders
    // below the signal.
    public const double DivergenceMinGrowthMeters = 1e-5;

    // Noise floor and decay margin for the rotation trend. A resting assembly's rotation
    // converges: measured growth over the final quarter of a run was below 1e-6 deg for
    // every sound configuration in the catalogue. A topple keeps turning - the slowest one
    // measured, at floor_strength 1e7, still gained about 1e-4 deg per quarter.
    public const double RotationNoiseFloorDegrees = 1e-5;
    public const double RotationDecayMargin = 0.95;

    // How close to the floor a contact site has to be to count as bearing on the ground
    // when the support polygon is built. Two millimetres covers modelling slop without
    // reaching up to the next storey.
    public const double GroundContactToleranceMeters = 2e-3;
    public const double DivergenceGrowthMargin = 1.05;
    public const double CollapseThresholdFactor = 10.0;

    // Cap the gap between samples so the settled-motion exit does not inherit the step
    // budget: tying the interval to current_step meant a large budget also postponed the
    // exit that makes the large budget affordable. A sound assembly now leaves after
    // MinSettledSamples * MaxSampleInterval steps whatever the cap is set to.
    // The verdict rests on two complementary signals. The motion trend catches a failure
    // still in progress; rotation catches one that finished inside the step budget and came
    // to rest lying down, which reads as "settled" because it genuinely has stopped.
    //
    // Rotation therefore only has to separate a completed topple from a resting body, and
    // once contact is weighted by area rather than by vertex count the gap is enormous. The
    // uneven seating that used to tilt a resting body - two differently tessellated elements
    // meeting the ground at one support - is gone, and what rotation remains is just the
    // floor's own compliance, which scales with penetration: the same tower turned 0.98 deg
    // at floor_strength 1.2e4 and 0.00053 deg at 1e7.
    //
    // Measured at the 1e5 default: a four-column tower rests at 0.024 deg and a squat block
    // control at 0.041 deg, while a block whose centre of mass sat 186 mm outside its
    // support fell flat and read 89.6 deg. One degree sits about 24x above the resting
    // values and far below a completed topple. Ten degrees was needed only to absorb the
    // old contact noise.
    //
    // Magnitude is the last of the three signals, not the first. A collapse still running
    // when the budget ends is caught by the trends at a fraction of a degree - 0.18 and
    // 0.36 deg for two cantilevered frames - and only an assembly that finished toppling
    // and came to rest lying down needs this comparison at all.
    public const double DefaultRotationThresholdDegrees = 1.0;

    public const int MaxSampleInterval = 25;
    public const int MinSettledSamples = 8;

    // Consecutive samples that must show both diverging motion and growing rotation before
    // a run gives up on the assembly. Three of them span 75 solver steps at the default
    // sampling interval, which is long enough that bedding-in noise cannot fake it.
    public const int DivergingSamplesToExit = 3;
    public const double MaxAutoRigidStrength = 1e12;
    public const double DefaultGravity = 9.80665;
    public const double DefaultAssignToleranceMeters = 1e-6;
    public const double DefaultSolverThresholdMeters = 0.001;
    public const int DefaultSolverSubsteps = 1;
    private const int MaxCurrentStep = 10000;
    private const int MaxSolverSubsteps = 1000;
    private const int MaxTotalSolverSteps = 100000;

    public JObject EvaluateStability(JObject parameters)
    {
        try
        {
            if (!global::RhinoMCPModPlugin.KangarooRuntime.EnsureAvailable(out var kangarooError))
            {
                throw new InvalidOperationException($"Kangaroo solver is unavailable. {kangarooError}");
            }

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
            {
                throw new Exception("No active Rhino document.");
            }

            var unitContext = StabilityUnits.Create(doc.ModelUnitSystem);

            var graph = ReadGraph(parameters, doc);
            var nodes = graph["n"] as JArray;
            if (nodes == null)
            {
                throw new Exception("Connectivity graph does not contain an 'n' array.");
            }
            if (nodes.Count == 0)
            {
                throw new Exception("Connectivity graph contains no nodes to evaluate.");
            }

            var stabilityNodes = new List<StabilityNode>();
            var nodeErrors = new List<string>();
            var unitWarnings = new JArray();
            for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                var nodeToken = nodes[nodeIndex];
                if (nodeToken is not JObject node)
                {
                    nodeErrors.Add($"node[{nodeIndex}] is not an object");
                    continue;
                }

                if (node["g"]?.ToString() is not string guidString || !Guid.TryParse(guidString, out var guid))
                {
                    nodeErrors.Add($"node[{nodeIndex}] has no valid object GUID");
                    continue;
                }

                var rhinoObject = doc.Objects.FindId(guid);
                if (rhinoObject == null)
                {
                    nodeErrors.Add($"node[{nodeIndex}] object {guidString} was not found");
                    continue;
                }

                var geometry = rhinoObject.Geometry;
                if (geometry == null)
                {
                    nodeErrors.Add($"node[{nodeIndex}] object {guidString} has no geometry");
                    continue;
                }

                JObject massSource = node;
                var userText = rhinoObject.Attributes.GetUserString(StabilityKey);
                if (!string.IsNullOrWhiteSpace(userText))
                {
                    try
                    {
                        var storedMass = JObject.Parse(userText);
                        if (storedMass["mass"] != null)
                        {
                            massSource = storedMass;
                        }
                    }
                    catch (Exception ex)
                    {
                        nodeErrors.Add(
                            $"node[{nodeIndex}] object {guidString} has invalid stored mass data: {ex.Message}");
                        continue;
                    }
                }

                if (!TryReadFiniteDouble(massSource["mass"], out var mass) || mass <= 0.0)
                {
                    nodeErrors.Add($"node[{nodeIndex}] object {guidString} needs a positive finite mass");
                    continue;
                }

                var massUnit = massSource["mass_unit"]?.ToString();
                if (string.IsNullOrWhiteSpace(massUnit))
                {
                    massUnit = StabilityUnits.InferLegacyMassUnit(doc.ModelUnitSystem);
                    unitWarnings.Add(
                        $"Object {guidString} has untagged legacy mass; interpreted as {massUnit}. Reassign mass to store canonical kg metadata.");
                }

                if (!StabilityUnits.TryMassToKilograms(mass, massUnit, out var massKilograms))
                {
                    nodeErrors.Add(
                        $"node[{nodeIndex}] object {guidString} has unsupported mass_unit '{massUnit}' or invalid mass");
                    continue;
                }

                node["mass"] = massKilograms;
                node["mass_unit"] = StabilityUnits.KilogramUnit;
                stabilityNodes.Add(new StabilityNode
                {
                    Node = node,
                    Geometry = geometry,
                    MassKilograms = massKilograms
                });
            }

            if (nodeErrors.Count > 0)
            {
                throw new Exception($"Connectivity graph is not evaluable: {string.Join("; ", nodeErrors)}");
            }

            var currentStep = ReadIntegerParameter(
                parameters, "current_step", DefaultCurrentStep, 1, MaxCurrentStep);
            var stabilityThreshold = ReadFiniteParameter(
                parameters,
                "stability_threshold",
                unitContext.FromMeters(DefaultStabilityThresholdMeters),
                0.0,
                inclusiveMinimum: true);
            // The floor is placed under the assembly rather than at world zero unless the
            // caller asks for a specific elevation. A scope that excludes the pads its
            // columns stand on would otherwise start 224 mm in the air and spend the run
            // falling, and a model built above or below the construction plane would never
            // touch the floor at all.
            var floorZIsAuto = parameters?["floor_z"] == null;
            var floorZ = floorZIsAuto
                ? AssemblyMinimumZ(stabilityNodes)
                : ReadFiniteParameter(parameters, "floor_z", DefaultFloorZ);
            var gravity = ReadFiniteParameter(
                parameters, "gravity", DefaultGravity, 0.0, inclusiveMinimum: true);

            // Floor2 is a linear contact spring, so a fixed strength lets a heavy assembly
            // sink far enough to exhaust the stability threshold on settling alone. When the
            // caller does not pin the strength down, size it from the assembly's own weight
            // so that settling stays within a small fraction of the threshold.
            //
            // floor_strength is now read as stiffness per square metre of bearing area: the
            // solver multiplies it by each contact's tributary area. A value carried over
            // from before that change is a per-vertex figure and will not mean the same
            // thing.
            var totalMassKilograms = stabilityNodes.Sum(node => node.MassKilograms);
            var stabilityThresholdMeters = stabilityThreshold * unitContext.LengthToMeters;
            // Kangaroo blends goals by weight, and gravity is a Unary of weight 1 per vertex.
            // Sizing the floor from mass pushed its weight into the millions, which divided
            // gravity away and froze the assembly: a structure whose centre of mass sat
            // 908 mm outside its support still reported as settled. The floor therefore
            // stays at the strength the penetration coefficient was calibrated against.
            // Settling is deep here, but it is pure translation and the verdict ignores it.
            // Renamed for what it is. It is not a subgrade modulus: it is multiplied by each
            // standing vertex's tributary area, and those areas include the corners' share of
            // the side faces meeting there, so a 0.3 x 0.4 m pedestal base sums to about
            // 0.47 m2 rather than 0.12. The product is the quantity with meaning, so that is
            // what the parameter is now called. The old name still works.
            var floorStrengthIsAuto = parameters?["ground_support_stiffness_n_per_m"] == null &&
                parameters?["floor_strength"] == null;
            var floorStrength = floorStrengthIsAuto
                ? DefaultFloorStrength
                : ReadFiniteParameter(
                    parameters,
                    parameters?["ground_support_stiffness_n_per_m"] != null
                        ? "ground_support_stiffness_n_per_m"
                        : "floor_strength",
                    DefaultFloorStrength, 0.0, inclusiveMinimum: false);

            // Sized after the floor, and from the floor, for the reason given on
            // AutoRigidFloorRatio. An explicit rigid_strength still wins outright, so a
            // caller can deliberately study a compliant assembly.
            var rigidStrengthIsAuto = parameters?["rigid_strength"] == null;
            var rigidStrength = rigidStrengthIsAuto
                ? Math.Min(
                    Math.Max(floorStrength * AutoRigidFloorRatio, DefaultRigidStrength),
                    MaxAutoRigidStrength)
                : ReadFiniteParameter(
                    parameters, "rigid_strength", DefaultRigidStrength, 0.0, inclusiveMinimum: false);
            var assignTol = ReadFiniteParameter(
                parameters,
                "assign_tol",
                unitContext.FromMeters(DefaultAssignToleranceMeters),
                0.0,
                inclusiveMinimum: false);
            var threshold = ReadFiniteParameter(
                parameters,
                "threshold",
                unitContext.FromMeters(DefaultSolverThresholdMeters),
                0.0,
                inclusiveMinimum: false);
            var solverSubsteps = ReadIntegerParameter(
                parameters, "solver_substeps", DefaultSolverSubsteps, 1, MaxSolverSubsteps);
            if ((long)currentStep * solverSubsteps > MaxTotalSolverSteps)
            {
                throw new ArgumentOutOfRangeException(
                    "solver_substeps",
                    $"current_step * solver_substeps must not exceed {MaxTotalSolverSteps}.");
            }

            // The two modes answer different questions and neither subsumes the other:
            // welded catches an assembly tipping over, pinned catches a mechanism. See the
            // remarks on the pinned solver for why a pin cannot see overturning.
            var modeText = parameters?["mode"]?.ToString();
            // "pinned" and "pinned_dynamic" are the same thing: the relaxed pinned solver
            // is gone, and the names are kept only so existing callers keep working.
            var pinned =
                string.Equals(modeText, "pinned", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modeText, "dynamic", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modeText, "pinned_dynamic", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modeText, PinnedEvaluationMode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modeText, PinnedDynamicEvaluationMode, StringComparison.OrdinalIgnoreCase);
            if (!pinned && !string.IsNullOrWhiteSpace(modeText) &&
                !string.Equals(modeText, "welded", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(modeText, "contact", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(modeText, ContactEvaluationMode, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(modeText, EvaluationMode, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Unknown evaluation mode '{modeText}'; use 'welded', 'pinned', 'contact' or 'pinned_dynamic'.");
            }

            var contactMode = string.Equals(modeText, "contact", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modeText, ContactEvaluationMode, StringComparison.OrdinalIgnoreCase);
            if (contactMode)
            {
                // Left alone, the contact solver now sizes every stiffness from the load it
                // carries: see the note on DefaultJointPenetrationMeters for why an absolute
                // modulus is a pseudo-time step here rather than a material property. The
                // two surfaces are separate knobs because they are different materials - a
                // soil under the assembly, dry masonry inside it - and an explicit strength
                // on either still pins that one to the old absolute law.
                var contactStrengthIsAuto = parameters?["contact_strength"] == null;
                var contactStrength = contactStrengthIsAuto
                    ? DefaultContactStrength
                    : ReadFiniteParameter(
                        parameters, "contact_strength", DefaultContactStrength, 0.0,
                        inclusiveMinimum: false);
                var groundStrengthIsAuto = floorStrengthIsAuto;
                var groundStrength = floorStrength;
                var jointPenetration = ReadFiniteParameter(
                    parameters,
                    "joint_penetration",
                    unitContext.FromMeters(DefaultJointPenetrationMeters),
                    0.0,
                    inclusiveMinimum: false);
                var groundSettlement = ReadFiniteParameter(
                    parameters,
                    "ground_settlement",
                    unitContext.FromMeters(DefaultGroundSettlementMeters),
                    0.0,
                    inclusiveMinimum: false);
                var torqueGain = ReadFiniteParameter(
                    parameters, "torque_gain", DefaultTorqueGain, 0.0, inclusiveMinimum: false);
                var bodyStrength = rigidStrengthIsAuto
                    ? contactStrength * AutoRigidFloorRatio
                    : rigidStrength;

                var contactStable = SolveContactFromGraph(
                    graph,
                    stabilityNodes,
                    currentStep,
                    contactStrength,
                    contactStrengthIsAuto,
                    groundStrength,
                    groundStrengthIsAuto,
                    unitContext.ToMeters(jointPenetration),
                    unitContext.ToMeters(groundSettlement),
                    torqueGain,
                    bodyStrength,
                    rigidStrengthIsAuto,
                    unitContext.ToMeters(floorZ),
                    gravity,
                    unitContext.ToMeters(assignTol),
                    unitContext.ToMeters(threshold),
                    solverSubsteps,
                    unitContext.LengthToMeters,
                    WantsDisplay(parameters) ? doc : null);

                var contactResult = BuildPinnedResult(graph, doc, unitContext, contactStable, gravity,
                    floorZ, floorZIsAuto, bodyStrength, totalMassKilograms, unitWarnings);
                contactResult["evaluation_mode"] = ContactEvaluationMode;
                contactResult["contact_strength_auto"] = contactStrengthIsAuto;
                contactResult["ground_strength_auto"] = groundStrengthIsAuto;
                contactResult["joint_penetration"] = jointPenetration;
                contactResult["joint_penetration_m"] = unitContext.ToMeters(jointPenetration);
                contactResult["ground_settlement"] = groundSettlement;
                contactResult["ground_settlement_m"] = unitContext.ToMeters(groundSettlement);
                contactResult["ground_strength"] = groundStrength;
                contactResult["joint_weight_min_n_per_m"] = graph["joint_weight_min_n_per_m"];
                contactResult["joint_weight_max_n_per_m"] = graph["joint_weight_max_n_per_m"];
                contactResult["friction"] = DefaultContactFriction;
                contactResult["contact_count"] = graph["contact_count"];
                contactResult["open_contacts"] = graph["open_contacts"];
                contactResult["ground_contact_points"] = graph["ground_contact_points"];
                contactResult["contacts"] = graph["contacts"];
                contactResult["torque_gain"] = torqueGain;
                contactResult["contact_strength"] = contactStrength;
                return contactResult;
            }

            if (pinned)
            {
                var pinnedSlip = ReadFiniteParameter(
                    parameters,
                    "joint_penetration",
                    unitContext.FromMeters(DefaultJointPenetrationMeters),
                    0.0,
                    inclusiveMinimum: false);

                // One number where there were four.
                //
                // A joint's stiffness used to be derived through mass, density, area and E,
                // with E and density global for the whole model, and `rigid_strength` doubling
                // as an override on top - four knobs and three chances to be wrong for one
                // quantity. `joint_stiffness_n_per_m` states it outright and is what a joint
                // test gives you; the derivation stays as the default so existing models are
                // unaffected.
                //
                // `rigid_strength` no longer reaches the pinned joints. It meant "how rigid is
                // a body" in welded mode and "how stiff is a joint" here, which are different
                // questions that happened to share a name.
                var jointStiffness = ReadFiniteParameter(
                    parameters, "joint_stiffness_n_per_m", 0.0, 0.0);
                var jointStiffnessIsAuto = !(jointStiffness > 0.0);

                var specificStiffness = ReadFiniteParameter(
                    parameters, "specific_stiffness", DefaultSpecificStiffnessM2S2, 0.0,
                    inclusiveMinimum: false);

                // Every pinned request is answered by the dynamic solver now. The relaxed
                // one asked the same question of the same model and reached its verdict
                // through a divergence trend rather than a displacement, which fired on the
                // one structure where the displacement test was right: it called the
                // unbraced bridge unstable at 1.47 mm of pin motion against a 60.8 mm
                // limit, while an integrator, a lateral load test and the mode shape all
                // said it stands. Deleting it removes the defect rather than patching it.
                {
                    // Same bodies, same pins, same member stiffness - Newton's second law
                    // instead of Kangaroo's weighted average. See StabilityDynamics.
                    var duration = ReadFiniteParameter(
                        parameters, "duration_seconds", StabilityDynamics.DefaultDurationSeconds,
                        0.0, inclusiveMinimum: false);
                    var damping = ReadFiniteParameter(
                        parameters, "damping_ratio", StabilityDynamics.DefaultDampingRatio, 0.0);
                    var lateralLoad = ReadFiniteParameter(
                        parameters, "lateral_load_fraction",
                        StabilityDynamics.DefaultNotionalLoadFraction, 0.0);
                    var imperfection = ReadFiniteParameter(
                        parameters, "imperfection_fraction",
                        StabilityDynamics.DefaultImperfectionFraction, 0.0);

                    // Two integrators, and the particle one is still the default.
                    //
                    // "rigid_bodies" makes the body the thing that moves, so an element with
                    // nothing under it falls at g - verified against 0.5*g*t^2 to one part
                    // in ten thousand, where the particle model reached 0.2% of it. That is
                    // the defect it exists to fix and it fixes it outright.
                    //
                    // It is not the default yet because its joints are not calibrated: the
                    // pins are springs where the particle model shared a particle outright,
                    // and the assembly comes out far softer than the deflections already
                    // checked against hand statics. Making it default would trade a defect
                    // that is understood and documented for numbers that are not, so it
                    // stays opt-in until it reproduces the validated cases.
                    var integrator = parameters?["integrator"]?.ToString();
                    if (string.Equals(integrator, "rigid_bodies", StringComparison.OrdinalIgnoreCase))
                    {
                        var rigidStable = SolvePinnedRigidFromGraph(
                            graph,
                            stabilityNodes,
                            jointStiffness,
                            jointStiffnessIsAuto,
                            unitContext.ToMeters(pinnedSlip),
                            specificStiffness,
                            unitContext.ToMeters(floorZ),
                            gravity,
                            duration,
                            damping,
                            imperfection,
                            lateralLoad,
                            ReadFiniteParameter(
                                parameters, "timestep_safety",
                                StabilityRigidBodies.TimestepSafety, 0.0, inclusiveMinimum: false),
                            unitContext.LengthToMeters,
                            WantsDisplay(parameters) ? doc : null);

                        var rigidResult = BuildPinnedResult(graph, doc, unitContext, rigidStable,
                            gravity, floorZ, floorZIsAuto, rigidStrength, totalMassKilograms,
                            unitWarnings);
                        rigidResult["evaluation_mode"] = PinnedDynamicEvaluationMode;
                        foreach (var key in new[]
                        {
                            "integrator", "max_pin_displacement_m", "settled_displacement_m", "timestep_s",
                            "timestep_safety", "steps_run",
                            "simulated_seconds", "duration_requested_s", "damping_ratio",
                            "peak_speed_m_s", "total_weight_n", "time_samples_s",
                            "speed_samples_m_s", "member_stiffness_min_n_per_m",
                            "member_stiffness_max_n_per_m", "node_count_clustered", "nodes",
                            "span_m", "imperfection_m", "imperfection_speed_m_s", "settled",
                            "verdict", "conclusive", "converged", "decay_ratio_per_swing",
                            "projected_displacement_m", "lateral_load_fraction", "sway",
                            "joint_count"
                        })
                        {
                            rigidResult[key] = graph[key];
                        }

                        return rigidResult;
                    }

                    var dynamicStable = SolvePinnedDynamicFromGraph(
                        graph,
                        stabilityNodes,
                        jointStiffness,
                        jointStiffnessIsAuto,
                        unitContext.ToMeters(pinnedSlip),
                        specificStiffness,
                        unitContext.ToMeters(floorZ),
                        gravity,
                        unitContext.ToMeters(assignTol),
                        duration,
                        damping,
                        imperfection,
                        lateralLoad,
                        unitContext.LengthToMeters,
                        WantsDisplay(parameters) ? doc : null);

                    var dynamicResult = BuildPinnedResult(graph, doc, unitContext, dynamicStable,
                        gravity, floorZ, floorZIsAuto, rigidStrength, totalMassKilograms,
                        unitWarnings);
                    dynamicResult["evaluation_mode"] = PinnedDynamicEvaluationMode;
                    foreach (var key in new[]
                    {
                        "max_pin_displacement_m", "settled_displacement_m", "timestep_s",
                        "steps_run", "simulated_seconds",
                        "duration_requested_s", "damping_ratio", "peak_speed_m_s", "total_weight_n",
                        "imperfection_m", "imperfection_fraction", "imperfection_speed_m_s", "settled",
                        "verdict", "conclusive", "converged", "decay_ratio_per_swing",
                        "projected_displacement_m", "turnovers",
                        "joint_sharing_histogram", "joint_max_shared_particles",
                        "joint_welded_examples", "joint_count", "lateral_load_fraction", "sway",
                        "time_samples_s", "speed_samples_m_s", "member_stiffness_min_n_per_m",
                        "member_stiffness_max_n_per_m", "node_count_clustered", "node_widest_m",
                        "nodes", "span_m"
                    })
                    {
                        dynamicResult[key] = graph[key];
                    }

                    return dynamicResult;
                }

            }

            var stable = SolveFromGraph(
                graph,
                stabilityNodes,
                currentStep,
                stabilityThreshold,
                rigidStrength,
                rigidStrengthIsAuto,
                floorStrength,
                floorStrengthIsAuto,
                DefaultGroundSettlementMeters,
                unitContext.ToMeters(floorZ),
                gravity,
                unitContext.ToMeters(assignTol),
                unitContext.ToMeters(threshold),
                solverSubsteps,
                unitContext.LengthToMeters,
                out var finalXform);

            graph["stable"] = stable;
            graph["evaluation_mode"] = EvaluationMode;
            graph["document_length_unit"] = doc.ModelUnitSystem.ToString();
            graph["displacement_unit"] = doc.ModelUnitSystem.ToString();
            graph["length_to_meters"] = unitContext.LengthToMeters;
            graph["mass_unit"] = StabilityUnits.KilogramUnit;
            graph["gravity_m_s2"] = gravity;
            // Report the solver inputs that the caller did not necessarily supply, so a
            // result can be explained without re-running the evaluation to discover them.
            graph["total_mass_kg"] = totalMassKilograms;
            // The sized value, not the value read from the parameters: an auto run picks
            // its own subgrade and reporting the placeholder would explain nothing.
            floorStrength = graph["floor_strength_sized"]?.Value<double>() ?? floorStrength;
            rigidStrength = graph["rigid_strength_sized"]?.Value<double>() ?? rigidStrength;
            graph["floor_strength"] = floorStrength;
            graph["floor_strength_auto"] = floorStrengthIsAuto;
            graph["rigid_strength"] = rigidStrength;
            graph["rigid_strength_auto"] = rigidStrengthIsAuto;
            graph["floor_z_m"] = unitContext.ToMeters(floorZ);
            graph["unit_warnings"] = unitWarnings.DeepClone();
            var evaluationGraph = SerializableGraph(graph);
            doc.Strings.SetString(EvaluationGraphKey, evaluationGraph.ToString());

            // Always rewrite the evaluated geometry cache with the latest simulation result.
            var displayRequested = parameters?["display"]?.ToString();
            var displayOn = string.Equals(displayRequested, "On", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(displayRequested, "on", StringComparison.OrdinalIgnoreCase) ||
                (parameters?["display"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && parameters["display"].Value<bool>() == true);
            var displayOff = string.Equals(displayRequested, "Off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(displayRequested, "off", StringComparison.OrdinalIgnoreCase) ||
                (parameters?["display"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && parameters["display"].Value<bool>() == false);

            ClearAfterEvaluationCache(doc);
            {
                try
                {
                    foreach (var sNode in stabilityNodes)
                    {
                        var guidStr = sNode.Node["g"]?.ToString();
                        if (string.IsNullOrWhiteSpace(guidStr) || !Guid.TryParse(guidStr, out var gid))
                            continue;

                        var obj = doc.Objects.FindId(gid);
                        if (obj == null || obj.Geometry == null)
                            continue;

                        // use the finalXform returned by the solver

                        // Duplicate geometry and apply transform
                        GeometryBase dup = null;
                        if (obj.Geometry is Brep br)
                        {
                            dup = br.DuplicateBrep();
                        }
                        else if (obj.Geometry is Mesh ms)
                        {
                            dup = ms.DuplicateMesh();
                        }
                        else if (obj.Geometry is Curve crv)
                        {
                            dup = crv.DuplicateCurve();
                        }
                        else if (obj.Geometry is Extrusion ex)
                        {
                            dup = ex.Duplicate();
                        }

                        if (dup == null)
                            continue;

                        try
                        {
                            dup.Transform(finalXform);
                        }
                        catch
                        {
                            // ignore transform failures
                        }

                        // Add temporary object to doc so Serializer can build outlines/obb
                        Guid tempId = Guid.Empty;
                        try
                        {
                            if (dup is Brep b)
                            {
                                tempId = doc.Objects.AddBrep(b);
                            }
                            else if (dup is Mesh m)
                            {
                                tempId = doc.Objects.AddMesh(m);
                            }
                            else if (dup is Curve c)
                            {
                                tempId = doc.Objects.AddCurve(c);
                            }
                            else if (dup is Extrusion ex)
                            {
                                tempId = doc.Objects.AddExtrusion(ex);
                            }
                            else
                            {
                                // fallback: try adding as generic geometry
                                tempId = doc.Objects.Add(dup);
                            }

                            if (tempId == Guid.Empty)
                                continue;

                            var tempObj = doc.Objects.FindId(tempId);
                            if (tempObj == null)
                                continue;

                                // Ensure a stored pose user-string exists so the serializer uses it
                                try
                                {
                                    JObject pose = GetOrBootstrapPose(tempObj);
                                    WriteStoredPose(tempObj, pose, invalidateObbCache: false);
                                }
                                catch
                                {
                                    // ignore pose caching failures
                                }

                            // Serialize geometry summary
                            JObject serial = Serializer.RhinoObject(tempObj, includeGeometrySummary: true, outlineMaxPoints: 64);
                            if (serial != null && serial["geometry"] is JObject geometry)
                            {
                                // build full mesh from transformed geometry and store alongside summary
                                var meshFull = AsMesh(dup);
                                if (meshFull != null)
                                {
                                    var verts = new JArray();
                                    foreach (var v in meshFull.Vertices)
                                    {
                                        verts.Add(new JArray { v.X, v.Y, v.Z });
                                    }

                                    var faces = new JArray();
                                    foreach (var f in meshFull.Faces)
                                    {
                                        if (f.IsTriangle)
                                        {
                                            faces.Add(new JArray { f.A, f.B, f.C });
                                        }
                                        else
                                        {
                                            faces.Add(new JArray { f.A, f.B, f.C, f.D });
                                        }
                                    }

                                    var fullMesh = new JObject
                                    {
                                        ["type"] = "MESH",
                                        ["vertices"] = verts,
                                        ["faces"] = faces
                                    };

                                    WriteAfterEvaluationFullGeometry(obj, geometry, fullMesh);
                                }
                                else
                                {
                                    // fallback: write only the summary
                                    WriteAfterEvaluationObb(obj, geometry);
                                }
                            }
                        }
                        catch
                        {
                            // ignore per-object failures
                        }
                        finally
                        {
                            if (tempId != Guid.Empty)
                            {
                                try { doc.Objects.Delete(tempId, true); } catch { }
                            }
                        }
                    }

                    if (displayOn)
                    {
                        global::RhinoMCPModPlugin.MCPStabilityController.SetEnabled(true);
                    }
                    else if (displayOff)
                    {
                        global::RhinoMCPModPlugin.MCPStabilityController.SetEnabled(false);
                    }

                    doc.Views.Redraw();
                }
                catch
                {
                    // swallow any caching/display errors
                }
            }

            var result = new JObject
            {
                ["success"] = true,
                ["stable"] = stable,
                ["evaluation_mode"] = EvaluationMode,
                ["node_count"] = stabilityNodes.Count,
                ["solver_iterations"] = currentStep * solverSubsteps,
                ["stability_threshold"] = stabilityThreshold,
                ["stability_threshold_m"] = unitContext.ToMeters(stabilityThreshold),
                ["document_length_unit"] = doc.ModelUnitSystem.ToString(),
                ["length_to_meters"] = unitContext.LengthToMeters,
                ["mass_unit"] = StabilityUnits.KilogramUnit,
                ["gravity_m_s2"] = gravity,
                ["total_mass_kg"] = totalMassKilograms,
                ["floor_strength"] = floorStrength,
                ["floor_strength_auto"] = floorStrengthIsAuto,
                ["ground_bearing_area_m2"] = graph["ground_bearing_area_m2"],
                ["ground_support_stiffness_n_per_m"] = graph["ground_support_stiffness_n_per_m"],
                ["ground_settlement_m"] = graph["ground_settlement_m"],
                // Reported for the same reason as the floor pair: the rigid strength is
                // now derived rather than fixed, so a result cannot be explained without
                // knowing which value the solver actually used.
                ["rigid_strength"] = rigidStrength,
                ["rigid_strength_auto"] = rigidStrengthIsAuto,
                // The verdict now rests on the motion trend, so the trend and the step
                // count it was measured over have to travel with the result; without them
                // a caller cannot tell a settled assembly from an under-run one.
                ["rotation_deg"] = graph["rotation_deg"],
                ["rotation_threshold_deg"] = graph["rotation_threshold_deg"],
                ["motion_trend"] = graph["motion_trend"],
                // Rotation carries its own trend now. A topple's rate scales as 1/rigid
                // strength, so a stiff floor can leave a real collapse turning too slowly
                // to register as displacement at all; only the rotation history shows it.
                ["rotation_trend"] = graph["rotation_trend"],
                ["solver_steps_run"] = graph["solver_steps_run"],
                ["motion_samples_m"] = graph["motion_samples_m"],
                ["rotation_samples_deg"] = graph["rotation_samples_deg"],
                // Whole-body overturning margin: signed distance from the centre of mass to
                // the ground support polygon, positive inside. Reported for diagnosis only.
                // It is exact for a genuinely rigid body and says nothing about an assembly
                // that can come apart at a joint, so it never decides the verdict.
                ["support_margin_m"] = graph["support_margin_m"],
                ["ground_contact_sites"] = graph["ground_contact_sites"],
                ["centre_of_mass_m"] = graph["centre_of_mass_m"],
                // floor_strength alone no longer says how stiff the support was: it is
                // multiplied by tributary area, so the bearing area and the bucket count it
                // was resolved into have to travel with the result too.
                ["contact_area_m2"] = graph["contact_area_m2"],
                ["contact_sites"] = graph["contact_sites"],
                ["floor_z"] = floorZ,
                ["floor_z_m"] = unitContext.ToMeters(floorZ),
                ["floor_z_auto"] = floorZIsAuto,
                ["unit_warnings"] = unitWarnings,
                ["evaluation_graph_key"] = EvaluationGraphKey
            };

            if (graph["max_displacement"] != null)
            {
                result["max_displacement"] = graph["max_displacement"].Value<double?>();
            }
            if (graph["max_displacement_m"] != null)
            {
                result["max_displacement_m"] = graph["max_displacement_m"].Value<double?>();
            }

            return result;
        }
        catch (Exception ex)
        {
            return new JObject
            {
                ["success"] = false,
                ["message"] = ex.Message
            };
        }
    }

    private static bool TryReadFiniteDouble(JToken token, out double value)
    {
        value = 0.0;
        if (token == null)
        {
            return false;
        }

        try
        {
            value = token.Value<double>();
            return double.IsFinite(value);
        }
        catch
        {
            return false;
        }
    }

    private static int ReadIntegerParameter(
        JObject parameters,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        var token = parameters?[name];
        if (token == null)
        {
            return fallback;
        }

        int value;
        try
        {
            value = token.Value<int>();
        }
        catch
        {
            throw new ArgumentException($"{name} must be an integer.", name);
        }

        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                $"{name} must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static double ReadFiniteParameter(
        JObject parameters,
        string name,
        double fallback,
        double? minimum = null,
        bool inclusiveMinimum = true)
    {
        var token = parameters?[name];
        if (token == null)
        {
            return fallback;
        }

        if (!TryReadFiniteDouble(token, out var value))
        {
            throw new ArgumentException($"{name} must be a finite number.", name);
        }

        if (minimum.HasValue &&
            (inclusiveMinimum ? value < minimum.Value : value <= minimum.Value))
        {
            var comparison = inclusiveMinimum ? "greater than or equal to" : "greater than";
            throw new ArgumentOutOfRangeException(
                name,
                $"{name} must be {comparison} {minimum.Value}.");
        }

        return value;
    }

    /// <summary>
    /// Resolves the graph to evaluate, in priority order: an explicit graph payload, then
    /// a freshly computed graph when scope filters are supplied, then the graph stored in
    /// document text. The scope path is preferred for real work because it evaluates one
    /// assembly and cannot serve a stale graph - the stored copy is only rewritten when
    /// someone requests an unscoped graph, so it can lag the model badly.
    /// </summary>
    /// <summary>True when the caller asked for the settled pose to be drawn.</summary>
    private static bool WantsDisplay(JObject parameters)
    {
        var token = parameters?["display"];
        if (token == null)
        {
            return false;
        }

        if (token.Type == JTokenType.Boolean)
        {
            return token.Value<bool>();
        }

        return string.Equals(token.ToString(), "On", StringComparison.OrdinalIgnoreCase);
    }

    private static JObject ReadGraph(JObject parameters, RhinoDoc doc)
    {
        var graphToken = parameters?["graph"];
        if (graphToken is JObject graphObject)
        {
            return graphObject;
        }

        if (HasGraphScopeParameters(parameters))
        {
            var scope = ReadGraphScope(parameters);
            var computed = MCPConnectivityGraphController.GetOrComputeGraph(
                doc, persist: false, scope: scope);

            if (computed.Nodes.Count == 0)
            {
                throw new Exception(
                    "Connectivity graph scope matched no objects; widen layer/ids/bbox/selected.");
            }

            if (computed.Truncated)
            {
                throw new Exception(
                    $"Connectivity graph is truncated ({computed.ExaminedCount} of " +
                    $"{computed.CandidateCount} objects examined); narrow the scope before evaluating.");
            }

            return MCPConnectivityGraphStore.BuildGraphPayload(doc, computed);
        }

        var graphText = graphToken?.Type == JTokenType.String
            ? graphToken.Value<string>()
            : graphToken?.ToString();

        if (string.IsNullOrWhiteSpace(graphText))
        {
            // The stored graph is only rewritten when someone asks for an unscoped graph,
            // so it can describe a model that no longer exists. Serving it silently is the
            // worst failure this evaluator has: it answers confidently about geometry the
            // caller cannot see. Measured on this project - five bracing members were added
            // and an unscoped evaluation returned byte-identical results for the model
            // without them.
            //
            // Check the stored copy against the document it claims to describe, and
            // recompute when it does not match.
            var fingerprint = MCPConnectivityGraphBuilder.ComputeFingerprint(doc);
            if (MCPConnectivityGraphStore.TryLoad(doc, fingerprint, out var stored) &&
                stored != null && stored.Nodes.Count > 0)
            {
                return MCPConnectivityGraphStore.BuildGraphPayload(doc, stored);
            }

            var recomputed = MCPConnectivityGraphController.GetOrComputeGraph(doc, persist: true);
            if (recomputed == null || recomputed.Nodes.Count == 0)
            {
                throw new Exception(
                    "Connectivity graph is empty for this document; nothing to evaluate.");
            }

            if (recomputed.Truncated)
            {
                throw new Exception(
                    $"Connectivity graph is truncated ({recomputed.ExaminedCount} of " +
                    $"{recomputed.CandidateCount} objects examined); scope the evaluation.");
            }

            return MCPConnectivityGraphStore.BuildGraphPayload(doc, recomputed);
        }

        if (string.IsNullOrWhiteSpace(graphText))
        {
            throw new Exception($"Connectivity graph not found in Rhino document: {GraphKey}");
        }

        var parsed = JToken.Parse(graphText);
        if (parsed is JValue value && value.Type == JTokenType.String)
        {
            parsed = JToken.Parse(value.Value<string>() ?? string.Empty);
        }

        if (parsed is not JObject graph)
        {
            throw new Exception("Connectivity graph JSON must be an object.");
        }

        return graph;
    }
    private static bool HasGraphScopeParameters(JObject parameters)
    {
        if (parameters == null)
        {
            return false;
        }

        foreach (var name in new[] { "layer", "ids", "bbox", "selected" })
        {
            var token = parameters[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                continue;
            }

            // selected:false is not a scope request, it is the absence of one.
            if (name == "selected" && token.Type == JTokenType.Boolean && !token.Value<bool>())
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static void ClearAfterEvaluationCache(RhinoDoc doc)
    {
        if (doc == null)
        {
            return;
        }

        foreach (var obj in doc.Objects)
        {
            if (obj == null || obj.IsDeleted)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(AfterEvaluationKey)))
            {
                obj.Attributes.DeleteUserString(AfterEvaluationKey);
                obj.CommitChanges();
            }
        }
    }
    private static bool SolveFromGraph(
        JObject graph,
        List<StabilityNode> nodes,
        int currentStep,
        double stabilityThreshold,
        double rigidStrength,
        bool rigidStrengthIsAuto,
        double floorStrength,
        bool floorStrengthIsAuto,
        double groundSettlementMeters,
        double floorZMeters,
        double gravity,
        double assignToleranceMeters,
        double solverThresholdMeters,
        int solverSubsteps,
        double lengthToMeters,
        out Transform finalXform)
    {
        if (nodes.Count == 0)
        {
            throw new InvalidOperationException("No valid stability nodes were provided to the solver.");
        }

        graph["stable"] = false;
        graph["stability_threshold"] = stabilityThreshold;
        graph["stability_threshold_m"] = stabilityThreshold * lengthToMeters;
        graph["max_displacement"] = null;
        graph["max_displacement_m"] = null;

        var rigidMesh = new Mesh();
        var vertexPoints = new List<Point3d>();

        // Contact and gravity are accumulated per unique particle position rather than per
        // mesh vertex. Kangaroo merges coincident points into one particle and blends every
        // goal on that particle as a weighted average, so an unwelded box - three
        // coincident vertices at each corner - had its three gravity goals averaged rather
        // than summed, applying a third of its own weight. Summing here, then emitting one
        // goal per unique site, makes the applied load exact and mesh-independent.
        var massMoment = Vector3d.Zero;
        var massTotal = 0.0;
        var siteAreas = new Dictionary<(long, long, long), double>();
        var siteMasses = new Dictionary<(long, long, long), double>();
        var sitePoints = new Dictionary<(long, long, long), Point3d>();

        foreach (var node in nodes)
        {
            var solverGeometry = node.Geometry.Duplicate();
            if (solverGeometry == null ||
                !solverGeometry.Transform(Transform.Scale(Point3d.Origin, lengthToMeters)))
            {
                throw new InvalidOperationException(
                    $"Object {node.Node["g"]} could not be scaled from document units to meters.");
            }

            var mesh = AsMesh(solverGeometry);
            if (mesh == null)
            {
                throw new InvalidOperationException(
                    $"Object {node.Node["g"]} could not be meshed in solver meter space.");
            }

            var points = MeshVerticesAsPoints(mesh);
            if (points.Count < 3)
            {
                throw new InvalidOperationException(
                    $"Object {node.Node["g"]} could not be converted to a solver mesh with at least three vertices.");
            }

            rigidMesh.Append(mesh);
            vertexPoints.AddRange(points);

            // Tributary area per vertex, deduplicated to one entry per unique position.
            var vertexAreas = TributaryVertexAreas(mesh);
            var nodeKeys = new List<(long, long, long)>();
            var nodePoints = new List<Point3d>();
            var nodeAreas = new List<double>();
            var nodeIndices = new Dictionary<(long, long, long), int>();
            for (var i = 0; i < points.Count; i++)
            {
                if (!TrySiteKey(points[i], assignToleranceMeters, out var key))
                {
                    throw new InvalidOperationException(
                        $"Object {node.Node["g"]} has a vertex that cannot be placed on the solver grid.");
                }

                if (!nodeIndices.TryGetValue(key, out var index))
                {
                    index = nodePoints.Count;
                    nodeIndices[key] = index;
                    nodeKeys.Add(key);
                    nodePoints.Add(points[i]);
                    nodeAreas.Add(0.0);
                }

                nodeAreas[index] += vertexAreas[i];
            }

            var nodeArea = nodeAreas.Sum();
            if (!double.IsFinite(nodeArea) || nodeArea <= 0.0)
            {
                throw new InvalidOperationException(
                    $"Object {node.Node["g"]} has no surface area to carry its own weight.");
            }

            // Distribute the node's mass over its surface, then correct the distribution so
            // that its resultant acts on the true volume centroid. Dividing mass by vertex
            // count instead put the load on the vertex centroid, which drifts wherever a
            // mesh is denser on one side - the same mesh-density bias as the contact goal,
            // on the load side.
            var shares = new double[nodeAreas.Count];
            for (var i = 0; i < shares.Length; i++)
            {
                shares[i] = nodeAreas[i] / nodeArea;
            }

            var loadCentre = Point3d.Origin;
            for (var i = 0; i < nodePoints.Count; i++)
            {
                loadCentre += nodePoints[i] * shares[i];
            }

            if (TryVolumeCentroid(mesh, out var volumeCentroid))
            {
                shares = SharesAtCentroid(nodePoints, shares, volumeCentroid);
                loadCentre = volumeCentroid;
            }

            massMoment += new Vector3d(loadCentre) * node.MassKilograms;
            massTotal += node.MassKilograms;

            for (var i = 0; i < nodeKeys.Count; i++)
            {
                var key = nodeKeys[i];
                sitePoints[key] = nodePoints[i];
                siteAreas.TryGetValue(key, out var area);
                siteAreas[key] = area + nodeAreas[i];
                siteMasses.TryGetValue(key, out var mass);
                siteMasses[key] = mass + (node.MassKilograms * shares[i]);
            }
        }

        if (vertexPoints.Count < 3 || rigidMesh.Vertices.Count == 0)
        {
            throw new InvalidOperationException("The assembly did not produce a valid solver mesh.");
        }

        rigidMesh.Normals.ComputeNormals();
        if (rigidMesh.Vertices.Count != vertexPoints.Count)
        {
            throw new Exception("Rigid mesh vertices and source points are not one-to-one.");
        }

        // Validate that the assembly is not degenerate or collinear. The indices are not
        // used to build the solver frame: deriving axes from assembly vertices made the
        // frame depend on which node came first in the graph, which changed the settled
        // transform for identical geometry.
        if (FrameIndices(vertexPoints) == null)
        {
            throw new InvalidOperationException("The assembly does not contain three non-collinear solver points.");
        }

        // RigidBody2 adds solverPlane.Origin as PPos[0]. Keep this origin away
        // from the mesh reference vertex so it does not collapse onto PPos[1].
        var combinedBoundingBox = rigidMesh.GetBoundingBox(true);
        if (!combinedBoundingBox.IsValid)
        {
            throw new InvalidOperationException("The assembly solver mesh has no valid bounding box.");
        }

        // World-aligned axes about the assembly centre: the frame then depends only on the
        // geometry, never on graph node order.
        var solverPlane = new Plane(
            combinedBoundingBox.Center,
            Vector3d.XAxis,
            Vector3d.YAxis);

        var bodyBrep = Brep.CreateFromMesh(rigidMesh, true);
        if (bodyBrep == null)
        {
            throw new InvalidOperationException("Kangaroo could not create a rigid body from the assembly mesh.");
        }

        // Sorted by grid key so that goal order - and with it particle numbering and the
        // order the solver sums contributions in - depends only on the geometry, never on
        // dictionary insertion order or on which node the graph listed first.
        var siteKeys = siteMasses.Keys
            .OrderBy(key => key.Item1)
            .ThenBy(key => key.Item2)
            .ThenBy(key => key.Item3)
            .ToList();

        var contactSites = new List<(Point3d Point, double AreaM2)>();
        var totalContactArea = 0.0;
        foreach (var key in siteKeys)
        {
            var area = siteAreas.TryGetValue(key, out var value) ? value : 0.0;
            if (!double.IsFinite(area) || area <= 0.0)
            {
                continue;
            }

            contactSites.Add((sitePoints[key], area));
            totalContactArea += area;
        }

        // The ground is sized from the load standing on it, not from a constant.
        //
        // A fixed subgrade modulus decides the verdict on its own, and in both directions.
        // Too soft and an eccentric assembly tilts on its own foundation until its centre
        // of mass leaves the base, so a block with 121 mm of margin topples; too stiff and
        // a genuine overturning develops too slowly to be seen inside the step budget. The
        // constant that used to sit here, 1e5, was a compromise between those two wrong
        // answers, and it read every case in the regression sweep as unstable, margin or no
        // margin.
        //
        // Sizing it the way the contact mode sizes its joints removes the choice: the
        // ground is a spring that settles a stated distance under the weight it actually
        // carries, so K = W / settlement, and the per-area modulus is that over the bearing
        // area. The result is a real subgrade stiffness in Pa/m rather than a tuning knob,
        // and it moves with the model instead of having to be re-picked for each one.
        //
        // AreaFloor proposes its full correction rather than Kangaroo's usual quarter, so
        // no relaxation compensation belongs here - unlike RigidMesh, see
        // RelaxationCompensation.
        if (floorStrengthIsAuto)
        {
            // The tributary areas of the vertices standing on the floor - which is what
            // AreaFloor multiplies its strength by, so it is the right denominator for
            // making the total support stiffness come out at W / settlement. It is not the
            // bearing footprint and is larger than it: a bottom corner's tributary area
            // includes its share of the two side faces meeting there, so a 0.3 x 0.4 m
            // pedestal base sums to about 0.47 m2. The product is what carries physical
            // meaning here; floor_strength on its own is not a subgrade modulus.
            var bearingArea = 0.0;
            foreach (var site in contactSites)
            {
                if (site.Point.Z <= floorZMeters + GroundContactToleranceMeters)
                {
                    bearingArea += site.AreaM2;
                }
            }

            var weightNewtons = gravity * massTotal;
            if (bearingArea > 0.0 && weightNewtons > 0.0 && groundSettlementMeters > 0.0)
            {
                floorStrength = weightNewtons / (groundSettlementMeters * bearingArea);
                if (rigidStrengthIsAuto)
                {
                    // Kangaroo blends goals by weight, so a floor heavier than the rigid
                    // goal deforms the very assembly it is supporting.
                    rigidStrength = Math.Min(
                        Math.Max(floorStrength * AutoRigidFloorRatio, DefaultRigidStrength),
                        MaxAutoRigidStrength);
                }
            }

            graph["ground_bearing_area_m2"] = bearingArea;
            graph["ground_support_stiffness_n_per_m"] = floorStrength * bearingArea;
        }

        graph["floor_strength_sized"] = floorStrength;
        graph["rigid_strength_sized"] = rigidStrength;
        graph["ground_settlement_m"] = groundSettlementMeters;

        // All source vertices intentionally form one welded rigid body.
        var rigidGoalPoints = new List<Point3d>(vertexPoints);
        var rbGoal = new RigidBody2(bodyBrep, solverPlane, rigidGoalPoints, rigidStrength);
        var goals = new List<IGoal> { rbGoal };

        foreach (var key in siteKeys)
        {
            goals.Add(new Unary(sitePoints[key], new Vector3d(0.0, 0.0, -gravity * siteMasses[key])));
        }

        if (contactSites.Count > 0)
        {
            goals.Add(new AreaFloor(
                contactSites.Select(site => site.Point).ToList(),
                contactSites.Select(site => floorStrength * site.AreaM2).ToList(),
                floorZMeters));
        }

        graph["contact_area_m2"] = totalContactArea;
        graph["contact_sites"] = contactSites.Count;

        // The classical overturning check, run on the same contact set the solver uses.
        var centreOfMass = massTotal > 0.0
            ? new Point3d(massMoment / massTotal)
            : Point3d.Unset;
        var hasSupportMargin = TrySupportMargin(
            contactSites, centreOfMass, floorZMeters, out var supportMargin, out var groundSites);
        graph["support_margin_m"] = hasSupportMargin ? new JValue(supportMargin) : JValue.CreateNull();
        graph["ground_contact_sites"] = groundSites;
        graph["centre_of_mass_m"] = centreOfMass.IsValid
            ? new JArray(centreOfMass.X, centreOfMass.Y, centreOfMass.Z)
            : null;

        var physicalSystem = new PhysicalSystem();
        foreach (var goal in goals)
        {
            physicalSystem.AssignPIndex(goal, assignToleranceMeters);
        }

        var initialRigidPositions = rbGoal.PPos;
        var initialRigidIndices = rbGoal.PIndex;
        if (initialRigidPositions == null || initialRigidIndices == null ||
            initialRigidPositions.Length != initialRigidIndices.Length ||
            initialRigidIndices.Length < vertexPoints.Count + 1)
        {
            throw new InvalidOperationException("Kangaroo returned an invalid rigid-body particle mapping.");
        }

        // PPos[0] is the orientation particle; PPos[1 + vertexIndex] is the
        // corresponding mesh vertex. Ignore duplicate global particles, then
        // select three distinct, non-collinear particles for transform recovery.
        var uniqueVertexRecords = new List<(int VertexIndex, int GlobalIndex, Point3d Point)>();
        var seenGlobalIndices = new HashSet<int>();
        for (var vertexIndex = 0; vertexIndex < vertexPoints.Count; vertexIndex++)
        {
            var globalIndex = initialRigidIndices[vertexIndex + 1];
            if (!seenGlobalIndices.Add(globalIndex))
            {
                continue;
            }

            uniqueVertexRecords.Add((vertexIndex, globalIndex, vertexPoints[vertexIndex]));
        }

        if (uniqueVertexRecords.Count < 3)
        {
            throw new InvalidOperationException("Kangaroo assigned fewer than three unique rigid-body particles.");
        }

        // Start from a geometrically determined particle rather than the first one listed,
        // so that transform recovery does not depend on graph node order either.
        var tracking0Index = CanonicalPointIndex(uniqueVertexRecords);
        var tracking0 = uniqueVertexRecords[tracking0Index];

        // Scan every other particle for the farthest one; the seed is no longer guaranteed
        // to sit at index 0, so the search cannot skip the leading entries.
        var tracking1Index = -1;
        var farthestDistanceSquared = -1.0;
        for (var i = 0; i < uniqueVertexRecords.Count; i++)
        {
            if (i == tracking0Index)
            {
                continue;
            }

            var distanceSquared = uniqueVertexRecords[i].Point.DistanceToSquared(tracking0.Point);
            if (distanceSquared > farthestDistanceSquared)
            {
                farthestDistanceSquared = distanceSquared;
                tracking1Index = i;
            }
        }

        if (tracking1Index < 0)
        {
            throw new InvalidOperationException("The solver could not select a second tracking particle.");
        }

        var tracking1 = uniqueVertexRecords[tracking1Index];

        var trackingAxis = tracking1.Point - tracking0.Point;
        var tracking2Index = -1;
        var bestTrackingCrossSquared = -1.0;
        for (var i = 0; i < uniqueVertexRecords.Count; i++)
        {
            var candidate = uniqueVertexRecords[i];
            if (candidate.GlobalIndex == tracking0.GlobalIndex ||
                candidate.GlobalIndex == tracking1.GlobalIndex)
            {
                continue;
            }

            var cross = Vector3d.CrossProduct(trackingAxis, candidate.Point - tracking0.Point);
            if (cross.SquareLength > bestTrackingCrossSquared)
            {
                bestTrackingCrossSquared = cross.SquareLength;
                tracking2Index = i;
            }
        }

        if (tracking2Index < 0 ||
            IsDegenerateCross(bestTrackingCrossSquared, trackingAxis.SquareLength))
        {
            throw new InvalidOperationException("The solver could not select a non-collinear tracking frame.");
        }

        var tracking2 = uniqueVertexRecords[tracking2Index];
        var initialTrackingPlane = new Plane(tracking0.Point, tracking1.Point, tracking2.Point);
        if (!initialTrackingPlane.IsValid)
        {
            throw new InvalidOperationException("The solver's initial tracking frame is invalid.");
        }

        var globalP0 = tracking0.GlobalIndex;
        var globalP1 = tracking1.GlobalIndex;
        var globalP2 = tracking2.GlobalIndex;

        // Sample how far the assembly has moved as the run proceeds, rather than reading
        // the displacement once at the end. A settling structure's motion decays toward
        // zero; a toppling one accelerates. Judged by a single end-of-run number the two
        // are indistinguishable, because a slow topple sampled early simply looks small -
        // which is exactly how an assembly whose centre of mass sits outside its support
        // came back "stable" at the old 50-step default.
        var motionSamples = new List<double>();
        var rotationSamples = new List<double>();
        var divergingSampleRun = 0;
        var sampleInterval = Math.Clamp(currentStep / MotionSampleCount, 1, MaxSampleInterval);
        var stepsRun = 0;

        // Rotation has to be sampled as the run proceeds, not read once at the end. The
        // solver's three weights - gravity 1, contact k, rigid R - admit only ratios, and
        // rigidity forces R above k, so a topple turns at a rate proportional to 1/R while
        // settling depth falls as 1/k. Shallow settling and a fast topple are therefore the
        // same knob pulled in opposite directions: at k=1e7 a tower whose centre of mass sat
        // 198 mm outside its support turned about 5e-8 deg per step - a real collapse, but
        // one whose displacement trace flattens and reads as rest. Magnitude cannot see it;
        // a rotation that is still growing at the end of the run can.
        double CurrentRotationDegrees()
        {
            var sampled = physicalSystem.GetPositionArray();
            if (globalP0 >= sampled.Length || globalP1 >= sampled.Length || globalP2 >= sampled.Length)
            {
                return 0.0;
            }

            var plane = new Plane(sampled[globalP0], sampled[globalP1], sampled[globalP2]);
            if (!plane.IsValid)
            {
                return rotationSamples.Count > 0 ? rotationSamples[rotationSamples.Count - 1] : 0.0;
            }

            return RotationDegreesFromTransform(Transform.PlaneToPlane(initialTrackingPlane, plane));
        }

        double CurrentMotionMeters()
        {
            var sampled = physicalSystem.GetPositionArray();
            if (globalP0 >= sampled.Length || globalP1 >= sampled.Length || globalP2 >= sampled.Length)
            {
                return 0.0;
            }

            return Math.Max(
                sampled[globalP0].DistanceTo(tracking0.Point),
                Math.Max(
                    sampled[globalP1].DistanceTo(tracking1.Point),
                    sampled[globalP2].DistanceTo(tracking2.Point)));
        }

        for (var step = 0; step < currentStep; step++)
        {
            for (var subStep = 0; subStep < solverSubsteps; subStep++)
            {
                physicalSystem.Step(goals, true, solverThresholdMeters);
            }

            stepsRun = step + 1;
            if (stepsRun % sampleInterval != 0 && stepsRun != currentStep)
            {
                continue;
            }

            var motion = CurrentMotionMeters();
            motionSamples.Add(motion);
            rotationSamples.Add(CurrentRotationDegrees());

            // No displacement-based early exit. With the floor soft enough for gravity to
            // act, a sound assembly settles hundreds of millimetres into it, so any cutoff
            // that trips on distance fires long before rotation - the actual signal - has
            // developed. That was the old collapse exit, and it is what made sound
            // assemblies read as collapsing. The divergence exit below replaces it: it asks
            // the two trends, not the distance travelled.

            // Equally, stop once motion has genuinely stopped: three consecutive samples
            // that add essentially nothing mean the structure has settled. Require a
            // minimum sample count first, because the initial settling into the floor
            // decays on its own and can briefly look like rest even while a slower topple
            // is building underneath it.
            if (motionSamples.Count >= MinSettledSamples)
            {
                var settled = true;
                for (var back = 0; back < 3; back++)
                {
                    var index = motionSamples.Count - 1 - back;
                    var delta = Math.Abs(motionSamples[index] - motionSamples[index - 1]);
                    if (delta > SettledEpsilonMeters * (1.0 + motionSamples[index]))
                    {
                        settled = false;
                        break;
                    }
                }

                // Rotation must have stopped too. A slow topple's translation flattens as
                // soon as the assembly has bedded in, so a displacement-only test calls it
                // settled while it is still turning over.
                if (settled && IsGrowingRotation(rotationSamples))
                {
                    settled = false;
                }

                if (settled)
                {
                    break;
                }
            }

            // A collapse that is under way is already decided: gravity alone does not slow
            // a toppling body back into rest, so nothing later in the run can flip the
            // verdict, and the remaining steps only enlarge a rotation figure that is not
            // deciding anything. Require both signals, and require them to hold for several
            // consecutive samples, so that a run does not quit on one noisy sample taken
            // while the assembly is still bedding into the floor.
            if (motionSamples.Count >= MinSettledSamples)
            {
                if (IsDivergingMotion(motionSamples) && IsGrowingRotation(rotationSamples))
                {
                    divergingSampleRun++;
                    if (divergingSampleRun >= DivergingSamplesToExit)
                    {
                        break;
                    }
                }
                else
                {
                    divergingSampleRun = 0;
                }
            }
        }

        var diverging = IsDivergingMotion(motionSamples);
        var turning = IsGrowingRotation(rotationSamples);
        graph["motion_trend"] = diverging ? "diverging" : "settling";
        graph["rotation_trend"] = turning ? "turning" : "steady";
        graph["motion_samples_m"] = new JArray(motionSamples.Select(value => (object)value).ToArray());
        graph["rotation_samples_deg"] = new JArray(rotationSamples.Select(value => (object)value).ToArray());
        graph["solver_steps_run"] = stepsRun;

        var positions = physicalSystem.GetPositionArray();
        if (globalP0 < 0 || globalP1 < 0 || globalP2 < 0 ||
            globalP0 >= positions.Length || globalP1 >= positions.Length || globalP2 >= positions.Length)
        {
            throw new InvalidOperationException("Kangaroo returned an incomplete final particle array.");
        }

        var nowP0 = positions[globalP0];
        var nowP1 = positions[globalP1];
        var nowP2 = positions[globalP2];
        var finalCross = Vector3d.CrossProduct(nowP1 - nowP0, nowP2 - nowP0);
        var initialTrackingCross = Vector3d.CrossProduct(
            tracking1.Point - tracking0.Point,
            tracking2.Point - tracking0.Point);
        if (!double.IsFinite(finalCross.SquareLength) ||
            finalCross.SquareLength <= Math.Max(1e-48, initialTrackingCross.SquareLength * 1e-20))
        {
            throw new InvalidOperationException("The final solver tracking frame collapsed.");
        }

        var nowPlane = new Plane(nowP0, nowP1, nowP2);
        if (!nowPlane.IsValid)
        {
            throw new InvalidOperationException("The solver's final tracking frame is invalid.");
        }

        var solverTransform = Transform.PlaneToPlane(initialTrackingPlane, nowPlane);
        finalXform = StabilityUnits.SolverTransformToDocument(solverTransform, lengthToMeters);
        // The support margin is reported but deliberately does not gate the verdict. It
        // measures whole-body overturning about the ground footprint, which assumes the
        // assembly is one rigid body - the same assumption the solver makes, but one the
        // real structures do not satisfy, since their parts merely rest on one another. On
        // a frame carrying its storeys on a single line of columns the margin read +393 mm,
        // comfortably "stable", while the level slab it hung on was cantilevered 1.64 m off
        // a 0.29 m column top and would rotate off it. Gating on that number would have
        // overruled both trends and passed the assembly. Judging an interface at a time is
        // what that case actually needs; until then the solver's own signals decide.
        return RecordNodeTransforms(
            nodes,
            finalXform,
            stabilityThreshold,
            lengthToMeters,
            diverging || turning,
            graph);
    }

    /// <summary>
    /// Classifies a motion history as settling or diverging by comparing how much the
    /// assembly moved during the final quarter of the run against the quarter before it.
    /// Growth means the motion is still accelerating, which for a body under gravity alone
    /// means it is falling or toppling rather than coming to rest.
    /// </summary>
    private static bool IsDivergingMotion(List<double> samples)
    {
        if (samples == null || samples.Count < 4)
        {
            return false;
        }

        var quarter = Math.Max(1, samples.Count / 4);
        var lastStart = samples.Count - quarter;

        // Both windows must span the same number of intervals, or the comparison is
        // meaningless. Taking previousStart as lastStart - quarter made the earlier window
        // one interval shorter than the later one, so steady non-accelerating growth came
        // out looking like acceleration by roughly a factor of two and every drifting
        // assembly was reported as diverging.
        var previousStart = Math.Max(0, lastStart - 1 - quarter);
        if (lastStart - 1 <= previousStart)
        {
            return false;
        }

        var lastGrowth = samples[samples.Count - 1] - samples[lastStart - 1];
        var previousGrowth = samples[lastStart - 1] - samples[previousStart];
        if (!double.IsFinite(lastGrowth) || !double.IsFinite(previousGrowth))
        {
            return false;
        }

        // A settling assembly's growth decays; require a clear margin so that solver noise
        // on an essentially static body is not read as collapse.
        return lastGrowth > previousGrowth * DivergenceGrowthMargin &&
            lastGrowth > DivergenceMinGrowthMeters;
    }

    /// <summary>
    /// True while an assembly is still turning at the end of the run. Unlike
    /// <see cref="IsDivergingMotion"/> this does not ask for acceleration: a topple driven
    /// by gravity alone against a very stiff rigid goal advances almost linearly, so steady
    /// growth is already the signal. A body at rest converges instead, and its remaining
    /// rotation is the floor's elastic compliance, which stops changing.
    /// </summary>
    private static bool IsGrowingRotation(List<double> samples)
    {
        if (samples == null || samples.Count < 4)
        {
            return false;
        }

        var quarter = Math.Max(1, samples.Count / 4);
        var lastStart = samples.Count - quarter;
        // Same equal-window requirement as the motion test; see the note there.
        var previousStart = Math.Max(0, lastStart - 1 - quarter);
        if (lastStart - 1 <= previousStart)
        {
            return false;
        }

        var lastGrowth = samples[samples.Count - 1] - samples[lastStart - 1];
        var previousGrowth = samples[lastStart - 1] - samples[previousStart];
        if (!double.IsFinite(lastGrowth) || !double.IsFinite(previousGrowth))
        {
            return false;
        }

        // Growth must be sustained rather than a single step of solver noise: the last
        // quarter has to turn at least as fast as the quarter before it, and by more than
        // the noise floor. A settling assembly fails the first test, since its compliance
        // rotation decays; a resting one fails the second.
        return lastGrowth > RotationNoiseFloorDegrees &&
            lastGrowth >= previousGrowth * RotationDecayMargin;
    }

    /// <summary>
    /// Signed distance from the assembly's centre of mass to the boundary of the convex
    /// hull of its ground contacts, positive inside. This is the classical rigid-body
    /// overturning test, and unlike the solver it is exact and costs nothing, so it is
    /// reported alongside the simulation and can fail an assembly the solver has not had
    /// the steps to topple.
    /// </summary>
    private static bool TrySupportMargin(
        IReadOnlyList<(Point3d Point, double AreaM2)> contactSites,
        Point3d centreOfMass,
        double floorZMeters,
        out double marginMeters,
        out int groundSiteCount)
    {
        marginMeters = 0.0;
        groundSiteCount = 0;
        if (contactSites == null || !centreOfMass.IsValid)
        {
            return false;
        }

        var ground = new List<Point2d>();
        foreach (var site in contactSites)
        {
            if (site.Point.Z <= floorZMeters + GroundContactToleranceMeters)
            {
                ground.Add(new Point2d(site.Point.X, site.Point.Y));
            }
        }

        groundSiteCount = ground.Count;
        var hull = ConvexHullXY(ground);
        if (hull.Count < 3)
        {
            return false;
        }

        marginMeters = SignedMarginToPolygon(hull, new Point2d(centreOfMass.X, centreOfMass.Y));
        return true;
    }

    /// <summary>Andrew's monotone chain; returns the hull counter-clockwise.</summary>
    private static List<Point2d> ConvexHullXY(List<Point2d> points)
    {
        var hull = new List<Point2d>();
        if (points.Count < 3)
        {
            return hull;
        }

        var sorted = points
            .OrderBy(point => point.X)
            .ThenBy(point => point.Y)
            .ToList();

        double Cross(Point2d o, Point2d a, Point2d b) =>
            ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

        var lower = new List<Point2d>();
        foreach (var point in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], point) <= 0.0)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(point);
        }

        var upper = new List<Point2d>();
        for (var i = sorted.Count - 1; i >= 0; i--)
        {
            var point = sorted[i];
            while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], point) <= 0.0)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        hull.AddRange(lower);
        hull.AddRange(upper);
        return hull;
    }

    /// <summary>Positive inside, negative outside; magnitude is distance to the boundary.</summary>
    private static double SignedMarginToPolygon(List<Point2d> polygon, Point2d query)
    {
        var inside = true;
        var best = double.MaxValue;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var edgeX = b.X - a.X;
            var edgeY = b.Y - a.Y;
            if ((edgeX * (query.Y - a.Y)) - (edgeY * (query.X - a.X)) < 0.0)
            {
                inside = false;
            }

            var lengthSquared = (edgeX * edgeX) + (edgeY * edgeY);
            var t = lengthSquared <= 0.0
                ? 0.0
                : Math.Max(0.0, Math.Min(1.0,
                    (((query.X - a.X) * edgeX) + ((query.Y - a.Y) * edgeY)) / lengthSquared));
            var dx = query.X - (a.X + (t * edgeX));
            var dy = query.Y - (a.Y + (t * edgeY));
            best = Math.Min(best, Math.Sqrt((dx * dx) + (dy * dy)));
        }

        return inside ? best : -best;
    }

    private static bool RecordNodeTransforms(
        List<StabilityNode> nodes,
        Transform xform,
        double stabilityThreshold,
        double lengthToMeters,
        bool diverging,
        JObject graph)
    {
        var maxDisplacement = 0.0;
        var rotation = RotationFromTransform(xform);
        var matrix = TransformMatrix(xform);

        foreach (var node in nodes)
        {
            if (!TryGeometryCenter(node.Geometry, out var center))
            {
                var displacement = Vector3d.Zero;
                node.Node["displacement"] = new JObject
                {
                    ["x"] = displacement.X,
                    ["y"] = displacement.Y,
                    ["z"] = displacement.Z,
                    ["length"] = 0.0,
                    ["length_m"] = 0.0
                };
                node.Node.Remove("rotation_degrees");
                node.Node["rotation"] = rotation;
                node.Node["transform"] = matrix;
                continue;
            }

            var movedCenter = new Point3d(center);
            movedCenter.Transform(xform);
            var movedDisplacement = movedCenter - center;
            var displacementLength = movedDisplacement.Length;
            maxDisplacement = Math.Max(maxDisplacement, displacementLength);

            node.Node["displacement"] = new JObject
            {
                ["x"] = movedDisplacement.X,
                ["y"] = movedDisplacement.Y,
                ["z"] = movedDisplacement.Z,
                ["length"] = displacementLength,
                ["length_m"] = displacementLength * lengthToMeters
            };
            node.Node.Remove("rotation_degrees");
            node.Node["rotation"] = rotation;
            node.Node["transform"] = matrix;
        }

        var maxDisplacementMeters = maxDisplacement * lengthToMeters;
        var stabilityThresholdMeters = stabilityThreshold * lengthToMeters;

        // The soft floor means a sound assembly still sinks a long way, so displacement
        // alone cannot separate settling from collapse - a stable pad sank 342 mm while a
        // toppling deck moved 2786 mm, and the gap between those is contact area, not
        // stability. Rotation does separate them cleanly: the pad turned 0.000 deg, the
        // deck 32.9 deg about the edge of its supports.
        var rotationDegrees = RotationDegreesFromTransform(xform);
        graph["rotation_deg"] = rotationDegrees;
        graph["rotation_threshold_deg"] = DefaultRotationThresholdDegrees;
        // Diverging motion is a collapse whatever the pose happens to read at the moment
        // the run stopped, so it overrides the rotation comparison outright.
        graph["stable"] = !diverging && rotationDegrees <= DefaultRotationThresholdDegrees;
        graph["stability_threshold"] = stabilityThreshold;
        graph["stability_threshold_m"] = stabilityThresholdMeters;
        graph["max_displacement"] = maxDisplacement;
        graph["max_displacement_m"] = maxDisplacementMeters;
        return graph["stable"].Value<bool>();
    }

    /// <summary>
    /// Rotation magnitude of a rigid transform, in degrees, from the trace of its linear
    /// part. Reported on its own because it, not displacement, decides the verdict.
    /// </summary>
    private static double RotationDegreesFromTransform(Transform xform)
    {
        var cosAngle = (xform.M00 + xform.M11 + xform.M22 - 1.0) * 0.5;
        cosAngle = Math.Max(-1.0, Math.Min(1.0, cosAngle));
        return RhinoMath.ToDegrees(Math.Acos(cosAngle));
    }

    private static JObject RotationFromTransform(Transform xform)
    {
        var cosAngle = (xform.M00 + xform.M11 + xform.M22 - 1.0) * 0.5;
        cosAngle = Math.Max(-1.0, Math.Min(1.0, cosAngle));
        var angle = Math.Acos(cosAngle);

        var axis = Vector3d.Zero;
        if (angle <= 1e-10)
        {
            axis = Vector3d.Zero;
        }
        else if (Math.Abs(Math.PI - angle) <= 1e-6)
        {
            var x = Math.Sqrt(Math.Max(0.0, (xform.M00 + 1.0) * 0.5));
            var y = Math.Sqrt(Math.Max(0.0, (xform.M11 + 1.0) * 0.5));
            var z = Math.Sqrt(Math.Max(0.0, (xform.M22 + 1.0) * 0.5));
            if (x >= y && x >= z && x > 1e-10)
            {
                y = (xform.M01 + xform.M10) / (4.0 * x);
                z = (xform.M02 + xform.M20) / (4.0 * x);
            }
            else if (y >= z && y > 1e-10)
            {
                x = (xform.M01 + xform.M10) / (4.0 * y);
                z = (xform.M12 + xform.M21) / (4.0 * y);
            }
            else if (z > 1e-10)
            {
                x = (xform.M02 + xform.M20) / (4.0 * z);
                y = (xform.M12 + xform.M21) / (4.0 * z);
            }

            axis = new Vector3d(x, y, z);
            axis.Unitize();
        }
        else
        {
            var scale = 2.0 * Math.Sin(angle);
            axis = new Vector3d(
                (xform.M21 - xform.M12) / scale,
                (xform.M02 - xform.M20) / scale,
                (xform.M10 - xform.M01) / scale);
            axis.Unitize();
        }

        return new JObject
        {
            ["angle_degrees"] = angle * 180.0 / Math.PI,
            ["axis"] = new JObject
            {
                ["x"] = axis.X,
                ["y"] = axis.Y,
                ["z"] = axis.Z
            }
        };
    }

    private static JArray TransformMatrix(Transform xform)
    {
        var matrix = new JArray();
        matrix.Add(new JArray(xform.M00, xform.M01, xform.M02, xform.M03));
        matrix.Add(new JArray(xform.M10, xform.M11, xform.M12, xform.M13));
        matrix.Add(new JArray(xform.M20, xform.M21, xform.M22, xform.M23));
        matrix.Add(new JArray(xform.M30, xform.M31, xform.M32, xform.M33));
        return matrix;
    }

    private static JObject SerializableGraph(JObject graph)
    {
        var result = (JObject)graph.DeepClone();
        var nodes = result["n"] as JArray;
        if (nodes != null)
        {
            var serializableNodes = new JArray();
            foreach (var nodeToken in nodes)
            {
                if (nodeToken is not JObject node)
                {
                    continue;
                }

                var storedNode = (JObject)node.DeepClone();
                storedNode.Remove("geo");
                serializableNodes.Add(storedNode);
            }

            result["n"] = serializableNodes;
        }

        return result;
    }

    /// <summary>
    /// Area each mesh vertex is responsible for: every triangle hands a third of its area
    /// to each of its corners, and a quad is split on its A-C diagonal first.
    /// </summary>
    private static double[] TributaryVertexAreas(Mesh mesh)
    {
        var areas = new double[mesh.Vertices.Count];
        for (var faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
        {
            var face = mesh.Faces[faceIndex];
            AddTriangleArea(mesh, areas, face.A, face.B, face.C);
            if (face.IsQuad)
            {
                AddTriangleArea(mesh, areas, face.A, face.C, face.D);
            }
        }

        return areas;
    }

    private static void AddTriangleArea(Mesh mesh, double[] areas, int a, int b, int c)
    {
        if (a < 0 || b < 0 || c < 0 ||
            a >= areas.Length || b >= areas.Length || c >= areas.Length)
        {
            return;
        }

        var pa = (Point3d)mesh.Vertices[a];
        var pb = (Point3d)mesh.Vertices[b];
        var pc = (Point3d)mesh.Vertices[c];
        var area = 0.5 * Vector3d.CrossProduct(pb - pa, pc - pa).Length;
        if (!double.IsFinite(area) || area <= 0.0)
        {
            return;
        }

        var share = area / 3.0;
        areas[a] += share;
        areas[b] += share;
        areas[c] += share;
    }

    /// <summary>
    /// Quantises a solver-space point onto the same grid Kangaroo uses to merge coincident
    /// particles, so that goals can be summed per particle before they are handed over.
    /// </summary>
    private static bool TrySiteKey(Point3d point, double toleranceMeters, out (long, long, long) key)
    {
        key = default;
        if (!point.IsValid || !double.IsFinite(toleranceMeters) || toleranceMeters <= 0.0)
        {
            return false;
        }

        var x = Math.Round(point.X / toleranceMeters);
        var y = Math.Round(point.Y / toleranceMeters);
        var z = Math.Round(point.Z / toleranceMeters);
        if (Math.Abs(x) > long.MaxValue || Math.Abs(y) > long.MaxValue || Math.Abs(z) > long.MaxValue)
        {
            return false;
        }

        key = ((long)x, (long)y, (long)z);
        return true;
    }

    private static bool TryVolumeCentroid(Mesh mesh, out Point3d centroid)
    {
        centroid = Point3d.Unset;
        try
        {
            var properties = VolumeMassProperties.Compute(mesh);
            if (properties == null || !(properties.Volume > 0.0))
            {
                return false;
            }

            centroid = properties.Centroid;
            return centroid.IsValid;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Rebalances load shares so that their resultant acts on <paramref name="target"/>,
    /// by scaling each share with a linear ramp across the body: p' = p * (1 + a . (x - c)).
    /// The ramp sums to zero by construction, so the total load is untouched; solving the
    /// 3x3 covariance system for a moves the load centroid onto the target exactly.
    /// </summary>
    private static double[] SharesAtCentroid(
        IReadOnlyList<Point3d> points,
        double[] shares,
        Point3d target)
    {
        if (points.Count != shares.Length || points.Count < 4 || !target.IsValid)
        {
            return shares;
        }

        var centre = Point3d.Origin;
        for (var i = 0; i < shares.Length; i++)
        {
            centre += points[i] * shares[i];
        }

        var offset = target - centre;
        if (!offset.IsValid || offset.Length <= RhinoMath.ZeroTolerance)
        {
            return shares;
        }

        // Covariance of the load points about their own centroid.
        double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
        var deltas = new Vector3d[shares.Length];
        for (var i = 0; i < shares.Length; i++)
        {
            var d = points[i] - centre;
            deltas[i] = d;
            m00 += shares[i] * d.X * d.X;
            m01 += shares[i] * d.X * d.Y;
            m02 += shares[i] * d.X * d.Z;
            m11 += shares[i] * d.Y * d.Y;
            m12 += shares[i] * d.Y * d.Z;
            m22 += shares[i] * d.Z * d.Z;
        }

        var det =
            (m00 * ((m11 * m22) - (m12 * m12))) -
            (m01 * ((m01 * m22) - (m12 * m02))) +
            (m02 * ((m01 * m12) - (m11 * m02)));

        // A flat or collinear load cloud has no spread along some axis, so no ramp can move
        // the centroid that way. Leave the area-weighted shares alone rather than inverting
        // a singular system.
        var scale = (m00 + m11 + m22) / 3.0;
        if (!double.IsFinite(det) || scale <= 0.0 || Math.Abs(det) < 1e-12 * scale * scale * scale)
        {
            return shares;
        }

        var a = new Vector3d(
            (offset.X * ((m11 * m22) - (m12 * m12))) -
            (m01 * ((offset.Y * m22) - (m12 * offset.Z))) +
            (m02 * ((offset.Y * m12) - (m11 * offset.Z))),
            (m00 * ((offset.Y * m22) - (m12 * offset.Z))) -
            (offset.X * ((m01 * m22) - (m12 * m02))) +
            (m02 * ((m01 * offset.Z) - (offset.Y * m02))),
            (m00 * ((m11 * offset.Z) - (offset.Y * m12))) -
            (m01 * ((m01 * offset.Z) - (offset.Y * m02))) +
            (offset.X * ((m01 * m12) - (m11 * m02))));
        a /= det;
        if (!a.IsValid)
        {
            return shares;
        }

        // The ramp must never drive a share to zero or below. If it would, pull it back
        // until the smallest share keeps a tenth of its area-weighted value and accept the
        // residual centroid offset - a partial correction still beats a negative mass.
        var lowest = 0.0;
        for (var i = 0; i < shares.Length; i++)
        {
            lowest = Math.Min(lowest, a * deltas[i]);
        }

        if (1.0 + lowest < 0.1)
        {
            a *= 0.9 / -lowest;
        }

        var corrected = new double[shares.Length];
        var total = 0.0;
        for (var i = 0; i < shares.Length; i++)
        {
            corrected[i] = shares[i] * (1.0 + (a * deltas[i]));
            total += corrected[i];
        }

        if (!double.IsFinite(total) || total <= 0.0)
        {
            return shares;
        }

        for (var i = 0; i < corrected.Length; i++)
        {
            corrected[i] /= total;
        }

        return corrected;
    }

    private static List<Point3d> MeshVerticesAsPoints(Mesh mesh)
    {
        var points = new List<Point3d>();
        if (mesh == null)
        {
            return points;
        }

        foreach (var vertex in mesh.Vertices)
        {
            points.Add(new Point3d(vertex.X, vertex.Y, vertex.Z));
        }

        return points;
    }

    /// <summary>
    /// Lowest point of the assembly in document units, used as the floor elevation when the
    /// caller does not pin one down.
    /// </summary>
    private static double AssemblyMinimumZ(List<StabilityNode> nodes)
    {
        var lowest = double.PositiveInfinity;
        foreach (var node in nodes)
        {
            if (node?.Geometry == null)
            {
                continue;
            }

            var box = node.Geometry.GetBoundingBox(true);
            if (box.IsValid && box.Min.Z < lowest)
            {
                lowest = box.Min.Z;
            }
        }

        return double.IsFinite(lowest) ? lowest : DefaultFloorZ;
    }

    private static bool TryGeometryCenter(GeometryBase geometry, out Point3d center)
    {
        center = Point3d.Unset;
        if (geometry == null)
        {
            return false;
        }

        try
        {
            var bbox = geometry.GetBoundingBox(true);
            if (bbox.IsValid)
            {
                center = bbox.Center;
                return true;
            }
        }
        catch
        {
            // ignore and fall back
        }

        return false;
    }

    private static Mesh AsMesh(GeometryBase geometry)
    {
        if (geometry == null)
        {
            return null;
        }

        if (geometry is Mesh mesh)
        {
            return mesh.DuplicateMesh();
        }

        var brep = AsBrep(geometry);
        if (brep == null)
        {
            return null;
        }

        var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.Default);
        if (meshes == null || meshes.Length == 0)
        {
            return null;
        }

        var result = new Mesh();
        foreach (var part in meshes)
        {
            result.Append(part);
        }

        if (result.Vertices.Count == 0)
        {
            return null;
        }

        result.Normals.ComputeNormals();
        result.Compact();
        return result;
    }

    private static Brep AsBrep(GeometryBase geometry)
    {
        if (geometry == null)
        {
            return null;
        }

        switch (geometry)
        {
            case Brep brep:
                return brep.DuplicateBrep();
            case Extrusion extrusion:
                return extrusion.ToBrep();
            case Surface surface:
                return surface.ToBrep();
            case Mesh mesh:
                return Brep.CreateFromMesh(mesh, true);
            case Curve curve:
                var planarBreps = Brep.CreatePlanarBreps(curve, 0.001);
                return planarBreps != null && planarBreps.Length > 0 ? planarBreps[0] : null;
            default:
                return null;
        }
    }

    // Picks the lexicographically smallest point, so the choice follows the assembly's
    // geometry rather than the order its nodes arrived in. Coordinates are compared with a
    // tolerance so that float noise cannot flip the winner between otherwise equal runs.
    private static int CanonicalPointIndex(
        List<(int VertexIndex, int GlobalIndex, Point3d Point)> records)
    {
        const double tolerance = 1e-9;
        var bestIndex = 0;
        for (var i = 1; i < records.Count; i++)
        {
            var candidate = records[i].Point;
            var best = records[bestIndex].Point;
            var dx = candidate.X - best.X;
            if (Math.Abs(dx) > tolerance)
            {
                if (dx < 0.0) bestIndex = i;
                continue;
            }

            var dy = candidate.Y - best.Y;
            if (Math.Abs(dy) > tolerance)
            {
                if (dy < 0.0) bestIndex = i;
                continue;
            }

            var dz = candidate.Z - best.Z;
            if (Math.Abs(dz) > tolerance && dz < 0.0)
            {
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static (int Item1, int Item2, int Item3)? FrameIndices(List<Point3d> points)
    {
        if (points.Count < 3)
        {
            return null;
        }

        // Match the Python reference-frame selection exactly.
        var i0 = 0;
        var p0 = points[i0];
        var i1 = 0;
        var maxDistanceSquared = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var distanceSquared = points[i].DistanceToSquared(p0);
            if (distanceSquared > maxDistanceSquared)
            {
                maxDistanceSquared = distanceSquared;
                i1 = i;
            }
        }

        var xAxis = points[i1] - p0;
        if (!double.IsFinite(maxDistanceSquared) || maxDistanceSquared <= 1e-30)
        {
            return null;
        }

        var i2 = -1;
        var maxCross = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            if (i == i0 || i == i1)
            {
                continue;
            }

            var cross = Vector3d.CrossProduct(xAxis, points[i] - p0);
            if (cross.SquareLength > maxCross)
            {
                maxCross = cross.SquareLength;
                i2 = i;
            }
        }

        if (i2 < 0 || IsDegenerateCross(maxCross, maxDistanceSquared))
        {
            return null;
        }

        return (i0, i1, i2);
    }

    private static bool IsDegenerateCross(double crossSquareLength, double referenceLengthSquared)
    {
        if (!double.IsFinite(crossSquareLength) ||
            !double.IsFinite(referenceLengthSquared) ||
            referenceLengthSquared <= 0.0)
        {
            return true;
        }

        // Cross-product squared has units L^4. Use a relative test so meter
        // normalization does not reject valid small models that were authored
        // in millimeters or inches.
        return crossSquareLength <= Math.Max(
            1e-48,
            referenceLengthSquared * referenceLengthSquared * 1e-24);
    }

    private static void WriteAfterEvaluationFullGeometry(RhinoObject obj, JObject geometry, JObject fullMesh)
    {
        if (obj == null || geometry == null || fullMesh == null)
        {
            return;
        }

        var payload = new JObject
        {
            ["geometry"] = geometry,
            ["full_mesh"] = fullMesh
        };

        obj.Attributes.SetUserString(AfterEvaluationKey, payload.ToString(Newtonsoft.Json.Formatting.None));
        obj.CommitChanges();
    }

    private static void WriteAfterEvaluationObb(RhinoObject obj, JObject geometry)
    {
        if (obj == null || geometry == null)
        {
            return;
        }

        var payload = new JObject
        {
            ["geometry"] = geometry
        };

        obj.Attributes.SetUserString(AfterEvaluationKey, payload.ToString(Newtonsoft.Json.Formatting.None));
        obj.CommitChanges();
    }

    /// <summary>
    /// A unilateral floor contact carrying its own stiffness per point.
    /// </summary>
    /// <remarks>
    /// Kangaroo's own Floor2 does two things this solver cannot use. It shares one scalar
    /// strength across every point it is given, which is what made footing stiffness track
    /// mesh density; and it pins a contacting point laterally to a remembered target, which
    /// behaves as glue rather than as a resting contact. The glue is not cosmetic: a 13.4 t
    /// tower whose centre of mass sat 198 mm outside its support stood up at
    /// floor_strength 1e7 and 1e6, and only broke loose at 1e5. Since the pin strength
    /// scales with the floor strength, stiffening the floor to keep settling shallow also
    /// cemented the assembly to the ground, and a real topple went undetected.
    ///
    /// This goal pushes a penetrating point straight up to the floor and does nothing at
    /// all to a point above it - no lateral term, no memory between steps.
    /// </remarks>
    private sealed class AreaFloor : GoalObject
    {
        private readonly double[] _strengths;
        private readonly double _limit;

        public AreaFloor(List<Point3d> points, List<double> strengths, double limit)
        {
            if (points == null || strengths == null || points.Count != strengths.Count)
            {
                throw new ArgumentException("AreaFloor needs one strength per contact point.");
            }

            _limit = limit;
            _strengths = strengths.ToArray();
            PPos = points.ToArray();
            Move = new Vector3d[points.Count];
            Weighting = new double[points.Count];
        }

        public override void Calculate(List<KangarooSolver.Particle> p)
        {
            for (var i = 0; i < PIndex.Length; i++)
            {
                var height = p[PIndex[i]].Position.Z;
                if (height < _limit)
                {
                    Move[i] = new Vector3d(0.0, 0.0, _limit - height);
                    Weighting[i] = _strengths[i];
                }
                else
                {
                    Move[i] = Vector3d.Zero;
                    Weighting[i] = 0.0;
                }
            }
        }
    }

    private sealed class StabilityNode
    {
        public JObject Node { get; set; }
        public GeometryBase Geometry { get; set; }
        public double MassKilograms { get; set; }
    }
}
