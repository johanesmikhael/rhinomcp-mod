using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoMCPModPlugin;
using RhinoMCPModPlugin.Functions;
using static RhinoMCPModPlugin.Functions.RhinoMCPModFunctions;

namespace RhinoMCPModPlugin.Commands
{
    public class MCPModevaluateStabilityCommand : Command
    {
        public MCPModevaluateStabilityCommand()
        {
            Instance = this;
        }

        public static MCPModevaluateStabilityCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodevaluatestability";


        /// <summary>
        /// Captures the set of objects to evaluate. Honours pre-selection; otherwise
        /// prompts. "Pinned" reuses the scope the graph display is showing, so what you
        /// were just looking at is what gets solved.
        /// </summary>
        private static bool TryReadScope(RhinoDoc doc, JObject parameters, out string label)
        {
            label = "whole document";

            var getObject = new GetObject();
            getObject.SetCommandPrompt("Select objects to evaluate (Enter = whole document)");
            var allOption = getObject.AddOption("All");
            var pinnedOption = getObject.AddOption("Pinned");
            getObject.EnablePreSelect(true, true);
            getObject.EnablePostSelect(true);
            getObject.SubObjectSelect = false;
            getObject.GroupSelect = true;
            getObject.AcceptNothing(true);

            var result = getObject.GetMultiple(1, 0);

            if (result == GetResult.Option)
            {
                var index = getObject.Option()?.Index ?? -1;
                if (index == allOption)
                {
                    return true;
                }

                if (index == pinnedOption)
                {
                    // Whatever the graph is showing, in whichever way it was scoped.
                    //
                    // This used to accept an id list and nothing else, so a graph pinned to
                    // the whole document - which is what graph_display(scope="all") sets, and
                    // what the overlay shows by default - was refused with "run mcpmodgraph
                    // with a selection first". The graph was on screen at the time. A scope
                    // with no id list is not a missing scope; it is a scope that names its
                    // objects some other way, and every way it can is translated here.
                    var pinned = MCPConnectivityGraphController.PinnedScope;
                    if (pinned == null)
                    {
                        RhinoApp.WriteLine(
                            "No graph is pinned; run mcpmodgraph, or graph_display over MCP.");
                        return false;
                    }

                    if (pinned.IsWholeDocument)
                    {
                        label = "pinned graph scope (whole document)";
                        return true;
                    }

                    var parts = new List<string>();
                    if (pinned.Ids != null && pinned.Ids.Count > 0)
                    {
                        parameters["ids"] = new JArray(
                            pinned.Ids.Select(id => (object)id.ToString()).ToArray());
                        parts.Add($"{pinned.Ids.Count} objects");
                    }

                    if (pinned.Layers != null && pinned.Layers.Count > 0)
                    {
                        parameters["layer"] = new JArray(
                            pinned.Layers.Select(name => (object)name).ToArray());
                        parts.Add($"layers {string.Join(", ", pinned.Layers)}");
                    }

                    if (pinned.SelectedOnly)
                    {
                        parameters["selected"] = true;
                        parts.Add("current selection");
                    }

                    if (parts.Count == 0)
                    {
                        RhinoApp.WriteLine(
                            "The pinned graph scope names nothing this command can evaluate.");
                        return false;
                    }

                    label = $"pinned graph scope ({string.Join("; ", parts)})";
                    return true;
                }

                return false;
            }

            if (result == GetResult.Nothing)
            {
                return true;
            }

            if (result != GetResult.Object)
            {
                return false;
            }

            var ids = new JArray();
            var count = 0;
            for (var i = 0; i < getObject.ObjectCount; i++)
            {
                var objectId = getObject.Object(i)?.ObjectId ?? Guid.Empty;
                if (objectId != Guid.Empty)
                {
                    ids.Add(objectId.ToString());
                    count++;
                }
            }

            if (count == 0)
            {
                return true;
            }

            parameters["ids"] = ids;
            label = $"{count} selected objects";
            return true;
        }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (!StabilityUnits.TryCreate(doc.ModelUnitSystem, out var unitContext, out var unitError))
            {
                RhinoApp.WriteLine($"EvaluateStability failed: {unitError}");
                return Result.Failure;
            }

            var handler = new RhinoMCPModFunctions();
            var parameters = new JObject();

