using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;

namespace RhinoMCPModPlugin.Commands
{
    public class MCPOBBCommand : Command
    {
        public MCPOBBCommand()
        {
            Instance = this;
        }

        public static MCPOBBCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodobb";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var getOption = new GetOption();
            getOption.SetCommandPrompt("MCP OBB display");

            var onOption = getOption.AddOption("On");
            var offOption = getOption.AddOption("Off");
            var toggleOption = getOption.AddOption("Toggle");
            var statusOption = getOption.AddOption("Status");

            getOption.AcceptNothing(true);
            var result = getOption.Get();

            if (result == Rhino.Input.GetResult.Nothing)
            {
                MCPOBBController.Toggle();
                return Result.Success;
            }

            if (result != Rhino.Input.GetResult.Option)
            {
                return Result.Cancel;
            }

            var selected = getOption.Option()?.Index ?? -1;
            if (selected == onOption)
            {
                MCPOBBController.SetEnabled(true);
            }
            else if (selected == offOption)
            {
                MCPOBBController.SetEnabled(false);
            }
            else if (selected == toggleOption)
            {
                MCPOBBController.Toggle();
            }
            else if (selected == statusOption)
            {
                RhinoApp.WriteLine($"MCP OBB is {(MCPOBBController.IsEnabled ? "ON" : "OFF")}.");
            }

            return Result.Success;
        }
    }
}
