using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Commands;
using RhinoMCPModPlugin.Functions;

namespace RhinoMCPModPlugin.Commands
{
    public class MCPAssignMissingMassCommand : Command
    {
        public MCPAssignMissingMassCommand()
        {
            Instance = this;
        }

        public static MCPAssignMissingMassCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodassignmissingmass";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var handler = new RhinoMCPModFunctions();
            var result = handler.AssignMissingMass(new JObject());

            if (result["success"]?.Value<bool>() == true)
            {
                RhinoApp.WriteLine(
                    $"AssignMissingMass completed successfully. Assigned {result["assigned"]?.Count()} objects " +
                    $"using {result["input_mass_unit"]}; stored internally as kg.");
                if (result["unit_warnings"] is JArray warnings)
                {
                    foreach (var warning in warnings)
                    {
                        RhinoApp.WriteLine($"Unit warning: {warning}");
                    }
                }
                return Result.Success;
            }

            RhinoApp.WriteLine($"AssignMissingMass failed: {result["message"]}");
            return Result.Failure;
        }
    }
}