            // Scope first, so pre-selected objects are picked up before any other prompt.
            // Evaluating the whole document welds every object in the file into one body,
            // which is almost never what you want once a file holds more than one thing.
            if (!TryReadScope(doc, parameters, out var scopeLabel))
            {
                return Result.Cancel;
            }

            RhinoApp.WriteLine($"Stability scope: {scopeLabel}.");
            var defaultStabilityThreshold =
                unitContext.FromMeters(DefaultStabilityThresholdMeters);
            var defaultAssignTolerance =
                unitContext.FromMeters(DefaultAssignToleranceMeters);
            var defaultSolverThreshold =
                unitContext.FromMeters(DefaultSolverThresholdMeters);

            // How the scope is modelled, which is a different question from what its joints
            // are.
            //
            // These options used to be named Welded / Contact / PinnedJoints, after the three
            // solvers behind them, and that name hid the distinction that matters now:
            // "welded" is not "every joint is welded", it is *no joints at all* - the whole
            // scope fused into one rigid body that either tips or does not. The other two were
            // one body per element, differing only in what their joints were assumed to be,
            // and that assumption is now something the model states per joint. So the first
            // question is how many bodies there are, and the second is what to assume where
            // nothing was stated.
            var getEvalMode = new GetOption();
            getEvalMode.SetCommandPrompt("Model the scope as");
            var assemblyOption = getEvalMode.AddOption("Assembly");
            var elementsOption = getEvalMode.AddOption("Elements");
            getEvalMode.AcceptNothing(true);

            var evalModeResult = getEvalMode.Get();
            if (evalModeResult == GetResult.Cancel)
            {
                return Result.Cancel;
            }

            var evalModeIndex = getEvalMode.Option()?.Index ?? -1;
            var multiBody = evalModeIndex == elementsOption;
            var evaluationMode = multiBody
                ? RhinoMCPModFunctions.ElementsMode
                : RhinoMCPModFunctions.AssemblyMode;
            parameters["mode"] = evaluationMode;

            if (!multiBody)
            {
                RhinoApp.WriteLine(
                    "Assembly: the whole scope as one rigid body. It has no joints, so joint " +
                    "types do not apply; it answers only whether that body tips or slides.");
            }

            if (multiBody)
            {
                var pairRules = ReadPairRules(doc);
                var ruleCount = pairRules.Count;
                var staleCount = CountStaleRules(doc, pairRules);

                // What a joint is where no rule names one. Rules beat it, so this is a default
                // and is prompted as one - the old naming implied it was the answer.
                var getDefaultJoint = new GetOption();
                getDefaultJoint.SetCommandPrompt(
                    ruleCount == 0
                        ? "Joint type where no rule names one (no rules are set)"
                        : staleCount == 0
                            ? $"Joint type where no rule names one ({ruleCount} rules will override it)"
                            : $"Joint type where no rule names one ({ruleCount} rules, {staleCount} stale " +
                              "and naming nothing in the document - mcpmodassignjointtype Prune removes them)");
                // Contact first, because it is the default and the prompt should say so by
                // its order as well as by what it does when nothing is chosen.
                var contactJointOption = getDefaultJoint.AddOption("Contact");
                var pinJointOption = getDefaultJoint.AddOption("Pin");
                var fixedJointOption = getDefaultJoint.AddOption("Fixed");
                getDefaultJoint.AcceptNothing(true);

                if (getDefaultJoint.Get() == GetResult.Cancel)
                {
                    return Result.Cancel;
                }

                var jointIndex = getDefaultJoint.Option()?.Index ?? -1;
                var defaultJoint = "contact";
                if (jointIndex == pinJointOption)
                {
                    defaultJoint = "pin";
                }
                else if (jointIndex == fixedJointOption)
                {
                    defaultJoint = "fixed";
                }

                parameters["joint_type"] = defaultJoint;

                // The rigid-body integrator, not offered as a choice.
                //
                // The particle path cannot represent a joint type at all: a body there is
                // particles held to a fitted frame by Kangaroo's RigidMesh, which takes one
                // strength for all of a body's points, and a joint is a shared particle rather
                // than a spring. One point has no lever arm, so it is a pin by construction -
                // welded has nowhere to put its moment and a shared particle can never open,
                // so contact cannot happen either.
                //
                // Offering it here would let someone assign joint types, see them drawn in the
                // overlay, and get an answer that quietly ignored every one of them. A warning
                // was the first attempt and is a weaker fix than not offering the trap: this
                // is the human-facing surface, and the choice it was offering was between a
                // model of what you asked for and a model of something else. The particle path
                // stays reachable over MCP with integrator="particles", where it earns its
                // keep as the calibrated reference the closed-form cases are checked against.
                parameters["integrator"] = "rigid_bodies";
            }

