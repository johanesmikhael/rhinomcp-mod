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

        /// <summary>
        /// The settled shape the stability preview draws from.
        /// </summary>
        /// <remarks>
        /// Cleared here because it is a cache like the others and lives in the same place, on
        /// the objects. Leaving it out meant the command reported the cache cleared while the
        /// preview carried on drawing the result of an evaluation that no longer existed.
        /// </remarks>
        private const string AfterEvaluationKey = Functions.RhinoMCPModFunctions.AfterEvaluationKey;

        public MCPClearCacheCommand()
        {
            Instance = this;
        }

        public static MCPClearCacheCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodclearcache";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // Run from a script the command must not ask anything: a prompt has nobody to
            // answer it, so the handler never returns and the commands sent afterwards are
            // swallowed by the prompt still waiting - the caller is told the clear succeeded,
            // nothing is cleared, and the next few commands vanish into the same prompt.
            //
            // Over MCP this needs the dash form, "-mcpmodclearcache": RhinoApp.RunScript runs
            // a command interactively unless the name is dashed, so the undashed name prompts
            // even though nobody is there. Clearing everything is the only sensible scripted
            // reading anyway - "which objects" is a question, and there is no one to ask.
            if (mode == RunMode.Scripted)
            {
                return Clear(doc, selectedOnly: false);
            }

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

            return Clear(doc, selectedOnly);
        }

        private static Result Clear(RhinoDoc doc, bool selectedOnly)
        {
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
                bool hadEva = !string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(AfterEvaluationKey));
                if (!hadPose && !hadPoseMode && !hadObb && !hadEva)
                {
                    continue;
                }

                obj.Attributes.DeleteUserString(PoseStorageKey);
                obj.Attributes.DeleteUserString(PoseModeStorageKey);
                obj.Attributes.DeleteUserString(ObbStorageKey);
                obj.Attributes.DeleteUserString(AfterEvaluationKey);
                obj.CommitChanges();

                // CommitChanges returns false for a successful attribute write whenever
                // Rhino decides the change needs no new undo record, so counting on it here
                // reports cleared objects as failures. Confirm against the document.
                var after = doc.Objects.FindId(obj.Id)?.Attributes;
                var stillCached = after == null ||
                    !string.IsNullOrWhiteSpace(after.GetUserString(PoseStorageKey)) ||
                    !string.IsNullOrWhiteSpace(after.GetUserString(PoseModeStorageKey)) ||
                    !string.IsNullOrWhiteSpace(after.GetUserString(ObbStorageKey)) ||
                    !string.IsNullOrWhiteSpace(after.GetUserString(AfterEvaluationKey));
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
