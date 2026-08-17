using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoMCPModPlugin;

namespace RhinoMCPModPlugin.Commands
{
    public class MCPStabilityDisplayCommand : Command
    {
        public MCPStabilityDisplayCommand()
        {
            Instance = this;
        }

        public static MCPStabilityDisplayCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodstabilitydisplay";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var getOption = new GetOption();
            getOption.SetCommandPrompt("MCP stability geometry cache display");
            var onOption = getOption.AddOption("On");
            var offOption = getOption.AddOption("Off");

            var result = getOption.Get();
            if (result == GetResult.Cancel)
            {
                return Result.Cancel;
            }

            var selected = getOption.Option()?.Index ?? -1;
            if (selected == onOption)
            {
                MCPStabilityController.SetEnabled(true);
            }
            else if (selected == offOption)
            {
                MCPStabilityController.SetEnabled(false);
            }
            else
            {
                return Result.Cancel;
            }

            RhinoApp.WriteLine($"MCP stability geometry cache display is {(MCPStabilityController.IsEnabled ? "ON" : "OFF")}.");
            return Result.Success;
        }
    }
}
