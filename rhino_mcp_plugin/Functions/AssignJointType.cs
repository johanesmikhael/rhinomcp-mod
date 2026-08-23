using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPModPlugin.Functions;

/// <summary>
/// What the joints in a model are, as rules rather than as four hundred joints.
/// </summary>
/// <remarks>
/// Connection type is domain knowledge and not geometry: a screwed panel and a dry-stacked
/// one look identical to an intersection test, and no amount of sampling will tell them
/// apart. So it has to be stated, and the natural unit for stating it is a *pair of element
/// classes* - "beam to column is welded", "truss to truss is pinned" - because that is how an
/// engineer knows it. One rule, not one per joint.
///
/// Three scopes, most specific first:
///
/// - a **pair rule**, both classes named, which decides the joints between those two classes
/// - an **element rule**, one class named, which decides every joint that element has
/// - the **global default**, which is what <c>evaluate_stability</c>'s own joint_type says
///
/// Where two element rules meet and no pair rule covers them, the weaker governs, for the
/// reason the type ordering exists: a hinge assumed where a moment connection exists reads
/// softer and more mechanism-prone than the truth, which fails safe for a stability verdict,
/// and unlike "last rule wins" it does not depend on the order the rules were given in.
///
/// Element rules live on the object beside its mass, in the same user string, because they
/// are a property of that object and should travel with it when it is copied. Pair rules live
/// in document user text, because they are a property of the model rather than of any object
/// in it - there is nowhere on a beam to record what it does when it meets a column.
/// </remarks>
public partial class RhinoMCPModFunctions
{
    /// <summary>Document user text holding the pair rules.</summary>
    public const string JointTypeRulesKey = "rhinomcp.stability.joint_types.v1";

    public JObject AssignJointType(JObject parameters)
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
            {
                throw new InvalidOperationException("No active Rhino document.");
            }

            var clear = parameters?["clear"]?.Type == JTokenType.Boolean &&
                parameters["clear"].Value<bool>();

            var typeText = parameters?["joint_type"]?.ToString();
            var type = StabilityRigidBodies.JointType.Welded;
            if (!clear && !StabilityRigidBodies.TryParseJointType(typeText, out type))
            {
                throw new InvalidOperationException(
                    $"Unknown joint_type '{typeText}'. Expected contact, pin or welded.");
            }

            var withLayers = ReadLayerTokens(parameters?["with_layer"]);
            var layers = ReadLayerTokens(parameters?["layer"]);

            // A pair rule is about two classes of element, so both sides have to be classes.
            // Naming one side by id would be a rule about one object meeting a class, which is
            // an element rule with extra steps and no way to state the other half.
            if (withLayers.Count > 0)
            {
                if (layers.Count == 0)
                {
                    throw new InvalidOperationException(
                        "'with_layer' names the other half of a pair rule, so 'layer' is required too.");
                }

                var rules = ReadPairRules(doc);
                var written = new JArray();
                foreach (var a in layers)
                {
                    foreach (var b in withLayers)
                    {
                        var key = PairKey(a, b);
                        if (clear)
                        {
                            rules.Remove(key);
                        }
                        else
                        {
                            rules[key] = MakePairRule(a, b, type);
                        }

                        written.Add(new JObject
                        {
                            ["layer"] = a,
                            ["with_layer"] = b,
                            ["joint_type"] = clear ? null : TypeName(type)
                        });
                    }
                }

                WritePairRules(doc, rules);
                return new JObject
                {
                    ["success"] = true,
                    ["scope"] = "pair",
                    ["cleared"] = clear,
                    ["rules_written"] = written,
                    ["rules"] = PairRulesReport(rules)
                };
            }

            // Element rule: every joint this object has, unless a pair rule covers one.
            var targets = ResolveMassTargets(doc, parameters);
            if (targets.Count == 0)
            {
                throw new InvalidOperationException(
                    "Joint-type scope matched no objects; widen ids/names/layer/selected.");
            }

