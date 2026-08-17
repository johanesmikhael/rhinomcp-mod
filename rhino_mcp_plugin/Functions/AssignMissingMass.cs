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
            var unitWarnings = new JArray();
            var inputMassUnit = StabilityUnits.PreferredMassInputUnit(doc.ModelUnitSystem);
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

                JObject massSource = null;
                var userText = rhinoObject.Attributes.GetUserString(StabilityKey);
                if (!string.IsNullOrWhiteSpace(userText))
                {
                    var data = JObject.Parse(userText);
                    if (data["mass"] != null)
                    {
                        massSource = data;
                    }
                }

                if (massSource == null && node["mass"] != null)
                {
                    massSource = node;
                }

                if (massSource != null)
                {
                    var storedMass = massSource["mass"]?.Value<double>() ?? 0.0;
                    var storedUnit = massSource["mass_unit"]?.ToString();
                    if (string.IsNullOrWhiteSpace(storedUnit))
                    {
                        storedUnit = StabilityUnits.InferLegacyMassUnit(doc.ModelUnitSystem);
                        unitWarnings.Add(
                            $"Object {guidString} has untagged legacy mass; interpreted as {storedUnit}. Reassign mass to store canonical kg metadata.");
                    }

                    if (!StabilityUnits.TryMassToKilograms(storedMass, storedUnit, out var existingMassKg))
                    {
                        throw new InvalidOperationException(
                            $"Object {guidString} has invalid mass or unsupported mass_unit '{storedUnit}'.");
                    }

                    node["mass"] = existingMassKg;
                    node["mass_unit"] = StabilityUnits.KilogramUnit;
                    skippedExisting.Add(guidString);
                    continue;
                }

                doc.Objects.UnselectAll();
                doc.Objects.Select(rhinoObject.Id);
                doc.Views.Redraw();

                var prompt =
                    $"Assign missing mass for {rhinoObject.Name ?? guidString} in {inputMassUnit} " +
                    "(enter value or press Enter to skip)";
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

                if (!StabilityUnits.TryMassToKilograms(
                        assignedMass, inputMassUnit, out var assignedMassKilograms))
                {
                    throw new InvalidOperationException(
                        $"Mass for {rhinoObject.Name ?? guidString} could not be converted from {inputMassUnit} to kg.");
                }

                var payload = new JObject
                {
                    ["mass"] = assignedMassKilograms,
                    ["mass_unit"] = StabilityUnits.KilogramUnit
                };
                rhinoObject.Attributes.SetUserString(
                    StabilityKey,
                    payload.ToString(Formatting.None));
                rhinoObject.CommitChanges();

                node["mass"] = assignedMassKilograms;
                node["mass_unit"] = StabilityUnits.KilogramUnit;
                assigned.Add(new JObject
                {
                    ["guid"] = guidString,
                    ["entered_mass"] = assignedMass,
                    ["entered_mass_unit"] = inputMassUnit,
                    ["mass"] = assignedMassKilograms,
                    ["mass_unit"] = StabilityUnits.KilogramUnit
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
                ["skipped_existing"] = skippedExisting,
                ["unit_warnings"] = unitWarnings,
                ["input_mass_unit"] = inputMassUnit,
                ["stored_mass_unit"] = StabilityUnits.KilogramUnit
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
