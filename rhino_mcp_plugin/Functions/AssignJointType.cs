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
/// classes* - "beam to column is fixed", "truss to truss is pinned" - because that is how an
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

            var pruning = parameters?["prune"]?.Type == JTokenType.Boolean &&
                parameters["prune"].Value<bool>();

            var typeText = parameters?["joint_type"]?.ToString();

            // Nothing asked for is a request to look, not an error. Without this there is no
            // way to read the rule table at all, which makes "the stale ones are reported"
            // untrue in the one case where someone is checking for them.
            if (!clear && !pruning && string.IsNullOrWhiteSpace(typeText) &&
                parameters?["ids"] == null && parameters?["names"] == null &&
                parameters?["layer"] == null && parameters?["with_layer"] == null &&
                parameters?["with_ids"] == null && parameters?["with_names"] == null &&
                parameters?["selected"] == null)
            {
                var current = ReadPairRules(doc);
                return new JObject
                {
                    ["success"] = true,
                    ["scope"] = "list",
                    ["rules"] = PairRulesReport(current, doc),
                    ["stale_rules"] = current.Values.Count(rule => StaleReason(doc, rule) != null)
                };
            }

            // How much this joint can hold, stated with what it is. Absent means unlimited,
            // which is what every joint was before anyone could say otherwise.
            double? capacityNewtons = null;
            if (parameters?["capacity_kn"] != null &&
                parameters["capacity_kn"].Type != JTokenType.Null)
            {
                var kilonewtons = parameters["capacity_kn"].Value<double>();
                if (!(kilonewtons > 0.0) || double.IsInfinity(kilonewtons))
                {
                    throw new InvalidOperationException(
                        "capacity_kn must be a positive number of kilonewtons.");
                }

                capacityNewtons = kilonewtons * 1000.0;
            }

            var type = StabilityRigidBodies.JointType.Fixed;
            if (!clear && !pruning && !StabilityRigidBodies.TryParseJointType(typeText, out type))
            {
                throw new InvalidOperationException(
                    $"Unknown joint_type '{typeText}'. Expected contact, pin or fixed.");
            }

            // Rules that can no longer match anything, cleared out on request.
            //
            // Asked for rather than done on sight: a deleted object can be undone, and a rule
            // dropped in between would not come back with it. Reported by every other call,
            // so the tidying is visible before it is wanted.
            if (parameters?["prune"]?.Type == JTokenType.Boolean &&
                parameters["prune"].Value<bool>())
            {
                var all = ReadPairRules(doc);
                var removed = new JArray();
                foreach (var entry in all.ToList())
                {
                    var why = StaleReason(doc, entry.Value);
                    if (why == null)
                    {
                        continue;
                    }

                    removed.Add(new JObject
                    {
                        ["a"] = entry.Value.A,
                        ["b"] = entry.Value.B,
                        ["joint_type"] = TypeName(entry.Value.Type),
                        ["capacity_kn"] = entry.Value.CapacityNewtons.HasValue
                            ? entry.Value.CapacityNewtons.Value / 1000.0
                            : (double?)null,
                        ["stale"] = why
                    });
                    all.Remove(entry.Key);
                }

                WritePairRules(doc, all);
                return new JObject
                {
                    ["success"] = true,
                    ["scope"] = "prune",
                    ["removed"] = removed,
                    ["rules"] = PairRulesReport(all, doc)
                };
            }

            // Each side of a pair rule names a class of element, and there are two ways to
            // name one: by layer, which is how a trade is usually organised, and by object,
            // for the joint that is genuinely its own case. Same choice Rhino gives everywhere
            // else, and the same choice assign_mass already gives on its single scope.
            var sideB = ReadPairTokens(doc, parameters, "with_layer", "with_ids", "with_names");

            // The other side of a rule can be the ground itself.
            //
            // A base is founded or it is not, and geometry cannot tell: a pad cast into a
            // footing and one set down on gravel are drawn identically. So it is stated, the
            // same way every other connection is. Its own token rather than a layer named
            // "ground", which anyone might have.
            if (parameters?["with_ground"]?.Type == JTokenType.Boolean &&
                parameters["with_ground"].Value<bool>())
            {
                sideB.Add(GroundToken);
            }
            var sideA = ReadPairTokens(doc, parameters, "layer", "ids", "names");

            if (sideB.Count > 0)
            {
                if (sideA.Count == 0)
                {
                    throw new InvalidOperationException(
                        "'with_layer'/'with_ids' name one half of a pair rule, so 'layer', 'ids' " +
                        "or 'names' is required for the other.");
                }

                var rules = ReadPairRules(doc);
                var written = new JArray();
                foreach (var a in sideA)
                {
                    foreach (var b in sideB)
                    {
                        // A rule from an element to itself would fire on every joint between
                        // two members of that class, which is what naming the class once
                        // already means. Naming one *object* twice is a joint from a body to
                        // itself, which does not exist.
                        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) &&
                            a.StartsWith(IdTokenPrefix, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var key = PairKey(a, b);
                        if (clear)
                        {
                            rules.Remove(key);
                        }
                        else
                        {
                            var made = MakePairRule(a, b, type);
                            made.CapacityNewtons = capacityNewtons;
                            rules[key] = made;
                        }

                        written.Add(new JObject
                        {
                            ["a"] = a,
                            ["b"] = b,
                            ["joint_type"] = clear ? null : TypeName(type),
                            ["capacity_kn"] = clear || !capacityNewtons.HasValue
                                ? null
                                : capacityNewtons.Value / 1000.0
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
                    ["rules"] = PairRulesReport(rules, doc)
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
                    payload.Remove("joint_capacity_kn");
                }
                else
                {
                    payload["joint_type"] = TypeName(type);
                    if (capacityNewtons.HasValue)
                    {
                        payload["joint_capacity_kn"] = capacityNewtons.Value / 1000.0;
                    }
                    else
                    {
                        payload.Remove("joint_capacity_kn");
                    }
                }

                rhinoObject.Attributes.SetUserString(StabilityKey, payload.ToString(Formatting.None));
                rhinoObject.CommitChanges();

                assigned.Add(new JObject
                {
                    ["guid"] = rhinoObject.Id.ToString(),
                    ["name"] = rhinoObject.Name,
                    ["layer"] = doc.Layers.FindIndex(rhinoObject.Attributes.LayerIndex)?.Name,
                    ["joint_type"] = clear ? null : TypeName(type),
                    ["capacity_kn"] = clear || !capacityNewtons.HasValue
                        ? null
                        : capacityNewtons.Value / 1000.0
                });
            }

            doc.Views.Redraw();
            return new JObject
            {
                ["success"] = true,
                ["scope"] = "element",
                ["cleared"] = clear,
                ["assigned"] = assigned,
                ["rules"] = PairRulesReport(ReadPairRules(doc), doc)
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

    /// <summary>
    /// What one object says its own joints are, from the user string its mass lives in.
    /// </summary>
    /// <remarks>
    /// Shared with the graph overlay rather than left inline in the evaluator, so what is
    /// drawn and what is solved cannot drift apart. An overlay that colours joints by a
    /// second reading of the same rules is worth less than no overlay at all.
    /// </remarks>
    internal static bool TryGetElementJointType(
        Rhino.DocObjects.RhinoObject rhinoObject, out StabilityRigidBodies.JointType type)
    {
        return TryGetElementJointType(rhinoObject, out type, out _);
    }

    internal static bool TryGetElementJointType(
        Rhino.DocObjects.RhinoObject rhinoObject, out StabilityRigidBodies.JointType type,
        out double? capacityNewtons)
    {
        type = StabilityRigidBodies.JointType.Fixed;
        capacityNewtons = null;
        var stored = rhinoObject?.Attributes?.GetUserString(StabilityKey);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        try
        {
            var payload = JObject.Parse(stored);
            var kilonewtons = payload.Value<double?>("joint_capacity_kn");
            if (kilonewtons.HasValue && kilonewtons.Value > 0.0)
            {
                capacityNewtons = kilonewtons.Value * 1000.0;
            }

            return StabilityRigidBodies.TryParseJointType(
                payload["joint_type"]?.ToString(), out type);
        }
        catch (Exception)
        {
            return false;
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

    /// <summary>A class of element, as one of the two things a pair rule names.</summary>
    /// <remarks>
    /// Prefixed rather than bare so a layer called after a GUID cannot be mistaken for the
    /// object of that name. It also makes the stored rule readable: "layer:Beams" says what
    /// kind of thing it is without needing the code that wrote it.
    /// </remarks>
    internal const string LayerTokenPrefix = "layer:";

    /// <summary>
    /// The ground, as one half of a pair rule.
    /// </summary>
    /// <remarks>
    /// Prefixed like the others so it cannot collide with a layer or an object of that name,
    /// and readable in the stored rule: "ground: to layer:PAD is fixed" says that those pads
    /// are founded rather than set down.
    /// </remarks>
    internal const string GroundToken = "ground:";
    internal const string IdTokenPrefix = "id:";

    /// <summary>How specific a token is, so the tighter rule wins where two match.</summary>
    private static int TokenRank(string token)
    {
        return token != null && token.StartsWith(IdTokenPrefix, StringComparison.Ordinal) ? 2 : 1;
    }

    /// <summary>One pair rule, with the classes it was written for.</summary>
    internal sealed class PairRule
    {
        public string A;
        public string B;
        public StabilityRigidBodies.JointType Type;

        /// <summary>
        /// The most tension this joint can hold, in newtons, or null for unlimited.
        /// </summary>
        /// <remarks>
        /// Tension only. Compression is limited by the material of the things meeting, not by
        /// whatever holds them together, and a contact joint already refuses tension outright -
        /// so this binds exactly on the joints someone declared strong, which is where the
        /// model is otherwise unboundedly optimistic.
        /// </remarks>
        public double? CapacityNewtons;

        /// <summary>How specific this rule is: two named objects beat an object and a layer.</summary>
        public int Rank => TokenRank(A) + TokenRank(B);

        /// <summary>How this rule names itself in a report, the same way round every time.</summary>
        public string Label => "pair:" + Friendly(A) + "|" + Friendly(B);

        private static string Friendly(string token)
        {
            return token != null && token.StartsWith(LayerTokenPrefix, StringComparison.Ordinal)
                ? token.Substring(LayerTokenPrefix.Length)
                : token;
        }
    }

    /// <summary>
    /// One side of a pair rule, from whichever of the scope arguments was given.
    /// </summary>
    private static List<string> ReadPairTokens(
        RhinoDoc doc, JObject parameters, string layerKey, string idsKey, string namesKey)
    {
        var tokens = new List<string>();
        foreach (var layer in ReadLayerTokens(parameters?[layerKey]))
        {
            tokens.Add(LayerTokenPrefix + layer.Trim());
        }

        if (parameters?[idsKey] is JArray ids)
        {
            foreach (var token in ids)
            {
                if (Guid.TryParse(token?.ToString(), out var guid))
                {
                    tokens.Add(IdTokenPrefix + guid);
                }
            }
        }

        // Names are resolved to ids as they are written, because a name is a label on an
        // object and can be changed or duplicated, while the rule has to keep meaning the
        // element it was written for.
        if (parameters?[namesKey] is JArray names && doc != null)
        {
            var wanted = names.Select(n => n?.ToString())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (wanted.Count > 0)
            {
                foreach (var obj in doc.Objects)
                {
                    if (!string.IsNullOrWhiteSpace(obj?.Name) && wanted.Contains(obj.Name))
                    {
                        tokens.Add(IdTokenPrefix + obj.Id);
                    }
                }
            }
        }

        return tokens;
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
            A = ordered ? left : right,
            B = ordered ? right : left,
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
            // back into the two classes it was built from.
            foreach (var entry in JArray.Parse(stored).OfType<JObject>())
            {
                var a = entry["a"]?.ToString();
                var b = entry["b"]?.ToString();
                if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                {
                    continue;
                }

                if (StabilityRigidBodies.TryParseJointType(entry["joint_type"]?.ToString(), out var type))
                {
                    var rule = MakePairRule(a, b, type);
                    var capacity = entry.Value<double?>("capacity_kn");
                    if (capacity.HasValue && capacity.Value > 0.0)
                    {
                        rule.CapacityNewtons = capacity.Value * 1000.0;
                    }

                    rules[PairKey(a, b)] = rule;
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

        doc.Strings.SetString(JointTypeRulesKey, PairRulesPayload(rules).ToString(Formatting.None));
    }

    /// <summary>What is stored: the rule and nothing derived from the document.</summary>
    private static JArray PairRulesPayload(Dictionary<string, PairRule> rules)
    {
        var payload = new JArray();
        foreach (var rule in Ordered(rules))
        {
            payload.Add(new JObject
            {
                ["a"] = rule.A,
                ["b"] = rule.B,
                ["joint_type"] = TypeName(rule.Type),
                ["capacity_kn"] = rule.CapacityNewtons.HasValue
                    ? rule.CapacityNewtons.Value / 1000.0
                    : (double?)null
            });
        }

        return payload;
    }

    private static IEnumerable<PairRule> Ordered(Dictionary<string, PairRule> rules)
    {
        return rules.Values
            .OrderBy(r => r.A, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.B, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why a rule can no longer match anything.
    /// </summary>
    /// <remarks>
    /// A rule naming an object outlives that object: it lives in document text and the object
    /// does not, so deleting the beam leaves the rule behind, matching nothing and saying
    /// nothing. They accumulate quietly - a document here collected two inside an afternoon of
    /// rebuilding test scenes.
    ///
    /// Reported rather than removed on sight, because a deleted object can be undone and a
    /// rule silently dropped in between would not come back with it. Removal is asked for.
    /// </remarks>
    /// <summary>
    /// How many of the stored rules name something that is not in the document. For prompts
    /// that count rules: a count that includes the dead ones overstates what will happen.
    /// </summary>
    internal static int CountStaleRules(RhinoDoc doc, Dictionary<string, PairRule> rules)
    {
        return rules.Values.Count(rule => StaleReason(doc, rule) != null);
    }

    private static string StaleReason(RhinoDoc doc, PairRule rule)
    {
        var reasons = new List<string>();
        foreach (var token in new[] { rule.A, rule.B })
        {
            if (token.StartsWith(IdTokenPrefix, StringComparison.Ordinal))
            {
                var text = token.Substring(IdTokenPrefix.Length);
                if (!Guid.TryParse(text, out var guid) || doc?.Objects.FindId(guid) == null)
                {
                    reasons.Add($"object {text} is not in the document");
                }
            }
            else if (token.StartsWith(LayerTokenPrefix, StringComparison.Ordinal))
            {
                var name = token.Substring(LayerTokenPrefix.Length);
                if (doc?.Layers.FindName(name, -1) == null)
                {
                    reasons.Add($"layer '{name}' does not exist");
                }
            }
        }

        return reasons.Count > 0 ? string.Join("; ", reasons) : null;
    }

    private static JArray PairRulesReport(Dictionary<string, PairRule> rules, RhinoDoc doc = null)
    {
        var report = new JArray();
        foreach (var rule in Ordered(rules))
        {
            var entry = new JObject
            {
                ["a"] = rule.A,
                ["b"] = rule.B,
                ["joint_type"] = TypeName(rule.Type)
            };

            var stale = doc == null ? null : StaleReason(doc, rule);
            if (stale != null)
            {
                entry["stale"] = stale;
            }

            report.Add(entry);
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
        /// <remarks>
        /// Each body offers what it can be named by - the object itself, and the layer it sits
        /// on - and the tightest rule matching any combination wins. Two named objects beat an
        /// object and a layer, which beats two layers, so "this beam meets that column as a
        /// pin" survives a blanket "beams meet columns fixed" rather than being averaged with
        /// it. Specificity, not order: a rule table is not a script.
        /// </remarks>
        public StabilityRigidBodies.JointType Resolve(
            string guidA, string layerA, StabilityRigidBodies.JointType? elementA,
            string guidB, string layerB, StabilityRigidBodies.JointType? elementB,
            out string rule)
        {
            return Resolve(
                guidA, layerA, elementA, guidB, layerB, elementB, null, null, out rule, out _);
        }

        /// <summary>
        /// The same resolution, also answering how much this joint can hold.
        /// </summary>
        /// <remarks>
        /// Capacity travels with the rule that set the type, so one statement about a joint
        /// says both what it is and what it can take. Where two element rules meet, the
        /// smaller capacity governs for the same reason the weaker type does: a joint is no
        /// stronger than the weaker of the two things it connects.
        /// </remarks>
        public StabilityRigidBodies.JointType Resolve(
            string guidA, string layerA, StabilityRigidBodies.JointType? elementA,
            string guidB, string layerB, StabilityRigidBodies.JointType? elementB,
            double? capacityA, double? capacityB,
            out string rule, out double? capacity)
        {
            capacity = null;
            PairRule best = null;
            foreach (var tokenA in Tokens(guidA, layerA))
            {
                foreach (var tokenB in Tokens(guidB, layerB))
                {
                    if (_pairs.TryGetValue(PairKey(tokenA, tokenB), out var candidate) &&
                        (best == null || candidate.Rank > best.Rank))
                    {
                        best = candidate;
                    }
                }
            }

            if (best != null)
            {
                // The rule's own name for itself, not one built from the order these two
                // bodies happened to arrive in.
                rule = best.Label;
                capacity = best.CapacityNewtons;
                return best.Type;
            }

            // Weakest of the two elements' own rules. Not "last one wins": that would make the
            // answer depend on the order the rules were given in, and on which body the graph
            // happened to list first at this joint. "one" and "both" for the same reason - "a"
            // and "b" would report the graph's edge direction, which means nothing to a reader.
            capacity = Weaker(capacityA, capacityB);

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

        /// <summary>
        /// What holds a body to the ground, which is a bearing unless somebody says otherwise.
        /// </summary>
        /// <remarks>
        /// Only a rule naming the ground answers this. An element rule saying a beam's joints
        /// are fixed is about the beam's joints to other elements and says nothing about
        /// whether it is founded, and the global default is the same: a thing set on the
        /// floor rests on it. Founding is a claim about a footing, so it has to be made.
        /// </remarks>
        public StabilityRigidBodies.JointType ResolveGround(
            string guid, string layer, out string rule)
        {
            PairRule best = null;
            foreach (var token in Tokens(guid, layer))
            {
                if (_pairs.TryGetValue(PairKey(token, GroundToken), out var candidate) &&
                    (best == null || candidate.Rank > best.Rank))
                {
                    best = candidate;
                }
            }

            if (best != null)
            {
                rule = best.Label;
                return best.Type;
            }

            rule = "ground:default";
            return StabilityRigidBodies.JointType.Contact;
        }

        /// <summary>The smaller of two capacities, treating "unstated" as unlimited.</summary>
        private static double? Weaker(double? a, double? b)
        {
            if (!a.HasValue)
            {
                return b;
            }

            return !b.HasValue ? a : Math.Min(a.Value, b.Value);
        }

        private static IEnumerable<string> Tokens(string guid, string layer)
        {
            if (!string.IsNullOrWhiteSpace(guid))
            {
                yield return IdTokenPrefix + guid.Trim();
            }

            if (!string.IsNullOrWhiteSpace(layer))
            {
                yield return LayerTokenPrefix + layer.Trim();
            }
        }
    }
}
