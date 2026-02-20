using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;

namespace RhinoMCPModPlugin.Commands
{
    public class MCPGraphCommand : Command
    {
        public MCPGraphCommand()
        {
            Instance = this;
        }

        public static MCPGraphCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodgraph";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var getOption = new GetOption();
            getOption.SetCommandPrompt("MCP connectivity graph display");

            var onOption = getOption.AddOption("On");
            var offOption = getOption.AddOption("Off");
            var toggleOption = getOption.AddOption("Toggle");
            var statusOption = getOption.AddOption("Status");

            // Enter defaults to toggle for fast usage: `mcpmodgraph`.
            getOption.AcceptNothing(true);
            var result = getOption.Get();

            if (result == Rhino.Input.GetResult.Nothing)
            {
                MCPConnectivityGraphController.Toggle();
                return Result.Success;
            }

            if (result != Rhino.Input.GetResult.Option)
            {
                return Result.Cancel;
            }

            var selected = getOption.Option()?.Index ?? -1;
            if (selected == onOption)
            {
                MCPConnectivityGraphController.SetEnabled(true);
            }
            else if (selected == offOption)
            {
                MCPConnectivityGraphController.SetEnabled(false);
            }
            else if (selected == toggleOption)
            {
                MCPConnectivityGraphController.Toggle();
            }
            else if (selected == statusOption)
            {
                RhinoApp.WriteLine($"MCP graph is {(MCPConnectivityGraphController.IsEnabled ? "ON" : "OFF")}.");
            }

            return Result.Success;
        }
    }
}