            var getOption = new GetOption();
            getOption.SetCommandPrompt("Stability parameter mode");
            var defaultsOption = getOption.AddOption("Defaults");
            var customOption = getOption.AddOption("Custom");
            getOption.AcceptNothing(true);

            var optionResult = getOption.Get();
            var selectedMode = getOption.Option()?.Index ?? -1;

            if (optionResult == GetResult.Cancel)
            {
                return Result.Cancel;
            }

            if ((optionResult == GetResult.Nothing || selectedMode == defaultsOption) && multiBody)
            {
                // The multi-body solver sizes everything it needs from the document - joint
                // stiffness from each member, the run length from the span, the ground from
                // the joints. Only gravity is a number worth stating. It used to receive the
                // assembly mode's defaults here, which it never read, so "Defaults" looked
                // like a choice and was not one.
                parameters["gravity"] = DefaultGravity;
            }
            else if (selectedMode == customOption && multiBody)
            {
                var getFloorMode = new GetOption();
                getFloorMode.SetCommandPrompt("Floor level");
                var autoFloorOption = getFloorMode.AddOption("Auto");
                getFloorMode.AddOption("Manual");
                getFloorMode.AcceptNothing(true);
                var floorModeResult = getFloorMode.Get();
                if (floorModeResult == GetResult.Cancel)
                {
                    return Result.Cancel;
                }

                var floorZIsAuto = floorModeResult == GetResult.Nothing ||
                    getFloorMode.Option()?.Index == autoFloorOption;

                // The knobs the multi-body solver actually reads, in the order a reader
                // meets them: how long, how damped, how stiff, whether to probe sway.
                var parameterLabels = new[]
                {
                    "Duration (s)",
                    "Damping ratio (fraction of critical at a joint)",
                    $"Joint penetration ({doc.ModelUnitSystem}; sizes joint stiffness where none is stated)",
                    "Joint stiffness (N/m, 0 = derived per member)",
                    "Sway probe (fraction of weight, 0 = off)",
                    $"Floor Z ({doc.ModelUnitSystem})",  // skipped when the floor is auto
                    "Gravity (m/s²)"
                };
                var values = new double[]
                {
                    StabilityDynamics.DefaultDurationSeconds,
                    StabilityRigidBodies.DefaultDampingRatio,
                    unitContext.FromMeters(DefaultJointPenetrationMeters),
                    0.0,
                    0.0,
                    DefaultFloorZ,
                    DefaultGravity
                };

                for (var i = 0; i < parameterLabels.Length; i++)
                {
                    if (i == 5 && floorZIsAuto)
                    {
                        continue;
                    }

                    var getNumber = new GetNumber();
                    getNumber.SetCommandPrompt($"{parameterLabels[i]} = {values[i]}");
                    getNumber.SetDefaultNumber(values[i]);
                    getNumber.AcceptNothing(true);
                    var numberResult = getNumber.Get();
                    if (numberResult == GetResult.Number)
                    {
                        values[i] = getNumber.Number();
                    }
                    else if (numberResult == GetResult.Cancel)
                    {
                        return Result.Cancel;
                    }
                }

                parameters["duration_seconds"] = values[0];
                parameters["damping_ratio"] = values[1];
                parameters["joint_penetration"] = values[2];
                if (values[3] > 0.0)
                {
                    parameters["joint_stiffness_n_per_m"] = values[3];
                }

                if (values[4] > 0.0)
                {
                    parameters["lateral_load_fraction"] = values[4];
                }

                if (!floorZIsAuto)
                {
                    parameters["floor_z"] = values[5];
                }

                parameters["gravity"] = values[6];
            }
            else if (optionResult == GetResult.Nothing || selectedMode == defaultsOption)
            {
                parameters["current_step"] = DefaultCurrentStep;
                parameters["stability_threshold"] = defaultStabilityThreshold;
                // Both strengths are left unset on purpose: omitting floor_strength gives
                // the calibrated subgrade modulus, and omitting rigid_strength then keeps
                // the rigid goal above that floor. Pinning either one alone is what makes a
                // sound assembly read as unstable.
                // floor_z is left unset with the strengths: the solver then puts the floor
                // at the underside of the assembly, so a scope that excludes the pads its
                // columns stand on is evaluated standing on the ground rather than falling
                // to world zero.
                parameters["gravity"] = DefaultGravity;
                parameters["assign_tol"] = defaultAssignTolerance;
                parameters["threshold"] = defaultSolverThreshold;
                parameters["solver_substeps"] = DefaultSolverSubsteps;
            }
            else if (selectedMode == customOption)
            {
                // Zero is a perfectly good floor elevation, so it cannot double as the
                // "auto" sentinel the way a zero strength does. Ask outright instead.
                var getFloorMode = new GetOption();
                getFloorMode.SetCommandPrompt("Floor level");
                var autoFloorOption = getFloorMode.AddOption("Auto");
                getFloorMode.AddOption("Manual");
                getFloorMode.AcceptNothing(true);

                var floorModeResult = getFloorMode.Get();
                if (floorModeResult == GetResult.Cancel)
                {
                    return Result.Cancel;
                }

                var floorZIsAuto = floorModeResult == GetResult.Nothing ||
                    getFloorMode.Option()?.Index == autoFloorOption;

                var parameterLabels = new[]
                {
                    "Current step",
                    $"Stability threshold ({doc.ModelUnitSystem})",
                    "Rigid strength (0 = auto from floor)",
                    "Floor strength (0 = auto from mass and settlement)",
                    $"Floor Z ({doc.ModelUnitSystem})",  // skipped when the floor is auto
                    "Gravity (m/s²)",
                    $"Assign tolerance ({doc.ModelUnitSystem})",
                    $"Displacement threshold ({doc.ModelUnitSystem})",
                    "Solver substeps",
                    $"Ground settlement ({doc.ModelUnitSystem}; sizes the floor where no strength is stated)"
                };

                var values = new double[10]
                {
                    DefaultCurrentStep,
                    defaultStabilityThreshold,
                    0.0,
                    0.0,
                    DefaultFloorZ,
                    DefaultGravity,
                    defaultAssignTolerance,
                    defaultSolverThreshold,
                    DefaultSolverSubsteps,
                    unitContext.FromMeters(DefaultGroundSettlementMeters)
                };

                for (var i = 0; i < parameterLabels.Length; i++)
                {
                    if (i == 4 && floorZIsAuto)
                    {
                        continue;
                    }

                    var getNumber = new GetNumber();
                    getNumber.SetCommandPrompt($"{parameterLabels[i]} = {values[i]}");
                    getNumber.SetDefaultNumber(values[i]);
                    getNumber.AcceptNothing(true);

                    var numberResult = getNumber.Get();
                    if (numberResult == GetResult.Number)
                    {
                        values[i] = getNumber.Number();
                    }
                    else if (numberResult == GetResult.Cancel)
                    {
                        return Result.Cancel;
                    }
                }

                parameters["current_step"] = (int)values[0];
                parameters["stability_threshold"] = values[1];
                // Zero means auto for both strengths: leave the parameter out so the
                // solver sizes the floor from the assembly's mass and the rigid goal
                // from the floor.
                if (values[2] > 0.0)
                {
                    parameters["rigid_strength"] = values[2];
                }

                if (values[3] > 0.0)
                {
                    parameters["floor_strength"] = values[3];
                }

                if (!floorZIsAuto)
                {
                    parameters["floor_z"] = values[4];
                }

                parameters["gravity"] = values[5];
                parameters["assign_tol"] = values[6];
                parameters["threshold"] = values[7];
                parameters["solver_substeps"] = (int)values[8];
                parameters["ground_settlement"] = values[9];
            }
            else
            {
                return Result.Cancel;
            }

