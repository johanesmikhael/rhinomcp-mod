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
    public const string LayerDensityUnitKey = "density_unit";

    public static string GetDensityUnit(UnitSystem modelUnitSystem)
    {
        return StabilityUnits.PreferredDensityInputUnit(modelUnitSystem);
    }

    public JObject AssignLayerDensity(JObject parameters)
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                throw new InvalidOperationException("No active Rhino document.");

            var unitContext = StabilityUnits.Create(doc.ModelUnitSystem);
            var densityUnit = unitContext.DensityInputUnit;

            var assigned = new JArray();
            var skipped = new JArray();
            var unitWarnings = new JArray();
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
                    var existingUnit = layer.GetUserString(LayerDensityUnitKey);
                    if (string.IsNullOrWhiteSpace(existingUnit))
                    {
                        existingUnit = StabilityUnits.InferLegacyDensityUnit(doc.ModelUnitSystem);
                        unitWarnings.Add(
                            $"Layer '{layer.FullPath}' has untagged legacy density; interpreted as {existingUnit}.");
                    }

                    if (StabilityUnits.TryDensityToKilogramsPerCubicMeter(
                            existingDensity, existingUnit, out var existingDensitySi))
                    {
                        getNumber.SetDefaultNumber(
                            StabilityUnits.KilogramsPerCubicMeterToInputDensity(
                                existingDensitySi, densityUnit));
                    }
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
                layer.SetUserString(LayerDensityUnitKey, densityUnit);

                if (!doc.Layers.Modify(layer, layer.Index, true))
                {
                    skipped.Add(layer.FullPath);
                    continue;
                }

                assigned.Add(new JObject
                {
                    ["layer_id"] = layer.Id.ToString(),
                    ["layer_name"] = layer.FullPath,
                    [LayerDensityKey] = density,
                    [LayerDensityUnitKey] = densityUnit
                });
            }

            doc.Views.Redraw();
            return new JObject
            {
                ["success"] = true,
                ["cancelled"] = cancelled,
                ["assigned"] = assigned,
                ["skipped"] = skipped,
                ["unit_warnings"] = unitWarnings,
                ["density_unit"] = densityUnit
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
