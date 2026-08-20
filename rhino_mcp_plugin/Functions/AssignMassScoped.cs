using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    /// <summary>
    /// Assign mass to a scoped set of objects without prompting. Either a density
    /// is given and each object's mass follows from its own volume, or one mass is
    /// given and every object in the scope carries it.
    /// </summary>
    public JObject AssignMassScoped(JObject parameters)
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                throw new InvalidOperationException("No active Rhino document.");

            var unitContext = StabilityUnits.Create(doc.ModelUnitSystem);
            var volumeScaleToCubicMeters = Math.Pow(unitContext.LengthToMeters, 3.0);

            var hasDensity = TryReadFiniteDouble(parameters?["density"], out var density) && density > 0.0;
            var hasMass = TryReadFiniteDouble(parameters?["mass"], out var mass) && mass > 0.0;
            if (hasDensity == hasMass)
            {
                throw new InvalidOperationException(
                    "Pass exactly one of 'density' (kg/m^3) or 'mass' (kg per object).");
            }

            var overwrite = parameters?["overwrite"]?.Type == JTokenType.Boolean
                ? parameters["overwrite"].Value<bool>()
                : true;

            var targets = ResolveMassTargets(doc, parameters);
            if (targets.Count == 0)
            {
                throw new InvalidOperationException(
                    "Mass scope matched no objects; widen ids/names/layer/selected.");
            }

            var assigned = new JArray();
            var skipped = new JArray();
            var totalKilograms = 0.0;

            foreach (var rhinoObject in targets)
            {
                var guidString = rhinoObject.Id.ToString();

                if (!overwrite)
                {
                    var existing = rhinoObject.Attributes.GetUserString(StabilityKey);
                    if (!string.IsNullOrWhiteSpace(existing) &&
                        JObject.Parse(existing)["mass"] != null)
                    {
                        skipped.Add(new JObject
                        {
                            ["guid"] = guidString,
                            ["name"] = rhinoObject.Name,
                            ["reason"] = "Object already carries a mass and overwrite is off."
                        });
                        continue;
                    }
                }

                double massKilograms;
                double? volumeCubicMeters = null;
                if (hasDensity)
                {
                    if (!TryGetObjectVolume(rhinoObject.Geometry, out var volume))
                    {
                        skipped.Add(new JObject
                        {
                            ["guid"] = guidString,
                            ["name"] = rhinoObject.Name,
                            ["reason"] = "Object has no computable closed volume; pass 'mass' instead."
                        });
                        continue;
                    }

                    volumeCubicMeters = volume * volumeScaleToCubicMeters;
                    massKilograms = density * volumeCubicMeters.Value;
                }
                else
                {
                    massKilograms = mass;
                }

                if (!double.IsFinite(massKilograms) || massKilograms <= 0.0)
                {
                    skipped.Add(new JObject
                    {
                        ["guid"] = guidString,
                        ["name"] = rhinoObject.Name,
                        ["reason"] = "Computed mass is not positive and finite."
                    });
                    continue;
                }

                var payload = new JObject
                {
                    ["mass"] = massKilograms,
                    ["mass_unit"] = StabilityUnits.KilogramUnit
                };
                rhinoObject.Attributes.SetUserString(StabilityKey, payload.ToString(Formatting.None));
                rhinoObject.CommitChanges();

                totalKilograms += massKilograms;
                var record = new JObject
                {
                    ["guid"] = guidString,
                    ["name"] = rhinoObject.Name,
                    ["mass"] = massKilograms,
                    ["mass_unit"] = StabilityUnits.KilogramUnit
                };
                if (volumeCubicMeters.HasValue)
                {
                    record["volume_m3"] = volumeCubicMeters.Value;
                }

                assigned.Add(record);
            }

            doc.Views.Redraw();

            return new JObject
            {
                ["success"] = true,
                ["source"] = hasDensity ? "density" : "mass",
                ["density_kg_m3"] = hasDensity ? density : null,
                ["assigned"] = assigned,
                ["skipped"] = skipped,
                ["total_mass_kg"] = totalKilograms,
                ["document_length_unit"] = doc.ModelUnitSystem.ToString(),
                ["mass_unit"] = StabilityUnits.KilogramUnit
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

    private static List<RhinoObject> ResolveMassTargets(RhinoDoc doc, JObject parameters)
    {
        var targets = new List<RhinoObject>();
        var seen = new HashSet<Guid>();

        void Add(RhinoObject candidate)
        {
            if (candidate == null || candidate.IsDeleted)
                return;
            if (seen.Add(candidate.Id))
                targets.Add(candidate);
        }

        if (parameters?["ids"] is JArray ids)
        {
            foreach (var token in ids)
            {
                if (Guid.TryParse(token?.ToString(), out var guid))
                    Add(doc.Objects.FindId(guid));
            }
        }

        if (parameters?["names"] is JArray names)
        {
            var wanted = names.Select(n => n?.ToString())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (wanted.Count > 0)
            {
                foreach (var obj in doc.Objects)
                {
                    if (!string.IsNullOrWhiteSpace(obj?.Name) && wanted.Contains(obj.Name))
                        Add(obj);
                }
            }
        }

        var layerTokens = new List<string>();
        if (parameters?["layer"] is JArray layers)
        {
            layerTokens.AddRange(layers.Select(l => l?.ToString()).Where(l => !string.IsNullOrWhiteSpace(l)));
        }
        else if (parameters?["layer"] != null)
        {
            var single = parameters["layer"].ToString();
            if (!string.IsNullOrWhiteSpace(single))
                layerTokens.Add(single);
        }

        if (layerTokens.Count > 0)
        {
            var wanted = layerTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in doc.Objects)
            {
                var layer = doc.Layers.FindIndex(obj.Attributes.LayerIndex);
                if (layer == null)
                    continue;
                if (wanted.Contains(layer.Name) || wanted.Contains(layer.FullPath))
                    Add(obj);
            }
        }

        if (parameters?["selected"]?.Type == JTokenType.Boolean &&
            parameters["selected"].Value<bool>())
        {
            foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
                Add(obj);
        }

        // No scope at all means the whole document, matching how the graph and the
        // evaluator read an omitted scope.
        if (targets.Count == 0 &&
            parameters?["ids"] == null && parameters?["names"] == null &&
            parameters?["layer"] == null && parameters?["selected"] == null)
        {
            foreach (var obj in doc.Objects)
                Add(obj);
        }

        return targets;
    }
}