            var assigned = new JArray();
            foreach (var rhinoObject in targets)
            {
                var payload = new JObject();
                var existing = rhinoObject.Attributes.GetUserString(StabilityKey);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    try
                    {
                        payload = JObject.Parse(existing);
                    }
                    catch (Exception)
                    {
                        payload = new JObject();
                    }
                }

                // Written beside the mass, never over it.
                if (clear)
                {
                    payload.Remove("joint_type");
                }
                else
                {
                    payload["joint_type"] = TypeName(type);
                }

                rhinoObject.Attributes.SetUserString(StabilityKey, payload.ToString(Formatting.None));
                rhinoObject.CommitChanges();

                assigned.Add(new JObject
                {
                    ["guid"] = rhinoObject.Id.ToString(),
                    ["name"] = rhinoObject.Name,
                    ["layer"] = doc.Layers.FindIndex(rhinoObject.Attributes.LayerIndex)?.Name,
                    ["joint_type"] = clear ? null : TypeName(type)
                });
            }

            doc.Views.Redraw();
            return new JObject
            {
                ["success"] = true,
                ["scope"] = "element",
                ["cleared"] = clear,
                ["assigned"] = assigned,
                ["rules"] = PairRulesReport(ReadPairRules(doc))
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

    internal static string TypeName(StabilityRigidBodies.JointType type)
    {
        return type.ToString().ToLowerInvariant();
    }

    private static List<string> ReadLayerTokens(JToken token)
    {
        var tokens = new List<string>();
        if (token is JArray array)
        {
            tokens.AddRange(array.Select(t => t?.ToString())
                .Where(t => !string.IsNullOrWhiteSpace(t)));
        }
        else if (token != null && !string.IsNullOrWhiteSpace(token.ToString()))
        {
            tokens.Add(token.ToString());
        }

        return tokens;
    }

    /// <summary>One pair rule, with the classes it was written for.</summary>
    internal sealed class PairRule
    {
        public string LayerA;
        public string LayerB;
        public StabilityRigidBodies.JointType Type;

        /// <summary>How this rule names itself in a report, the same way round every time.</summary>
        public string Label => "pair:" + LayerA + "|" + LayerB;
    }

    /// <summary>Separator for a pair key: a control character no layer name can contain.</summary>
    private static readonly string PairSeparator = new string((char)31, 1);

    /// <summary>
    /// One key for a pair, whichever way round it was given.
    /// </summary>
    /// <remarks>
    /// "Beams to Columns" and "Columns to Beams" are the same joint, and a rule table that
    /// held both would let one silently shadow the other depending on which was written last.
    /// Sorting the pair makes that impossible rather than merely unlikely, and it also fixes
    /// what a joint is *called* in a report: the two bodies at a joint reach the solver in
    /// whatever order the graph listed that edge, which is arbitrary, so a label built from
    /// their order would flip between runs of the same model for no reason a reader could see.
    ///
    /// Joined by a separator no layer name can contain rather than by a space, because layer
    /// names do contain spaces and a key that cannot be split back apart is a key that
    /// quietly merges two rules.
    /// </remarks>
    private static string PairKey(string a, string b)
    {
        var left = (a ?? string.Empty).Trim();
        var right = (b ?? string.Empty).Trim();
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) <= 0
            ? left + PairSeparator + right
            : right + PairSeparator + left;
    }

    private static PairRule MakePairRule(string a, string b, StabilityRigidBodies.JointType type)
    {
        var left = (a ?? string.Empty).Trim();
        var right = (b ?? string.Empty).Trim();
        var ordered = string.Compare(left, right, StringComparison.OrdinalIgnoreCase) <= 0;
        return new PairRule
        {
            LayerA = ordered ? left : right,
            LayerB = ordered ? right : left,
            Type = type
        };
    }

    internal static Dictionary<string, PairRule> ReadPairRules(RhinoDoc doc)
    {
        var rules = new Dictionary<string, PairRule>(StringComparer.OrdinalIgnoreCase);
        var stored = doc?.Strings?.GetValue(JointTypeRulesKey);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return rules;
        }

        try
        {
            // An array rather than an object keyed by the pair, so nothing has to parse a key
            // back into two layer names.
            foreach (var entry in JArray.Parse(stored).OfType<JObject>())
            {
                var a = entry["layer"]?.ToString();
                var b = entry["with_layer"]?.ToString();
                if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                {
                    continue;
                }

                if (StabilityRigidBodies.TryParseJointType(entry["joint_type"]?.ToString(), out var type))
                {
                    rules[PairKey(a, b)] = MakePairRule(a, b, type);
                }
            }
        }
        catch (Exception)
        {
            // A rule table that will not parse is a table with no rules in it, which is what
            // the model had before any were written. Failing the evaluation instead would make
            // a stray edit to document text look like a solver defect.
        }

        return rules;
    }

    private static void WritePairRules(RhinoDoc doc, Dictionary<string, PairRule> rules)
    {
        if (rules.Count == 0)
        {
            doc.Strings.Delete(JointTypeRulesKey);
            return;
        }

        doc.Strings.SetString(JointTypeRulesKey, PairRulesReport(rules).ToString(Formatting.None));
    }

    private static JArray PairRulesReport(Dictionary<string, PairRule> rules)
    {
        var report = new JArray();
        foreach (var rule in rules.Values
            .OrderBy(r => r.LayerA, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.LayerB, StringComparer.OrdinalIgnoreCase))
        {
            report.Add(new JObject
            {
                ["layer"] = rule.LayerA,
                ["with_layer"] = rule.LayerB,
                ["joint_type"] = TypeName(rule.Type)
            });
        }

        return report;
    }

    /// <summary>
    /// The rule table as the solver sees it, with the lookup that resolves one joint.
    /// </summary>
    internal sealed class JointTypeRules
    {
        private readonly Dictionary<string, PairRule> _pairs;

        public JointTypeRules(
            Dictionary<string, PairRule> pairs,
            StabilityRigidBodies.JointType fallback)
        {
            _pairs = pairs ?? new Dictionary<string, PairRule>(StringComparer.OrdinalIgnoreCase);
            Default = fallback;
        }

        public StabilityRigidBodies.JointType Default { get; }

        public int PairCount => _pairs.Count;

        /// <summary>What the joint between these two elements is, and which rule said so.</summary>
        public StabilityRigidBodies.JointType Resolve(
            string layerA, StabilityRigidBodies.JointType? elementA,
            string layerB, StabilityRigidBodies.JointType? elementB,
            out string rule)
        {
            if (!string.IsNullOrWhiteSpace(layerA) && !string.IsNullOrWhiteSpace(layerB) &&
                _pairs.TryGetValue(PairKey(layerA, layerB), out var paired))
            {
                // The rule's own name for itself, not one built from the order these two
                // bodies happened to arrive in.
                rule = paired.Label;
                return paired.Type;
            }

            // Weakest of the two elements' own rules. Not "last one wins": that would make the
            // answer depend on the order the rules were given in, and on which body the graph
            // happened to list first at this joint. "one" and "both" for the same reason - "a"
            // and "b" would report the graph's edge direction, which means nothing to a reader.
            if (elementA.HasValue && elementB.HasValue)
            {
                rule = "element:both";
                return (StabilityRigidBodies.JointType)Math.Min((int)elementA.Value, (int)elementB.Value);
            }

            if (elementA.HasValue)
            {
                rule = "element:one";
                return elementA.Value;
            }

            if (elementB.HasValue)
            {
                rule = "element:one";
                return elementB.Value;
            }

            rule = "default";
            return Default;
        }
    }
}
