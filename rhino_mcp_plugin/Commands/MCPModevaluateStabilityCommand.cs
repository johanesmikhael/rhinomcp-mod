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
                    var pinned = MCPConnectivityGraphController.PinnedScope;
                    if (pinned?.Ids == null || pinned.Ids.Count == 0)
                    {
                        RhinoApp.WriteLine(
                            "No pinned graph scope; run mcpmodgraph with a selection first.");
                        return false;
                    }

                    parameters["ids"] = new JArray(pinned.Ids.Select(id => (object)id.ToString()).ToArray());
                    label = $"pinned graph scope ({pinned.Ids.Count} objects)";
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

            if (optionResult == GetResult.Nothing || selectedMode == defaultsOption)
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
                    "Floor strength (0 = auto from mass)",
                    $"Floor Z ({doc.ModelUnitSystem})",  // skipped when the floor is auto
                    "Gravity (m/s²)",
                    $"Assign tolerance ({doc.ModelUnitSystem})",
                    $"Displacement threshold ({doc.ModelUnitSystem})",
                    "Solver substeps"
                };

                var values = new double[9]
                {
                    DefaultCurrentStep,
                    defaultStabilityThreshold,
                    0.0,
                    0.0,
                    DefaultFloorZ,
                    DefaultGravity,
                    defaultAssignTolerance,
                    defaultSolverThreshold,
                    DefaultSolverSubsteps
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

            if (result["success"]?.Value<bool>() == true)
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
    }
}