                // Prompt whether to display the evaluated geometry cache.
                var getDisplay = new GetOption();
                getDisplay.SetCommandPrompt("Display evaluated geometry cache?");
                var displayOn = getDisplay.AddOption("On");
                var displayOff = getDisplay.AddOption("Off");
                getDisplay.AcceptNothing(true);

                var displayResult = getDisplay.Get();
                if (displayResult == GetResult.Cancel)
                {
                    return Result.Cancel;
                }

                var selectedDisplay = getDisplay.Option()?.Index ?? -1;
                if (displayResult == GetResult.Option && selectedDisplay == displayOn)
                {
                    parameters["display"] = "On";
                }
                else if (displayResult == GetResult.Option && selectedDisplay == displayOff)
                {
                    parameters["display"] = "Off";
                }

                var result = handler.EvaluateStability(parameters);

            if (result["success"]?.Value<bool>() == true && multiBody)
            {
                // The multi-body result has no single assembly transform, no floor strength
                // and no support margin, so printing the welded lines would show blanks.
                // Printed from the keys the multi-body result actually carries. The block
                // used to read the particle path's names - worst_body, max_body_*, the
                // trends - which the rigid-body path never fills, so half the lines were
                // blank on every default run.
                var verdict = result["stable"]?.Value<bool>() == true ? "stable" : "unstable";
                var conclusive = result["conclusive"]?.Value<bool?>();
                var diverged = result["diverged"]?.Value<bool>() == true;
                var integrator = result["integrator"]?.ToString();
                RhinoApp.WriteLine(
                    $"EvaluateStability ({result["evaluation_mode"]}" +
                    (string.IsNullOrEmpty(integrator) ? "" : $", {integrator}") + $"): {verdict}" +
                    (diverged ? $" - DIVERGED: {result["diverged_reason"]}" :
                        conclusive == false ? " (inconclusive)" : ""));

                var counts = result["joint_type_counts"] as JObject;
                var typeText = counts == null
                    ? ""
                    : " (" + string.Join(", ", counts.Properties()
                        .Where(pr => (pr.Value.Value<int?>() ?? 0) > 0)
                        .Select(pr => $"{pr.Value} {pr.Name}")) + ")";
                RhinoApp.WriteLine(
                    $"bodies: {result["body_count"]}, joints: {result["joint_count"]}{typeText}, " +
                    $"default {result["joint_type_default"]}, rules {result["joint_type_pair_rules"] ?? 0}, " +
                    $"ground points: {result["anchored_ground_points"]}, " +
                    $"total mass: {Metres(result["total_mass_kg"], 1)} kg");

                if (result["max_pin_displacement_m"] != null)
                {
                    RhinoApp.WriteLine(
                        $"worst pin: {Millimetres(result["max_pin_displacement_m"])} mm against a " +
                        $"mechanism threshold of {Millimetres(result["mechanism_threshold_m"])} mm " +
                        $"(span {Metres(result["span_m"], 2)} m); " +
                        $"settled displacement {Millimetres(result["settled_displacement_m"])} mm");
                    RhinoApp.WriteLine(
                        $"run: {result["steps_run"]} steps, {Metres(result["simulated_seconds"], 3)} s simulated, " +
                        $"timestep {result["timestep_s"]} s, " +
                        (result["settled"]?.Value<bool>() == true ? "settled" : "not settled") +
                        $", peak speed {Metres(result["peak_speed_m_s"], 4)} m/s");
                }

                var sided = result["contact_joints_sided"]?.Value<int?>();
                if (sided.HasValue)
                {
                    RhinoApp.WriteLine(
                        $"contact joints: {sided} bearing, {result["contact_joints_open"]} open; " +
                        $"joints with a capacity: {result["joints_with_capacity"]}, " +
                        $"at capacity: {result["joints_at_capacity"]}");
                }

                if (result["joint_forces_summary"] is JObject forces)
                {
                    RhinoApp.WriteLine(
                        $"joint forces: {forces["count"]} records, max force {Metres(forces["max_force_n"], 0)} N, " +
                        $"max tension {Metres(forces["max_tension_n"], 0)} N, max shear {Metres(forces["max_shear_n"], 0)} N");
                    if (forces["top_by_tension"] is JArray top)
                    {
                        foreach (var joint in top.OfType<JObject>().Take(3))
                        {
                            RhinoApp.WriteLine(
                                $"  body {joint["body"]} ({joint["joint_type"]}) with {joint["with"]}: " +
                                $"tension {Metres(joint["tension_n"], 0)} N, shear {Metres(joint["shear_n"], 0)} N" +
                                (joint["reached_capacity"]?.Value<bool>() == true ? " - AT CAPACITY" : ""));
                        }
                    }
                }

                if (result["sway"] is JObject sway && sway["sway_stiffness_min_n_per_m"] != null)
                {
                    RhinoApp.WriteLine(
                        $"sway stiffness: x {sway["sway_stiffness_x_n_per_m"]} N/m, " +
                        $"y {sway["sway_stiffness_y_n_per_m"]} N/m, softest {sway["softest_direction"]}");
                }

                // The particle path reports per-body motion under different names; print them
                // only when they are there.
                if (result["max_body_displacement_m"] != null)
                {
                    RhinoApp.WriteLine(
                        $"worst body: {result["worst_body"]}, " +
                        $"max body displacement {Millimetres(result["max_body_displacement_m"])} mm, " +
                        $"rotation {result["max_body_rotation_deg"]} deg " +
                        $"(threshold {result["rotation_threshold_deg"]} deg)");
                }

                if (result["bodies"] is JArray movedBodies)
                {
                    var worst = movedBodies
                        .OfType<JObject>()
                        .OrderByDescending(b => b["displacement_m"]?.Value<double>() ?? 0.0)
                        .Take(5);
                    foreach (var body in worst)
                    {
                        RhinoApp.WriteLine(
                            $"  {body["g"]}: {body["displacement_m"]} m, " +
                            $"{body["rotation_deg"]} deg, joints {body["joints"]}");
                    }
                }

                if (result["unit_warnings"] is JArray multiBodyWarnings)
                {
                    foreach (var warning in multiBodyWarnings)
                    {
                        RhinoApp.WriteLine($"Unit warning: {warning}");
                    }
                }

                RhinoApp.WriteLine(
                    "Full report: get_stability_report over MCP, or evaluate_stability(detail=\"full\").");
            }
            else if (result["success"]?.Value<bool>() == true)
            {
                var stable = result["stable"]?.Value<bool>() == true ? "stable" : "unstable";
                RhinoApp.WriteLine($"EvaluateStability result: {stable}");
                RhinoApp.WriteLine(
                    $"max_displacement: {result["max_displacement"]} {result["document_length_unit"]} " +
                    $"({result["max_displacement_m"]} m)");
                // Rotation decides the verdict, so printing only the displacement leaves the
                // reader unable to see why a run with tiny displacement came back unstable.
                RhinoApp.WriteLine(
                    $"rotation: {result["rotation_deg"]} deg " +
                    $"(threshold {result["rotation_threshold_deg"]} deg), " +
                    $"motion_trend: {result["motion_trend"]}, " +
                    $"steps_run: {result["solver_steps_run"]}");
                var floorStrengthSource =
                    result["floor_strength_auto"]?.Value<bool>() == true ? "auto" : "explicit";
                var floorZSource =
                    result["floor_z_auto"]?.Value<bool>() == true ? "auto, lowest point" : "explicit";
                RhinoApp.WriteLine(
                    $"floor_z: {result["floor_z"]} {result["document_length_unit"]} ({floorZSource}), " +
                    $"rotation_trend: {result["rotation_trend"]}, " +
                    $"support_margin: {result["support_margin_m"]} m");
                var rigidStrengthSource =
                    result["rigid_strength_auto"]?.Value<bool>() == true ? "auto from floor" : "explicit";

                RhinoApp.WriteLine(
                    $"floor_strength: {result["floor_strength"]} ({floorStrengthSource}), " +
                    $"rigid_strength: {result["rigid_strength"]} ({rigidStrengthSource}), " +
                    $"total_mass: {result["total_mass_kg"]} kg");
                if (result["unit_warnings"] is JArray warnings)
                {
                    foreach (var warning in warnings)
                    {
                        RhinoApp.WriteLine($"Unit warning: {warning}");
                    }
                }
            }
            else
            {
                RhinoApp.WriteLine($"EvaluateStability failed: {result["message"]}");
                return Result.Failure;
            }

            return Result.Success;
        }

        /// <summary>A number in the result, rounded for the command line; blank when absent.</summary>
        private static string Metres(JToken token, int decimals)
        {
            var value = token?.Type is JTokenType.Float or JTokenType.Integer ? token.Value<double>() : double.NaN;
            return double.IsNaN(value) ? "-" : value.ToString($"F{decimals}");
        }

        private static string Millimetres(JToken token)
        {
            var value = token?.Type is JTokenType.Float or JTokenType.Integer ? token.Value<double>() : double.NaN;
            return double.IsNaN(value) ? "-" : (value * 1000.0).ToString("F2");
        }

    }
}
