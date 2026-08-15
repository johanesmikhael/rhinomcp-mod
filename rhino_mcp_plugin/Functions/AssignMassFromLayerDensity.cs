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

            var assigned = new JArray();
            var skippedLayers = new JArray();
            var skippedObjects = new JArray();
            var usesFeetDensity = doc.ModelUnitSystem == UnitSystem.Feet;
            var densityVolumeUnit = usesFeetDensity ? UnitSystem.Feet : UnitSystem.Meters;
            var densityUnit = usesFeetDensity ? "lb/ft³" : "kg/m³";
            var massUnit = usesFeetDensity ? "lb" : "kg";
            var volumeUnit = usesFeetDensity ? "ft³" : "m³";
            var linearScaleToDensityUnit = RhinoMath.UnitScale(
                doc.ModelUnitSystem,
                densityVolumeUnit);
            if (!double.IsFinite(linearScaleToDensityUnit) || linearScaleToDensityUnit <= 0.0)
                throw new InvalidOperationException(
                    $"Cannot convert model unit '{doc.ModelUnitSystem}' to {densityVolumeUnit}.");

            var volumeScaleToDensityUnit = Math.Pow(linearScaleToDensityUnit, 3.0);

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

                    var volumeInDensityUnit = volume * volumeScaleToDensityUnit;
                    var mass = density * volumeInDensityUnit;
                    var payload = new JObject { ["mass"] = mass };
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
                        ["volume_model_units_cubed"] = volume,
                        ["volume_in_density_unit"] = volumeInDensityUnit,
                        ["volume_unit"] = volumeUnit,
                        ["density_unit"] = densityUnit,
                        ["mass"] = mass,
                        ["mass_unit"] = massUnit
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
                ["model_unit_system"] = doc.ModelUnitSystem.ToString(),
                ["density_unit"] = densityUnit,
                ["volume_unit"] = volumeUnit,
                ["mass_unit"] = massUnit,
                ["linear_scale_to_density_unit"] = linearScaleToDensityUnit,
                ["volume_scale_to_density_unit"] = volumeScaleToDensityUnit
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
