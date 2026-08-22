using Rhino;
using Rhino.Commands;

namespace RhinoMCPModPlugin.Commands
{
    /// <summary>
    /// Toggles the contact-extent overlay without prompting for anything.
    /// </summary>
    /// <remarks>
    /// The same toggle lives on mcpmodgraph's Extent option, but that command prompts for
    /// objects, and a command that prompts cannot be driven from a script or over MCP - the
    /// call blocks on the prompt and the caller times out. Checking a geometric reduction by
    /// eye means building a scene, turning the overlay on and capturing a view, all without a
    /// human at the keyboard, so the toggle needs a form that never asks a question.
    /// </remarks>
    public class MCPGraphExtentCommand : Command
    {
        public MCPGraphExtentCommand()
        {
            Instance = this;
        }

        public static MCPGraphExtentCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodgraphextent";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            MCPConnectivityGraphController.ShowContactExtent =
                !MCPConnectivityGraphController.ShowContactExtent;

            // Showing the regions is pointless with the graph itself switched off, so turning
            // them on turns it on. Turning them off leaves the graph alone.
            if (MCPConnectivityGraphController.ShowContactExtent &&
                !MCPConnectivityGraphController.IsEnabled)
            {
                MCPConnectivityGraphController.SetEnabled(true);
            }

            doc?.Views.Redraw();
            RhinoApp.WriteLine(
                "MCP contact extent " +
                (MCPConnectivityGraphController.ShowContactExtent ? "shown." : "hidden."));
            return Result.Success;
        }
    }
}
