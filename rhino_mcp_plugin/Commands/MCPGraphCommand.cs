using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
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
            if (doc == null)
            {
                RhinoApp.WriteLine("No active Rhino document.");
                return Result.Failure;
            }

            // Objects selected before the command are picked up automatically; otherwise
            // this prompts for them. Either way the resulting set is pinned, so the graph
            // stays on screen after the selection is cleared.
            var getObject = new GetObject();
            getObject.SetCommandPrompt(
                "Select objects to graph (Enter = whole document)");
            var allOption = getObject.AddOption("All");
            var offOption = getObject.AddOption("Off");
            getObject.EnablePreSelect(true, true);
            getObject.EnablePostSelect(true);
            getObject.SubObjectSelect = false;
            getObject.GroupSelect = true;
            getObject.AcceptNothing(true);

            var result = getObject.GetMultiple(1, 0);

            if (result == GetResult.Option)
            {
                var index = getObject.Option()?.Index ?? -1;
                if (index == offOption)
                {
                    MCPConnectivityGraphController.SetEnabled(false);
                    return Result.Success;
                }

                if (index == allOption)
                {
                    PinAndShow(doc, null, "whole document");
                    return Result.Success;
                }

                return Result.Cancel;
            }

            if (result == GetResult.Nothing)
            {
                PinAndShow(doc, null, "whole document");
                return Result.Success;
            }

            if (result != GetResult.Object)
            {
                return Result.Cancel;
            }

            var ids = new HashSet<Guid>();
            for (var i = 0; i < getObject.ObjectCount; i++)
            {
                var objRef = getObject.Object(i);
                if (objRef?.ObjectId != null && objRef.ObjectId != Guid.Empty)
                {
                    ids.Add(objRef.ObjectId);
                }
            }

            if (ids.Count == 0)
            {
                PinAndShow(doc, null, "whole document");
                return Result.Success;
            }

            PinAndShow(doc, new GraphScope { Ids = ids }, $"{ids.Count} selected objects");
            return Result.Success;
        }

        private static void PinAndShow(RhinoDoc doc, GraphScope scope, string label)
        {
            MCPConnectivityGraphController.PinnedScope = scope;

            if (MCPConnectivityGraphController.IsEnabled)
            {
                doc.Views.Redraw();
            }
            else
            {
                MCPConnectivityGraphController.SetEnabled(true);
            }

            RhinoApp.WriteLine($"MCP graph scope pinned to {label}.");
        }
    }
}
