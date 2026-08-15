using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Input;
using Rhino.Input.Custom;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public const string LayerDensityKey = "density";

    public static string GetDensityUnit(UnitSystem modelUnitSystem)
    {
        return modelUnitSystem == UnitSystem.Feet ? "lb/ft³" : "kg/m³";
    }

    public JObject AssignLayerDensity(JObject parameters)
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                throw new InvalidOperationException("No active Rhino document.");

            var densityUnit = GetDensityUnit(doc.ModelUnitSystem);

            var assigned = new JArray();
            var skipped = new JArray();
            var cancelled = false;

            foreach (var layer in doc.Layers)
            {
                if (layer == null || layer.IsDeleted)
                    continue;

                var getNumber = new GetNumber();
                getNumber.SetCommandPrompt(
                    $"Density for layer/material '{layer.FullPath}' in {densityUnit} (Enter to skip, Esc to stop)");
                getNumber.SetLowerLimit(0.0, true);
                getNumber.AcceptNothing(true);

                var existingText = layer.GetUserString(LayerDensityKey);
                if (double.TryParse(existingText, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var existingDensity))
                {
                    getNumber.SetDefaultNumber(existingDensity);
                }

                var getResult = getNumber.Get();
                if (getResult == GetResult.Cancel)
                {
                    cancelled = true;
                    break;
                }

                if (getResult != GetResult.Number)
                {
                    skipped.Add(layer.FullPath);
                    continue;
                }

                var density = getNumber.Number();
                layer.SetUserString(
                    LayerDensityKey,
                    density.ToString("R", CultureInfo.InvariantCulture));

                if (!doc.Layers.Modify(layer, layer.Index, true))
                {
                    skipped.Add(layer.FullPath);
                    continue;
                }

                assigned.Add(new JObject
                {
                    ["layer_id"] = layer.Id.ToString(),
                    ["layer_name"] = layer.FullPath,
                    [LayerDensityKey] = density
                });
            }

            doc.Views.Redraw();
            return new JObject
            {
                ["success"] = true,
                ["cancelled"] = cancelled,
                ["assigned"] = assigned,
                ["skipped"] = skipped
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
