using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    private static bool TryGetObjectVolume(GeometryBase geometry, out double volume)
    {
        volume = 0.0;
        VolumeMassProperties properties = geometry switch
        {
            Brep brep => VolumeMassProperties.Compute(brep),
            Mesh mesh => VolumeMassProperties.Compute(mesh),
            Surface surface => VolumeMassProperties.Compute(surface),
            _ => null
        };

        using (properties)
        {
            if (properties == null ||
                !double.IsFinite(properties.Volume) ||
                Math.Abs(properties.Volume) <= RhinoMath.ZeroTolerance)
                return false;

            volume = Math.Abs(properties.Volume);
            return true;
        }
    }
    public JObject AssignMassFromLayerDensity(JObject parameters)
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                throw new InvalidOperationException("No active Rhino document.");

            var unitContext = StabilityUnits.Create(doc.ModelUnitSystem);
            var assigned = new JArray();
            var skippedLayers = new JArray();
            var skippedObjects = new JArray();
            var unitWarnings = new JArray();
            var volumeScaleToCubicMeters = Math.Pow(unitContext.LengthToMeters, 3.0);

            foreach (var layer in doc.Layers)
            {
                if (layer == null || layer.IsDeleted)
                    continue;

                var densityText = layer.GetUserString(LayerDensityKey);
                if (!double.TryParse(densityText, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var density) ||
                    !double.IsFinite(density) || density <= 0.0)
                {
                    skippedLayers.Add(new JObject
                    {
                        ["layer_id"] = layer.Id.ToString(),
                        ["layer_name"] = layer.FullPath,
                        ["reason"] = "Layer has no valid positive density."
                    });
                    continue;
                }

                var densityUnit = layer.GetUserString(LayerDensityUnitKey);
                if (string.IsNullOrWhiteSpace(densityUnit))
                {
                    densityUnit = StabilityUnits.InferLegacyDensityUnit(doc.ModelUnitSystem);
                    unitWarnings.Add(
                        $"Layer '{layer.FullPath}' has untagged legacy density; interpreted as {densityUnit}. Reassign density to store unit metadata.");
                }

                if (!StabilityUnits.TryDensityToKilogramsPerCubicMeter(
                        density, densityUnit, out var densityKilogramsPerCubicMeter))
                {
                    skippedLayers.Add(new JObject
                    {
                        ["layer_id"] = layer.Id.ToString(),
                        ["layer_name"] = layer.FullPath,
                        ["reason"] = $"Layer has unsupported density unit '{densityUnit}'."
                    });
                    continue;
                }

                var objects = doc.Objects.FindByLayer(layer);
                foreach (var rhinoObject in objects)
                {
                    if (rhinoObject?.Geometry == null)
                        continue;

                    if (!TryGetObjectVolume(rhinoObject.Geometry, out var volume))

                    {
                        skippedObjects.Add(new JObject
                        {
                            ["guid"] = rhinoObject.Id.ToString(),
                            ["layer_name"] = layer.FullPath,
                            ["reason"] = "Object has no valid non-zero volume."
                        });
                        continue;
                    }

                    var volumeCubicMeters = volume * volumeScaleToCubicMeters;
                    var massKilograms = densityKilogramsPerCubicMeter * volumeCubicMeters;
                    var payload = new JObject
                    {
                        ["mass"] = massKilograms,
                        ["mass_unit"] = StabilityUnits.KilogramUnit
                    };
                    rhinoObject.Attributes.SetUserString(
                        StabilityKey,
                        payload.ToString(Formatting.None));

                    if (!rhinoObject.CommitChanges())
                    {
                        skippedObjects.Add(new JObject
                        {
                            ["guid"] = rhinoObject.Id.ToString(),
                            ["layer_name"] = layer.FullPath,
                            ["reason"] = "Failed to commit object attributes."
                        });
                        continue;
                    }

                    assigned.Add(new JObject
                    {
                        ["guid"] = rhinoObject.Id.ToString(),
                        ["layer_name"] = layer.FullPath,
                        ["density"] = density,
                        ["density_unit"] = densityUnit,
                        ["density_kg_m3"] = densityKilogramsPerCubicMeter,
                        ["volume_model_units_cubed"] = volume,
                        ["volume_m3"] = volumeCubicMeters,
                        ["mass"] = massKilograms,
                        ["mass_unit"] = StabilityUnits.KilogramUnit
                    });
                }
            }

            doc.Views.Redraw();
            return new JObject
            {
                ["success"] = true,
                ["assigned"] = assigned,
                ["skipped_layers"] = skippedLayers,
                ["skipped_objects"] = skippedObjects,
                ["unit_warnings"] = unitWarnings,
                ["model_unit_system"] = doc.ModelUnitSystem.ToString(),
                ["document_length_to_meters"] = unitContext.LengthToMeters,
                ["volume_unit"] = "m³",
                ["mass_unit"] = StabilityUnits.KilogramUnit,
                ["volume_scale_to_m3"] = volumeScaleToCubicMeters
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
