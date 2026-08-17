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

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (!StabilityUnits.TryCreate(doc.ModelUnitSystem, out var unitContext, out var unitError))
            {
                RhinoApp.WriteLine($"EvaluateStability failed: {unitError}");
                return Result.Failure;
            }

            var handler = new RhinoMCPModFunctions();
            var parameters = new JObject();
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
                parameters["rigid_strength"] = DefaultRigidStrength;
                parameters["floor_strength"] = DefaultFloorStrength;
                parameters["floor_z"] = DefaultFloorZ;
                parameters["gravity"] = DefaultGravity;
                parameters["assign_tol"] = defaultAssignTolerance;
                parameters["threshold"] = defaultSolverThreshold;
                parameters["solver_substeps"] = DefaultSolverSubsteps;
            }
            else if (selectedMode == customOption)
            {
                var parameterLabels = new[]
                {
                    "Current step",
                    $"Stability threshold ({doc.ModelUnitSystem})",
                    "Rigid strength",
                    "Floor strength",
                    $"Floor Z ({doc.ModelUnitSystem})",
                    "Gravity (m/s²)",
                    $"Assign tolerance ({doc.ModelUnitSystem})",
                    $"Displacement threshold ({doc.ModelUnitSystem})",
                    "Solver substeps"
                };

                var values = new double[9]
                {
                    DefaultCurrentStep,
                    defaultStabilityThreshold,
                    DefaultRigidStrength,
                    DefaultFloorStrength,
                    DefaultFloorZ,
                    DefaultGravity,
                    defaultAssignTolerance,
                    defaultSolverThreshold,
                    DefaultSolverSubsteps
                };

                for (var i = 0; i < parameterLabels.Length; i++)
                {
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
                parameters["rigid_strength"] = values[2];
                parameters["floor_strength"] = values[3];
                parameters["floor_z"] = values[4];
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
