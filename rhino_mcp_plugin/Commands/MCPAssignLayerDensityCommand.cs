using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Commands;
using RhinoMCPModPlugin.Functions;

namespace RhinoMCPModPlugin.Commands
{
    public class MCPAssignLayerDensityCommand : Command
    {
        public MCPAssignLayerDensityCommand()
        {
            Instance = this;
        }

        public static MCPAssignLayerDensityCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodassignlayerdensity";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (!StabilityUnits.TryCreate(doc.ModelUnitSystem, out var unitContext, out var unitError))
            {
                RhinoApp.WriteLine($"Assign layer density failed: {unitError}");
                return Result.Failure;
            }

            var densityUnit = unitContext.DensityInputUnit;
            RhinoApp.WriteLine(
                $"Current Rhino model unit: {doc.ModelUnitSystem}. Density unit: {densityUnit}.");

            var handler = new RhinoMCPModFunctions();
            var result = handler.AssignLayerDensity(new JObject());

            if (result["success"]?.Value<bool>() != true)
            {
                RhinoApp.WriteLine($"Assign layer density failed: {result["message"]}");
                return Result.Failure;
            }

            var assignedCount = result["assigned"] is JArray assigned ? assigned.Count : 0;
            var skippedCount = result["skipped"] is JArray skipped ? skipped.Count : 0;
            RhinoApp.WriteLine(
                $"Layer density finished. Updated {assignedCount} layer(s), skipped {skippedCount} layer(s).");
            if (result["unit_warnings"] is JArray warnings)
            {
                foreach (var warning in warnings)
                {
                    RhinoApp.WriteLine($"Unit warning: {warning}");
                }
            }

            return result["cancelled"]?.Value<bool>() == true ? Result.Cancel : Result.Success;
        }
    }
}
