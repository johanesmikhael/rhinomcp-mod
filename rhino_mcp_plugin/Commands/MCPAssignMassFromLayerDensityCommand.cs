using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Commands;
using RhinoMCPModPlugin.Functions;

namespace RhinoMCPModPlugin.Commands
{
    public class MCPAssignMassFromLayerDensityCommand : Command
    {
        public MCPAssignMassFromLayerDensityCommand()
        {
            Instance = this;
        }

        public static MCPAssignMassFromLayerDensityCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodmassfromlayerdensity";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (!StabilityUnits.TryCreate(doc.ModelUnitSystem, out var unitContext, out var unitError))
            {
                RhinoApp.WriteLine($"Assign mass from layer density failed: {unitError}");
                return Result.Failure;
            }

            var densityUnit = unitContext.DensityInputUnit;
            RhinoApp.WriteLine(
                $"Density unit is {densityUnit}. Current Rhino model unit: {doc.ModelUnitSystem}. " +
                "Object volumes and density will be normalized before storing mass in kg.");

            var handler = new RhinoMCPModFunctions();
            var result = handler.AssignMassFromLayerDensity(new JObject());

            if (result["success"]?.Value<bool>() != true)
            {
                RhinoApp.WriteLine($"Assign mass from layer density failed: {result["message"]}");
                return Result.Failure;
            }

            var assignedCount = result["assigned"] is JArray assigned ? assigned.Count : 0;
            var skippedLayerCount = result["skipped_layers"] is JArray layers ? layers.Count : 0;
            var skippedObjectCount = result["skipped_objects"] is JArray objects ? objects.Count : 0;

            RhinoApp.WriteLine(
                $"Mass calculation finished. Updated {assignedCount} object(s); " +
                $"skipped {skippedLayerCount} layer(s) and {skippedObjectCount} object(s). " +
                $"Model units: {result["model_unit_system"]}.");
            if (result["unit_warnings"] is JArray warnings)
            {
                foreach (var warning in warnings)
                {
                    RhinoApp.WriteLine($"Unit warning: {warning}");
                }
            }

            return Result.Success;
        }
    }
}
