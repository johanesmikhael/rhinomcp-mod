using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Input;
using Rhino.Input.Custom;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject AssignMissingMass(JObject parameters)
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
            {
                throw new Exception("No active Rhino document.");
            }

            var usesDocumentGraph = parameters?["graph"] == null;
            var graphText = parameters?["graph"]?.ToString();
            if (string.IsNullOrWhiteSpace(graphText))
            {
                graphText = doc.Strings.GetValue(GraphKey);
                usesDocumentGraph = true;
            }

            if (string.IsNullOrWhiteSpace(graphText))
            {
                throw new Exception($"Connectivity graph not found in Rhino document: {GraphKey}");
            }

            var graph = JObject.Parse(graphText);
            var nodes = graph["n"] as JArray;
            if (nodes == null)
            {
                throw new Exception("Connectivity graph does not contain an 'n' array.");
            }

            var assigned = new JArray();
            var skippedExisting = new JArray();
            foreach (var nodeToken in nodes)
            {
                if (nodeToken is not JObject node)
                {
                    continue;
                }

                if (node["g"]?.ToString() is not string guidString ||
                    !Guid.TryParse(guidString, out var guid))
                {
                    continue;
                }

                var rhinoObject = doc.Objects.FindId(guid);
                if (rhinoObject == null)
                {
                    continue;
                }

                var mass = 0.0;
                var userText = rhinoObject.Attributes.GetUserString(StabilityKey);
                if (!string.IsNullOrWhiteSpace(userText))
                {
                    var data = JObject.Parse(userText);
                    if (data["mass"] != null)
                    {
                        mass = data["mass"].Value<double>();
                        node["mass"] = mass;
                    }
                }

                if (node["mass"] != null)
                {
                    mass = node["mass"].Value<double>();
                }

                if (mass > 0.0)
                {
                    skippedExisting.Add(guidString);
                    continue;
                }

                doc.Objects.UnselectAll();
                doc.Objects.Select(rhinoObject.Id);
                doc.Views.Redraw();

                var prompt = $"Assign missing mass for {rhinoObject.Name ?? guidString} (enter value or press Enter to skip)";
                var getNumber = new GetNumber();
                getNumber.SetCommandPrompt(prompt);
                getNumber.SetLowerLimit(0.0, true);
                getNumber.AcceptNothing(true);

                var result = getNumber.Get();
                double assignedMass;
                if (result == GetResult.Number)
                {
                    assignedMass = getNumber.Number();
                }
                else if (result == GetResult.Nothing)
                {
                    continue;
                }
                else if (result == GetResult.Cancel)
                {
                    break;
                }
                else
                {
                    continue;
                }

                var payload = new JObject { ["mass"] = assignedMass };
                rhinoObject.Attributes.SetUserString(
                    StabilityKey,
                    payload.ToString(Formatting.None));
                rhinoObject.CommitChanges();

                node["mass"] = assignedMass;
                assigned.Add(new JObject
                {
                    ["guid"] = guidString,
                    ["mass"] = assignedMass
                });
            }

            if (usesDocumentGraph && assigned.Count > 0)
            {
                doc.Strings.SetString(GraphKey, graph.ToString(Formatting.None));
            }

            return new JObject
            {
                ["success"] = true,
                ["assigned"] = assigned,
                ["skipped_existing"] = skippedExisting
            };
        }
        catch (Exception ex)
        {
            return new JObject
            {
                ["success"] = false,
                ["message"] = ex.Message
            };
        }
    }
}
