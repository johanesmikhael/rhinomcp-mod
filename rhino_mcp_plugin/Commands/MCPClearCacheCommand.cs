using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input.Custom;

namespace RhinoMCPModPlugin.Commands
{
    public class MCPClearCacheCommand : Command
    {
        private const string PoseStorageKey = "rhinomcp.pose.v1";
        private const string PoseModeStorageKey = "rhinomcp.pose.mode.v1";
        private const string ObbStorageKey = "rhinomcp.obb.v1";

        public MCPClearCacheCommand()
        {
            Instance = this;
        }

        public static MCPClearCacheCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodclearcache";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var getOption = new GetOption();
            getOption.SetCommandPrompt("Clear RhinoMCP cached user strings");

            var allOption = getOption.AddOption("All");
            var selectedOption = getOption.AddOption("Selected");

            // Enter defaults to clearing all objects.
            getOption.AcceptNothing(true);
            var result = getOption.Get();
            if (result == Rhino.Input.GetResult.Cancel)
            {
                return Result.Cancel;
            }

            bool selectedOnly = false;
            if (result == Rhino.Input.GetResult.Option)
            {
                int chosen = getOption.Option()?.Index ?? -1;
                if (chosen == selectedOption)
                {
                    selectedOnly = true;
                }
                else if (chosen != allOption)
                {
                    return Result.Cancel;
                }
            }

            IEnumerable<RhinoObject> targets = selectedOnly
                ? doc.Objects.GetSelectedObjects(false, false)
                : doc.Objects.GetObjectList(new ObjectEnumeratorSettings
                {
                    NormalObjects = true,
                    LockedObjects = true,
                    HiddenObjects = true,
                    IncludeLights = true,
                    IncludeGrips = false,
                    DeletedObjects = false,
                    ReferenceObjects = false
                });

            int inspected = 0;
            int cleared = 0;
            int failed = 0;
            foreach (RhinoObject obj in targets)
            {
                if (obj == null)
                {
                    continue;
                }

                inspected++;
                bool hadPose = !string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(PoseStorageKey));
                bool hadPoseMode = !string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(PoseModeStorageKey));
                bool hadObb = !string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(ObbStorageKey));
                if (!hadPose && !hadPoseMode && !hadObb)
                {
                    continue;
                }

                obj.Attributes.DeleteUserString(PoseStorageKey);
                obj.Attributes.DeleteUserString(PoseModeStorageKey);
                obj.Attributes.DeleteUserString(ObbStorageKey);
                obj.CommitChanges();

                // CommitChanges returns false for a successful attribute write whenever
                // Rhino decides the change needs no new undo record, so counting on it here
                // reports cleared objects as failures. Confirm against the document.
                var after = doc.Objects.FindId(obj.Id)?.Attributes;
                var stillCached = after == null ||
                    !string.IsNullOrWhiteSpace(after.GetUserString(PoseStorageKey)) ||
                    !string.IsNullOrWhiteSpace(after.GetUserString(PoseModeStorageKey)) ||
                    !string.IsNullOrWhiteSpace(after.GetUserString(ObbStorageKey));
                if (stillCached)
                {
                    failed++;
                }
                else
                {
                    cleared++;
                }
            }

            if (selectedOnly && inspected == 0)
            {
                RhinoApp.WriteLine("mcpmodclearcache: no selected objects.");
                return Result.Nothing;
            }

            if (!selectedOnly)
            {
                MCPConnectivityGraphController.ClearStoredGraph(doc);
                RhinoApp.WriteLine("mcpmodclearcache: cleared stored connectivity graph.");
            }

            RhinoApp.WriteLine(
                $"mcpmodclearcache: cleared cache on {cleared} object(s)" +
                (failed > 0 ? $", failed {failed}" : string.Empty) +
                $", inspected {inspected}."
            );

            doc.Views.Redraw();
            return failed > 0 ? Result.Failure : Result.Success;
        }
    }
}
