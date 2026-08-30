using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    /// <summary>
    /// Where the last evaluation's full report is kept, so the parts a summary leaves out
    /// can be read back without running the solver again.
    /// </summary>
    public const string StabilityReportKey = "rhinomcp-mod:stability-report";

    /// <summary>
    /// The sections of a report that grow with the model, and the field each is sorted by
    /// when it is paged. A 104-element bridge answers in 112 KB, 95% of it these; the verdict
    /// and everything needed to read it fit in 4 KB.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> HeavySections =
        new Dictionary<string, string>
        {
            ["joint_forces"] = "tension_n",
            ["nodes"] = "diameter_m",
            ["ground_sites"] = "fz_n",
            ["bodies"] = "displacement_m",
            ["joint_welded_examples"] = "shared_particles",
            ["motion_samples_m"] = null,
            ["rotation_samples_deg"] = null,
            ["time_samples_s"] = null,
            ["speed_samples_m_s"] = null,
            ["joint_sharing_histogram"] = null
        };

    private const int DefaultReportLimit = 20;
    private const int MaxReportLimit = 500;
    private const int SummaryTopCount = 5;
    private const int SummaryCapacityCount = 10;

    /// <summary>
    /// Evaluates, keeps the whole report on the document, and returns as much of it as the
    /// caller asked for. <c>detail</c> is <c>"summary"</c> unless told otherwise.
    /// </summary>
    /// <remarks>
    /// The summary exists because a tool result over about 100 KB does not reach the model
    /// at all - it is written to a file the model then has to search - and the per-joint
    /// tables cross that on any real assembly. The verdict never needed them; the questions
    /// that do ("which joint carries the most tension") are answered by
    /// <see cref="GetStabilityReport"/> from the stored copy, one page at a time.
    /// </remarks>
    public JObject EvaluateStability(JObject parameters)
    {
        var detail = parameters?["detail"]?.ToString()?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(detail) && detail != "summary" && detail != "full")
        {
            return new JObject
            {
                ["success"] = false,
                ["message"] = $"Unknown detail '{detail}'; use 'summary' or 'full'."
            };
        }

        var full = EvaluateStabilityFull(parameters);
        if (full?["success"]?.Value<bool>() != true)
        {
            return full;
        }

        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
        {
            try
            {
                doc.Strings.SetString(StabilityReportKey, full.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                full["report_store_warning"] = $"Full report was not stored: {ex.Message}";
            }
        }

        if (detail == "full")
        {
            full["detail"] = "full";
            return full;
        }

        return CompactStabilityResult(full);
    }

    /// <summary>
    /// The report without its per-element tables, plus what a reader most often wanted
    /// them for: the most-loaded joints, anything that yielded, the ground reactions.
    /// </summary>
    internal static JObject CompactStabilityResult(JObject full)
    {
        var result = (JObject)full.DeepClone();
        var omitted = new JObject();
        foreach (var section in HeavySections.Keys)
        {
            switch (result[section])
            {
                case JArray array:
                    omitted[section] = array.Count;
                    result.Remove(section);
                    break;
                case JObject obj:
                    omitted[section] = obj.Count;
                    result.Remove(section);
                    break;
            }
        }

        if (full["joint_forces"] is JArray forces && forces.Count > 0)
        {
            result["joint_forces_summary"] = SummariseJointForces(forces);
        }

        if (full["ground_sites"] is JArray ground && ground.Count > 0)
        {
            result["ground_sites_summary"] = SummariseGroundSites(ground);
        }

        result["detail"] = "summary";
        result["omitted_sections"] = omitted;
        result["report_key"] = StabilityReportKey;
        result["report_hint"] =
            "get_stability_report(section=...) pages any omitted section from the stored " +
            "report; evaluate_stability(detail=\"full\") returns everything at once.";
        return result;
    }

    private static readonly string[] JointForceSummaryFields =
    {
        "body", "guid", "with", "joint_type", "force_n", "tension_n", "shear_n",
        "peak_point_tension_n", "capacity_n", "reached_capacity", "bearing_points"
    };

    private static JObject SummariseJointForces(JArray forces)
    {
        var records = forces.OfType<JObject>().ToList();
        double Field(JObject record, string name) =>
            record[name]?.Type is JTokenType.Float or JTokenType.Integer
                ? record[name].Value<double>()
                : double.NaN;

        var summary = new JObject
        {
            ["count"] = records.Count,
            ["max_force_n"] = MaxOrNull(records.Select(r => Field(r, "force_n"))),
            ["max_tension_n"] = MaxOrNull(records.Select(r => Field(r, "tension_n"))),
            ["max_shear_n"] = MaxOrNull(records.Select(r => Field(r, "shear_n"))),
            ["at_capacity"] = records.Count(r => r["reached_capacity"]?.Value<bool>() == true)
        };

        // Tension first: it is what a dry joint cannot carry and a fastener is sized for.
        // A record without a tension figure has no bearing normal, so it sorts by force.
        summary["top_by_tension"] = new JArray(records
            .Where(r => !double.IsNaN(Field(r, "tension_n")))
            .OrderByDescending(r => Field(r, "tension_n"))
            .Take(SummaryTopCount)
            .Select(Trim));
        summary["top_by_force"] = new JArray(records
            .OrderByDescending(r => Field(r, "force_n"))
            .Take(SummaryTopCount)
            .Select(Trim));

        var yielded = records.Where(r => r["reached_capacity"]?.Value<bool>() == true).ToList();
        if (yielded.Count > 0)
        {
            summary["at_capacity_joints"] = new JArray(yielded.Take(SummaryCapacityCount).Select(Trim));
        }

        return summary;

        JObject Trim(JObject record)
        {
            var trimmed = new JObject();
            foreach (var field in JointForceSummaryFields)
            {
                if (record[field] != null)
                {
                    trimmed[field] = record[field];
                }
            }

            return trimmed;
        }
    }

    private static JObject SummariseGroundSites(JArray ground)
    {
        var records = ground.OfType<JObject>().ToList();
        var loads = records
            .Select(r => r["fz_n"]?.Value<double?>())
            .Where(v => v.HasValue)
            .Select(v => v.Value)
            .ToList();
        return new JObject
        {
            ["count"] = records.Count,
            ["opened"] = records.Count(r => (r["opened"]?.Value<int>() ?? 0) > 0),
            ["fz_total_n"] = loads.Count > 0 ? Math.Round(loads.Sum(), 1) : (double?)null,
            ["fz_min_n"] = loads.Count > 0 ? Math.Round(loads.Min(), 1) : (double?)null,
            ["fz_max_n"] = loads.Count > 0 ? Math.Round(loads.Max(), 1) : (double?)null
        };
    }

    private static double? MaxOrNull(IEnumerable<double> values)
    {
        var finite = values.Where(v => !double.IsNaN(v)).ToList();
        return finite.Count > 0 ? Math.Round(finite.Max(), 3) : (double?)null;
    }

    /// <summary>
    /// Reads one section of the last evaluation's stored report, filtered, sorted and paged.
    /// With no section named it lists what is there.
    /// </summary>
    public JObject GetStabilityReport(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var text = doc?.Strings.GetValue(StabilityReportKey);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JObject
            {
                ["success"] = false,
                ["message"] = "No stored stability report in this document. Run evaluate_stability first."
            };
        }

        JObject full;
        try
        {
            full = JObject.Parse(text);
        }
        catch (JsonException ex)
        {
            return new JObject
            {
                ["success"] = false,
                ["message"] = $"Stored stability report could not be read: {ex.Message}"
            };
        }

        var section = parameters?["section"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(section) || section == "sections")
        {
            return ListSections(full);
        }

        var token = full[section];
        if (token == null)
        {
            return new JObject
            {
                ["success"] = false,
                ["message"] = $"The stored report has no section '{section}'.",
                ["sections"] = ListSections(full)["sections"]
            };
        }

        if (token is not JArray array)
        {
            return new JObject
            {
                ["success"] = true,
                ["section"] = section,
                ["value"] = token
            };
        }

        var records = array.ToList();
        var total = records.Count;
        records = Filter(records, parameters);
        var matched = records.Count;

        var sortField = parameters?["sort"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(sortField))
        {
            HeavySections.TryGetValue(section, out sortField);
        }

        var ascending = parameters?["ascending"]?.Type == JTokenType.Boolean &&
            parameters["ascending"].Value<bool>();
        if (!string.IsNullOrEmpty(sortField) && records.All(r => r is JObject))
        {
            double Key(JToken r) =>
                r[sortField]?.Type is JTokenType.Float or JTokenType.Integer
                    ? r[sortField].Value<double>()
                    : double.NegativeInfinity;
            records = ascending
                ? records.OrderBy(Key).ToList()
                : records.OrderByDescending(Key).ToList();
        }
        else
        {
            sortField = null;
        }

        var offset = Math.Max(0, parameters?["offset"]?.Value<int?>() ?? 0);
        var limit = Math.Clamp(parameters?["limit"]?.Value<int?>() ?? DefaultReportLimit, 1, MaxReportLimit);
        var page = records.Skip(offset).Take(limit).ToList();

        return new JObject
        {
            ["success"] = true,
            ["section"] = section,
            ["total"] = total,
            ["matched"] = matched,
            ["offset"] = offset,
            ["returned"] = page.Count,
            ["sort"] = sortField,
            ["ascending"] = ascending,
            ["records"] = new JArray(page)
        };
    }

    private static JObject ListSections(JObject full)
    {
        var sections = new JObject();
        foreach (var property in full.Properties())
        {
            sections[property.Name] = property.Value switch
            {
                JArray array => $"list[{array.Count}]",
                JObject obj => $"object[{obj.Count}]",
                _ => property.Value.Type.ToString().ToLowerInvariant()
            };
        }

        return new JObject
        {
            ["success"] = true,
            ["stable"] = full["stable"],
            ["mode"] = full["mode"],
            ["sections"] = sections,
            ["pageable"] = new JArray(HeavySections.Keys.Where(k => full[k] is JArray))
        };
    }

    private static List<JToken> Filter(List<JToken> records, JObject parameters)
    {
        var ids = (parameters?["ids"] as JArray)?
            .Select(t => t?.ToString()?.Trim().ToLowerInvariant())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet();
        var jointType = parameters?["joint_type"]?.ToString()?.Trim().ToLowerInvariant();
        var minTension = parameters?["min_tension_n"]?.Value<double?>();
        var reachedOnly = parameters?["reached_capacity_only"]?.Type == JTokenType.Boolean &&
            parameters["reached_capacity_only"].Value<bool>();

        if (ids == null && string.IsNullOrEmpty(jointType) && !minTension.HasValue && !reachedOnly)
        {
            return records;
        }

        return records.Where(record =>
        {
            if (record is not JObject obj)
            {
                return false;
            }

            if (ids != null)
            {
                var own = obj["guid"]?.ToString()?.ToLowerInvariant();
                var members = (obj["members"] as JArray)?
                    .Select(m => m?.ToString()?.ToLowerInvariant()) ?? Enumerable.Empty<string>();
                if (!(own != null && ids.Contains(own)) && !members.Any(m => m != null && ids.Contains(m)))
                {
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(jointType) &&
                !string.Equals(obj["joint_type"]?.ToString(), jointType, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(obj["type"]?.ToString(), jointType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (minTension.HasValue &&
                (obj["tension_n"]?.Value<double?>() ?? double.NegativeInfinity) < minTension.Value)
            {
                return false;
            }

            if (reachedOnly && obj["reached_capacity"]?.Value<bool>() != true)
            {
                return false;
            }

            return true;
        }).ToList();
    }
}
