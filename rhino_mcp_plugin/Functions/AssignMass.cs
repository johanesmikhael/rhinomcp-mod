using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject AssignMass(JObject parameters)
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
            {
                throw new Exception("No active Rhino document.");
            }

            var graphText = parameters?["graph"]?.ToString();
            if (string.IsNullOrWhiteSpace(graphText))
            {
                graphText = doc.Strings.GetValue(GraphKey);
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
            var inputMassUnit = StabilityUnits.PreferredMassInputUnit(doc.ModelUnitSystem);
            foreach (var nodeToken in nodes)
            {
                if (nodeToken is not JObject node)
                {
                    continue;
                }

                if (node["g"]?.ToString() is not string guidString || !Guid.TryParse(guidString, out var guid))
                {
                    continue;
                }

                var rhinoObject = doc.Objects.FindId(guid);
                if (rhinoObject == null)
                {
                    continue;
                }

                doc.Objects.UnselectAll();
                doc.Objects.Select(rhinoObject.Id);
                doc.Views.Redraw();

                var prompt =
                    $"Assign mass for {rhinoObject.Name ?? guidString} in {inputMassUnit} " +
                    "(enter value or press Enter to skip)";
                var getNumber = new GetNumber();
                getNumber.SetCommandPrompt(prompt);
                getNumber.SetLowerLimit(0.0, true);
                getNumber.AcceptNothing(true);

                var result = getNumber.Get();
                double mass = 0.0;
                bool shouldSkip = false;
                if (result == GetResult.Number)
                {
                    mass = getNumber.Number();
                }
                else if (result == GetResult.Nothing)
                {
                    shouldSkip = true;
                }
                else if (result == GetResult.Cancel)
                {
                    break;
                }

                if (shouldSkip)
                {
                    continue;
                }

                if (!StabilityUnits.TryMassToKilograms(mass, inputMassUnit, out var massKilograms))
                {
                    throw new InvalidOperationException(
                        $"Mass for {rhinoObject.Name ?? guidString} could not be converted from {inputMassUnit} to kg.");
                }

                var payload = new JObject
                {
                    ["mass"] = massKilograms,
                    ["mass_unit"] = StabilityUnits.KilogramUnit
                };
                rhinoObject.Attributes.SetUserString(StabilityKey, payload.ToString(Formatting.None));
                rhinoObject.CommitChanges();

                node["mass"] = massKilograms;
                node["mass_unit"] = StabilityUnits.KilogramUnit;
                assigned.Add(new JObject
                {
                    ["guid"] = guidString,
                    ["entered_mass"] = mass,
                    ["entered_mass_unit"] = inputMassUnit,
                    ["mass"] = massKilograms,
                    ["mass_unit"] = StabilityUnits.KilogramUnit
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["assigned"] = assigned,
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
