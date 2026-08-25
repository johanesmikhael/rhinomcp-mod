using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoMCPModPlugin.Functions;

namespace RhinoMCPModPlugin.Commands
{
    /// <summary>
    /// States what the connections in a model are, from Rhino rather than over MCP.
    /// </summary>
    /// <remarks>
    /// The rule table was reachable only through the MCP tool, which means it could not be
    /// written by the person drawing the model. Connection type is domain knowledge - a
    /// screwed panel and a dry-stacked one look identical to an intersection test - so the
    /// person who knows it is the one at the keyboard.
    ///
    /// Selection first, type second, because that is the order the knowledge arrives in: you
    /// point at the beams and the columns, then say what happens where they meet. Layers are
    /// offered before objects since a rule about two element classes is what an engineer
    /// states; naming individual objects is the exception found after reading a report.
    /// </remarks>
    public class MCPAssignJointTypeCommand : Command
    {
        public MCPAssignJointTypeCommand()
        {
            Instance = this;
        }

        public static MCPAssignJointTypeCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodassignjointtype";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var handler = new RhinoMCPModFunctions();

            var listed = handler.AssignJointType(new JObject());
            var ruleCount = (listed["rules"] as JArray)?.Count ?? 0;
            var stale = listed["stale_rules"]?.Value<int>() ?? 0;
            RhinoApp.WriteLine(
                ruleCount == 0
                    ? "No joint type rules in this document."
                    : $"{ruleCount} joint type rules in this document" +
                      (stale > 0 ? $", {stale} of them stale." : "."));

            var getFirst = new GetObject();
            getFirst.SetCommandPrompt("Select elements on one side of the joint");
            var listOption = getFirst.AddOption("List");
            var pruneOption = getFirst.AddOption("Prune");
            getFirst.EnablePreSelect(true, true);
            getFirst.EnablePostSelect(true);
            getFirst.SubObjectSelect = false;
            getFirst.GroupSelect = true;

            while (true)
            {
                var picked = getFirst.GetMultiple(1, 0);

                if (picked == GetResult.Option)
                {
                    var index = getFirst.Option()?.Index ?? -1;
                    if (index == listOption)
                    {
                        Report(listed);
                        return Result.Success;
                    }

                    if (index == pruneOption)
                    {
                        var pruned = handler.AssignJointType(new JObject { ["prune"] = true });
                        RhinoApp.WriteLine(
                            $"Pruned {(pruned["removed"] as JArray)?.Count ?? 0} rules that " +
                            "could no longer match.");
                        return Result.Success;
                    }

                    continue;
                }

                if (picked != GetResult.Object)
                {
                    return Result.Cancel;
                }

                break;
            }

            var first = getFirst.Objects().Select(o => o.Object()).Where(o => o != null).ToList();
            if (first.Count == 0)
            {
                return Result.Cancel;
            }

            // The other side is optional. Given, this is a rule about the joints BETWEEN two
            // classes; left out, it is what these elements say about all of their own joints,
            // which is the weaker statement and loses to any pair rule.
            var getSecond = new GetObject();
            getSecond.SetCommandPrompt(
                "Select elements on the other side (Enter for a rule about the first set alone)");
            getSecond.EnablePreSelect(false, true);
            getSecond.EnablePostSelect(true);
            getSecond.SubObjectSelect = false;
            getSecond.GroupSelect = true;
            getSecond.AcceptNothing(true);
            getSecond.DeselectAllBeforePostSelect = true;

            var secondResult = getSecond.GetMultiple(1, 0);
            if (secondResult == GetResult.Cancel)
            {
                return Result.Cancel;
            }

            var second = secondResult == GetResult.Object
                ? getSecond.Objects().Select(o => o.Object()).Where(o => o != null).ToList()
                : new List<RhinoObject>();

