using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Commands;
using RhinoMCPModPlugin.Functions;

namespace RhinoMCPModPlugin.Commands
{
    public class MCPAssignMassCommand : Command
    {
        public MCPAssignMassCommand()
        {
            Instance = this;
        }

        public static MCPAssignMassCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodassignmass";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var handler = new RhinoMCPModFunctions();
            var parameters = new JObject();
            var result = handler.AssignMass(parameters);

            if (result["success"]?.Value<bool>() == true)
            {
                RhinoApp.WriteLine(
                    $"AssignMass completed successfully. Assigned {result["assigned"]?.Count()} objects " +
                    $"using {result["input_mass_unit"]}; stored internally as kg.");
            }
            else
            {
                RhinoApp.WriteLine($"AssignMass failed: {result["message"]}");
                return Result.Failure;
            }

            return Result.Success;
        }
    }
}