            var getScope = new GetOption();
            getScope.SetCommandPrompt("Write the rule about");
            var layerOption = getScope.AddOption("Layers");
            var objectOption = getScope.AddOption("Objects");
            getScope.AcceptNothing(true);
            if (getScope.Get() == GetResult.Cancel)
            {
                return Result.Cancel;
            }

            var byLayer = (getScope.Option()?.Index ?? layerOption) != objectOption;

            var getType = new GetOption();
            getType.SetCommandPrompt("Joint type");
            // Contact first, because it is what geometry alone can honestly claim.
            var contactOption = getType.AddOption("Contact");
            var pinOption = getType.AddOption("Pin");
            var fixedOption = getType.AddOption("Fixed");
            var clearOption = getType.AddOption("Clear");
            getType.AcceptNothing(true);
            if (getType.Get() == GetResult.Cancel)
            {
                return Result.Cancel;
            }

            var typeIndex = getType.Option()?.Index ?? contactOption;
            var clearing = typeIndex == clearOption;
            var jointType = typeIndex == pinOption
                ? "pin"
                : typeIndex == fixedOption ? "fixed" : "contact";

            var parameters = new JObject();
            if (clearing)
            {
                parameters["clear"] = true;
            }
            else
            {
                parameters["joint_type"] = jointType;
            }

            if (byLayer)
            {
                parameters["layer"] = new JArray(LayersOf(doc, first).Cast<object>().ToArray());
                if (second.Count > 0)
                {
                    parameters["with_layer"] =
                        new JArray(LayersOf(doc, second).Cast<object>().ToArray());
                }
            }
            else
            {
                parameters["ids"] = new JArray(
                    first.Select(o => (object)o.Id.ToString()).ToArray());
                if (second.Count > 0)
                {
                    parameters["with_ids"] = new JArray(
                        second.Select(o => (object)o.Id.ToString()).ToArray());
                }
            }

            var result = handler.AssignJointType(parameters);
            if (result["success"]?.Value<bool>() != true)
            {
                RhinoApp.WriteLine($"AssignJointType failed: {result["message"]}");
                return Result.Failure;
            }

            var sides = second.Count > 0
                ? $"{Describe(doc, first, byLayer)} to {Describe(doc, second, byLayer)}"
                : Describe(doc, first, byLayer);
            RhinoApp.WriteLine(
                clearing
                    ? $"Cleared the rule for {sides}."
                    : $"{sides} is {jointType}.");
            Report(handler.AssignJointType(new JObject()));

            return Result.Success;
        }

        /// <summary>The distinct layers the given objects are on, by name.</summary>
        private static List<string> LayersOf(RhinoDoc doc, IEnumerable<RhinoObject> objects)
        {
            var names = new List<string>();
            foreach (var obj in objects)
            {
                var layer = doc.Layers.FindIndex(obj.Attributes.LayerIndex);
                if (layer == null)
                {
                    continue;
                }

                // The leaf name, because that is what resolution matches on - a joint reports
                // its layer as Layer.Name and rules are checked against that. Storing the full
                // path here would write rules that can never fire. It also means two layers
                // with the same leaf name under different parents are one class to a rule.
                var name = layer.Name;
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        private static string Describe(RhinoDoc doc, List<RhinoObject> objects, bool byLayer)
        {
            return byLayer
                ? string.Join("/", LayersOf(doc, objects))
                : $"{objects.Count} object" + (objects.Count == 1 ? "" : "s");
        }

        private static void Report(JObject listed)
        {
            var rules = listed["rules"] as JArray;
            if (rules == null || rules.Count == 0)
            {
                RhinoApp.WriteLine("No joint type rules in this document.");
                return;
            }

            foreach (var rule in rules)
            {
                var staleNote = rule["stale"] != null && rule["stale"].Type != JTokenType.Null
                    ? $"   [stale: {rule["stale"]}]"
                    : "";
                RhinoApp.WriteLine(
                    $"  {rule["a"]} to {rule["b"]}: {rule["joint_type"]}{staleNote}");
            }
        }
    }
}
