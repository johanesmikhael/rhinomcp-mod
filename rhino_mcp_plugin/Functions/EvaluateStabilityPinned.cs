using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;
using KangarooSolver;
using KangarooSolver.Goals;

namespace RhinoMCPModPlugin.Functions;

/// <summary>
/// The pinned multi-body evaluation mode.
/// </summary>
/// <remarks>
/// The welded mode makes the whole assembly one rigid body, so the only failure it can
/// express is the entire thing tipping over. It is blind to a mechanism: a slab resting on
/// a single column, free to rotate off it, is welded to that column and reads as sound.
///
/// This mode gives every element its own rigid body and joins them at the contact points
/// the connectivity graph already found. Kangaroo merges coincident points into one
/// particle, so two bodies that list the same contact point are pinned there for free -
/// no contact goal, no patch geometry. A body pinned at one point is free to rotate about
/// it, at two points about their axis, and at three non-collinear points is held. That is
/// classical kinematic determinacy, and it is exactly the failure the welded mode cannot
/// see.
///
/// The complement is just as real: a pin holds in tension, so nothing here can lift off or
/// topple off anything. An eccentric block that would fall over is held by its pins. The
/// two modes therefore answer different questions and neither subsumes the other - welded
/// catches overturning, pinned catches mechanisms.
/// </remarks>
public partial class RhinoMCPModFunctions
{
    public const string PinnedEvaluationMode = "multi_body_pinned";

    // Markers give each body three non-shared particles so its transform can be recovered
    // the same way the welded mode recovers the assembly's. PhysicalSystem exposes particle
    // positions but not orientations, so without them a body's rotation is unreadable.
    private const int BodyMarkerCount = 3;

    // A pinned assembly is either determinate or it is a mechanism, so this only has to
    // separate solver noise from real motion. Ten millimetres is far above the former and
    // far below any movement worth calling a collapse.
    public const double PinnedMechanismDisplacementMeters = 0.01;

    /// <summary>
    /// A mechanism is judged against the span it sits in, not against a fixed length.
    /// </summary>
    /// <remarks>
    /// An absolute limit answers a different question at every scale: ten millimetres is
    /// gross collapse in a bookshelf and is inside the elastic deflection of a motorway
    /// bridge. L/200 is the deflection limit ordinary serviceability rules already use, so
    /// a structure that passes it is one whose joints are holding still relative to how far
    /// apart they are.
    /// </remarks>
    public const double PinnedSpanDivisor = 200.0;

    /// <summary>
    /// Below this the span-relative limit would sit inside the solver's own equilibrium
    /// error, so it is held here instead. It is not a physical statement about the
    /// structure - it is the floor of what this solver can resolve.
    /// </summary>
    public const double PinnedMechanismFloorMeters = 0.001;

    /// <summary>
    /// The span the mechanism limit is taken against: how far apart this assembly's
    /// supports are, or its own extent when it has none to measure between.
    /// </summary>
    private static double PinnedSpanMeters(List<PinnedBody> bodies)
    {
        var supports = new List<Point3d>();
        foreach (var body in bodies)
        {
            supports.AddRange(body.GroundPoints);
        }

        // Support spacing is the honest span: it is the distance the structure has to
        // bridge. Two supports one metre apart do not get a wider allowance because the
        // assembly happens to cantilever past them.
        var span = WidestSeparation(supports);
        if (span > 0.0)
        {
            return span;
        }

        // Nothing is anchored, so there is no spacing to measure. The assembly's own extent
        // is the only length available.
        var all = new List<Point3d>();
        foreach (var body in bodies)
        {
            all.AddRange(body.JointPoints);
            all.Add(body.BodyPlane.Origin);
        }

        return WidestSeparation(all);
    }

    private static double WidestSeparation(List<Point3d> points)
    {
        if (points.Count < 2)
        {
            return 0.0;
        }

        // The bounding box diagonal, rather than every pair: the same answer for the
        // spacing that matters and linear in the number of points.
        var box = new BoundingBox(points);
        return box.IsValid ? box.Diagonal.Length : 0.0;
    }

    private static double PinnedMechanismThresholdMeters(List<PinnedBody> bodies)
    {
        return Math.Max(PinnedSpanMeters(bodies) / PinnedSpanDivisor, PinnedMechanismFloorMeters);
    }

    /// <summary>Everything the pinned solver needs to know about one element.</summary>
    private sealed class PinnedBody
    {
        public StabilityNode Node { get; set; }
        public Mesh SolverMesh { get; set; }
        public Plane BodyPlane { get; set; }
        public Point3d Centroid { get; set; }
        public List<Point3d> JointPoints { get; } = new();

        /// <summary>
        /// The bearing region behind each joint point, in the same order. Invalid where the
        /// contact was found by intersection or proximity rather than by sampling. Only the
        /// rigid-body solver reads it - the particle solver holds all of a body's points to
        /// one frame at a single strength, so it cannot give one joint four points and
        /// another two without changing what every joint means.
        /// </summary>
        public List<ContactExtent> JointExtents { get; } = new();

        /// <summary>How much tension each joint can hold, in newtons, or null for unlimited.</summary>
        public List<double?> JointCapacities { get; } = new();

        /// <summary>
        /// What each joint is, in the same order. Welded until something says otherwise -
        /// that is the behaviour these joints already had, since a spring over a measured
        /// bearing carries moment and tension both.
        /// </summary>
        public List<StabilityRigidBodies.JointType> JointTypes { get; } = new();
        public List<Point3d> GroundPoints { get; } = new();
        public Point3d[] Markers { get; set; }
        public int[] MarkerParticles { get; set; }
        public Plane InitialMarkerPlane { get; set; }
        public Transform DocumentTransform { get; set; } = Transform.Identity;
        public int JointCount => JointPoints.Count;

        /// <summary>Solver particles for this body's pins, in the order the pins were added.</summary>
        public int[] JointParticles { get; set; } = System.Array.Empty<int>();
    }

    /// <summary>
    /// Reads the graph's edges as joints. The welded mode ignores them entirely; here each
    /// edge is a pin shared by the two bodies it connects.
    /// </summary>
    /// <summary>
    /// One exactly measured bearing, as the solver's <see cref="ContactExtent"/>.
    /// </summary>
    /// <remarks>
    /// The stored form is positional: frame origin, both in-plane axes, the two half-lengths,
    /// then polygon area, offset, piece and pair counts, region counts, and finally the flags
    /// that say what kind of contact it is. A line carries a zero half-width on purpose - a
    /// line contact has no width, and <c>BearingPoints</c> collapses that axis to a single
    /// position, which is what makes such a joint a hinge about the line rather than a plate.
    /// </remarks>
    private static bool TryReadExactBearing(
        JArray stored, double lengthToMeters, bool allowBuried, out ContactExtent extent)
    {
        extent = default;
        if (stored == null || stored.Count < 14)
        {
            return false;
        }

        var isLine = stored.Count > 17 && stored[17].Value<double>() != 0.0;
        var isBuried = stored.Count > 20 && stored[20].Value<double>() != 0.0;
        if (isBuried && !allowBuried)
        {
            return false;
        }

        var frame = new Plane(
            new Point3d(
                stored[0].Value<double>() * lengthToMeters,
                stored[1].Value<double>() * lengthToMeters,
                stored[2].Value<double>() * lengthToMeters),
            new Vector3d(stored[3].Value<double>(), stored[4].Value<double>(), stored[5].Value<double>()),
            new Vector3d(stored[6].Value<double>(), stored[7].Value<double>(), stored[8].Value<double>()));
        if (!frame.IsValid)
        {
            return false;
        }

        var halfU = stored[9].Value<double>() * lengthToMeters;
        var halfV = stored[10].Value<double>() * lengthToMeters;
        if (!(halfU > 0.0))
        {
            return false;
        }

        extent = new ContactExtent
        {
            IsValid = true,
            Frame = frame,
            HalfU = halfU,
            HalfV = isLine ? 0.0 : halfV,
            Samples = 0
        };
        return true;
    }

    private static List<PinnedBody> BuildPinnedBodies(
        JObject graph,
        List<StabilityNode> nodes,
        double lengthToMeters,
        double floorZMeters,
        double groundToleranceMeters,
        bool sharePins = true,
        JArray clusterReport = null,
        JointTypeRules jointTypeRules = null,
        bool preferExactBearings = false,
        bool allowBuriedBearings = false)
    {
        var bodies = new List<PinnedBody>(nodes.Count);
        foreach (var node in nodes)
        {
            var solverGeometry = node.Geometry.Duplicate();
            if (solverGeometry == null ||
                !solverGeometry.Transform(Transform.Scale(Point3d.Origin, lengthToMeters)))
            {
                throw new InvalidOperationException(
                    $"Object {node.Node["g"]} could not be scaled from document units to meters.");
            }

            var mesh = AsMesh(solverGeometry);
            if (mesh == null || mesh.Vertices.Count < 3)
            {
                throw new InvalidOperationException(
                    $"Object {node.Node["g"]} could not be meshed in solver meter space.");
            }

            var centroid = TryVolumeCentroid(mesh, out var volumeCentroid)
                ? volumeCentroid
                : mesh.GetBoundingBox(true).Center;

            var body = new PinnedBody
            {
                Node = node,
                SolverMesh = mesh,
                Centroid = centroid,
                BodyPlane = new Plane(centroid, Vector3d.XAxis, Vector3d.YAxis)
            };

            // Ground contacts are pinned to the world. In a mechanism test the question is
            // whether the assembly can move at all, so a footing that neither slides nor
            // lifts is the right idealisation; overturning is the welded mode's job.
            var groundSeen = new HashSet<(long, long, long)>();
            foreach (var vertex in mesh.Vertices)
            {
                var point = new Point3d(vertex.X, vertex.Y, vertex.Z);
                if (point.Z > floorZMeters + groundToleranceMeters)
                {
                    continue;
                }

                if (TrySiteKey(point, DefaultAssignToleranceMeters, out var key) && groundSeen.Add(key))
                {
                    body.GroundPoints.Add(point);
                }
            }

            bodies.Add(body);
        }

        // Graph edges arrive as [a, b, x, y, z]: the two node indices and their contact
        // point. In pinned mode both bodies receive the identical point, so the solver
        // merges them into one particle and the joint exists without a goal of its own. In
        // contact mode the points are not shared; they seed a patch instead.
        if (!sharePins)
        {
            return bodies;
        }

        var links = new List<JointLink>();
        // Bearings measured by intersecting the two bodies' flat faces, parallel to the edge
        // array by index. They ride separately because the edge payload is read by position
        // and would be awkward to extend a second time.
        var exactBearings = graph["ex"] as JArray;
        if (graph["e"] is JArray edges)
        {
            for (var edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
            {
                if (edges[edgeIndex] is not JArray edge || edge.Count < 5)
                {
                    continue;
                }

                var a = edge[0].Value<int>();
                var b = edge[1].Value<int>();
                if (a < 0 || b < 0 || a >= bodies.Count || b >= bodies.Count || a == b)
                {
                    continue;
                }

                var contact = new Point3d(
                    edge[2].Value<double>() * lengthToMeters,
                    edge[3].Value<double>() * lengthToMeters,
                    edge[4].Value<double>() * lengthToMeters);
                if (!contact.IsValid)
                {
                    continue;
                }

                // The bearing region, when the graph measured one. Eleven numbers follow the
                // contact point: the rectangle's centre, its two in-plane axes and its
                // half-lengths, in document units. Only the rigid-body solver uses them.
                var extent = default(ContactExtent);
                if (edge.Count >= 16)
                {
                    var origin = new Point3d(
                        edge[5].Value<double>() * lengthToMeters,
                        edge[6].Value<double>() * lengthToMeters,
                        edge[7].Value<double>() * lengthToMeters);
                    var axisU = new Vector3d(
                        edge[8].Value<double>(), edge[9].Value<double>(), edge[10].Value<double>());
                    var axisV = new Vector3d(
                        edge[11].Value<double>(), edge[12].Value<double>(), edge[13].Value<double>());
                    var frame = new Plane(origin, axisU, axisV);
                    if (frame.IsValid)
                    {
                        extent = new ContactExtent
                        {
                            IsValid = true,
                            Frame = frame,
                            HalfU = edge[14].Value<double>() * lengthToMeters,
                            HalfV = edge[15].Value<double>() * lengthToMeters,
                            Samples = edge.Count > 16 ? edge[16].Value<int>() : 0
                        };
                    }
                }

                // The exact measurement, where it exists and was asked for. It replaces the
                // sampled rectangle rather than supplementing it: they describe the same
                // bearing and the sampled one is the approximation.
                //
                // A buried bearing is the surface inside the volume two bodies share, and it
                // is gated separately. Its area grows with how far the drawing goes through
                // itself, so it hands a joint moment capacity in proportion to a modelling
                // artefact - fine where an overlap is a deliberate socket, and not something
                // to switch on for every truss node that happens to interpenetrate.
                if (preferExactBearings && exactBearings != null && edgeIndex < exactBearings.Count &&
                    TryReadExactBearing(
                        exactBearings[edgeIndex] as JArray, lengthToMeters, allowBuriedBearings,
                        out var exact))
                {
                    extent = exact;
                }

                links.Add(new JointLink { A = a, B = b, Point = contact, Extent = extent });
            }
        }

        ClusterJointsIntoNodes(bodies, links, clusterReport, jointTypeRules);

        return bodies;
    }

    /// <summary>
    /// Axial stiffness of the member a body represents, EA/L, taken from its mass rather
    /// than from a modelled section.
    /// </summary>
    /// <remarks>
    /// A prismatic member of mass m, length L and density rho has area A = m/(rho L), so
    /// EA/L = (E/rho) m / L^2. That reads the real section even when the geometry is drawn
    /// solid, which is the usual case for a catalogue part: the bridge's members are drawn
    /// as 150 mm solid boxes but massed as hollow sections, and this returns the hollow
    /// section's stiffness.
    ///
    /// Falls back to the load-referenced figure when the geometry gives no usable length -
    /// a blob with no long axis is not a member and has no EA/L to speak of.
    /// </remarks>
    /// <summary>
    /// How long a member is, independently of how it is turned in the world.
    /// </summary>
    /// <remarks>
    /// This was the longest edge of the world axis-aligned bounding box, which is exact for a
    /// member lying along an axis and wrong for every other one: tilt a 2 m member into the
    /// x-z diagonal and its box shrinks to 1.41 m while the member does not. Since k goes as
    /// 1/L^2 that reported 6.25e8 N/m where the truth was 3.61e8 - the same member, the same
    /// mass, 1.7 times stiffer for being rotated. Twenty of the test bridge's forty members
    /// are diagonal webs and all five braces are diagonals, so every sway figure it has ever
    /// produced carried this.
    ///
    /// The greatest distance between any two vertices is orientation-independent by
    /// construction. For a prismatic member it is sqrt(L^2 + section^2), which for anything
    /// slender enough to call a member is L to a fraction of a percent - 2.011 m for a 2 m
    /// member of 150 mm section. For a stubby body it is the diagonal, and a body with no
    /// long axis has no EA/L worth speaking of anyway.
    /// </remarks>
    private static double MemberLengthMeters(Mesh mesh)
    {
        if (mesh == null || mesh.Vertices.Count < 2)
        {
            return 0.0;
        }

        // Every pair is fine for the boxes and sections this sees, and the bound keeps a
        // dense mesh from turning a stiffness lookup into a quadratic sweep. Past it the
        // bounding box diagonal is used, which is still orientation-independent enough to
        // beat the longest edge.
        const int PairwiseVertexLimit = 512;
        if (mesh.Vertices.Count > PairwiseVertexLimit)
        {
            var box = mesh.GetBoundingBox(true);
            return box.IsValid ? box.Diagonal.Length : 0.0;
        }

        var points = mesh.Vertices.ToPoint3dArray();
        var longest = 0.0;
        for (var i = 0; i < points.Length; i++)
        {
            for (var j = i + 1; j < points.Length; j++)
            {
                longest = Math.Max(longest, points[i].DistanceTo(points[j]));
            }
        }

        return longest;
    }

    private static double MemberAxialStiffness(
        PinnedBody body, double specificStiffness, double carriedNewtons, double slipMeters)
    {
        var length = MemberLengthMeters(body.SolverMesh);
        var mass = body.Node.MassKilograms;

        if (!(length > 0.0) || !(mass > 0.0) || !(specificStiffness > 0.0))
        {
            return Math.Max(carriedNewtons, MinimumJointLoadNewtons) / slipMeters;
        }

        return specificStiffness * mass / (length * length);
    }

    /// <summary>
    /// Kangaroo's rigid goal proposes a quarter of its correction each iteration, so
    /// equilibrium sits at four times the error a full correction would leave. Passing four
    /// times the intended stiffness cancels it.
    /// </summary>
    public const double RelaxationCompensation = 4.0;

    /// <summary>
    /// A member's two ends are held by two springs in series, so each has to be twice the
    /// member's axial stiffness for the pair to deliver EA/L end to end.
    /// </summary>
    /// <remarks>
    /// Not a guess. Three 2 m columns of known EA/L, 3.611e7 N/m each, under a 196 kN block:
    /// the arithmetic says they shorten W/3k = 1.810 mm and the solver reported 3.627 - a
    /// ratio of 2.003. The path from one pin to the other runs pin, body-to-frame spring,
    /// frame, body-to-frame spring, pin, and two springs of strength S in series deliver
    /// S/2. Every stiffness this evaluator has ever reported was therefore half of what it
    /// claimed, which is why the bridge's sway figures move when this lands.
    ///
    /// It is the same factor the rigid-body path needs for a different reason, and both are
    /// checked by the same micro case: see scripts/stability_regression/cases.py.
    /// </remarks>
    public const double EndSpringsInSeries = 2.0;

    /// <summary>
    /// Specific stiffness, E over rho, in m^2/s^2. Structural steel by default.
    /// </summary>
    /// <remarks>
    /// Only the ratio ever mattered. A member's axial stiffness here is E A / L with the area
    /// recovered from mass, A = m / (rho L), so k = (E/rho) m / L^2 - and E and rho never
    /// appear apart. Two parameters were one all along, and asking for them separately invited
    /// a model that was right about neither.
    ///
    /// It is also a quantity worth seeing, because it barely moves across the materials this
    /// evaluator is pointed at. Steel is 210e9/7850 = 2.68e7. C24 spruce is 11e9/420 = 2.62e7,
    /// within two percent, and that is not a coincidence - it is why a timber member sized for
    /// the same load as a steel one deflects about the same amount. A model that never states
    /// its material is already close for both.
    ///
    /// What this does *not* capture is the connection, which for mass timber is the flexible
    /// part and has nothing to do with either number. State `joint_stiffness_n_per_m` when the
    /// joint governs, which for a screwed or doweled connection it usually does.
    /// </remarks>
    public const double DefaultSpecificStiffnessM2S2 = 210e9 / 7850.0;

    /// <summary>
    /// The load each body carries down to its supports, for a pinned assembly.
    /// </summary>
    /// <remarks>
    /// The same descent the contact mode makes over its patches, expressed over shared pin
    /// points instead: bodies are taken from the top down, and each sheds what it has
    /// accumulated through the joints below it and the ground beneath it. A body that hangs
    /// from its neighbours keeps its load, which is correct - it is not carrying anything
    /// down.
    /// </remarks>
    private static double[] PinnedCarriedLoads(List<PinnedBody> bodies, double gravity)
    {
        var carried = new double[bodies.Count];
        var height = new double[bodies.Count];
        for (var i = 0; i < bodies.Count; i++)
        {
            carried[i] = gravity * bodies[i].Node.MassKilograms;
            height[i] = bodies[i].BodyPlane.Origin.Z;
        }

        // Which bodies meet at a pin, found from the points they share.
        var atPoint = new Dictionary<(long, long, long), List<int>>();
        for (var i = 0; i < bodies.Count; i++)
        {
            foreach (var pin in bodies[i].JointPoints)
            {
                if (!TrySiteKey(pin, DefaultAssignToleranceMeters, out var key))
                {
                    continue;
                }

                if (!atPoint.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    atPoint[key] = list;
                }

                if (!list.Contains(i))
                {
                    list.Add(i);
                }
            }
        }

        var order = Enumerable.Range(0, bodies.Count).OrderByDescending(i => height[i]).ToList();
        foreach (var index in order)
        {
            var below = new List<int>();
            foreach (var pin in bodies[index].JointPoints)
            {
                if (!TrySiteKey(pin, DefaultAssignToleranceMeters, out var key) ||
                    !atPoint.TryGetValue(key, out var list))
                {
                    continue;
                }

                foreach (var other in list)
                {
                    if (other != index && height[other] < height[index] && !below.Contains(other))
                    {
                        below.Add(other);
                    }
                }
            }

            var ways = below.Count + (bodies[index].GroundPoints.Count > 0 ? 1 : 0);
            if (ways == 0)
            {
                continue;
            }

            var share = carried[index] / ways;
            foreach (var other in below)
            {
                carried[other] += share;
            }
        }

        return carried;
    }

    /// <summary>One graph edge read as a joint: the two bodies and where they bear.</summary>
    private sealed class JointLink
    {
        public int A { get; set; }
        public int B { get; set; }
        public Point3d Point { get; set; }
        public ContactExtent Extent { get; set; }
        public int Cluster { get; set; } = -1;
    }

    /// <summary>
    /// Gathers the pairwise bearing points around each element into the structural nodes
    /// they actually belong to, and gives every body one point per node.
    /// </summary>
    /// <remarks>
    /// The graph reports one bearing point per pair of elements, not one per node. Six
    /// members meeting at a truss node therefore arrive as fifteen scattered points, none
    /// coincident, and the solver merges none of them - so instead of one pin the node
    /// becomes fifteen independent ball joints and the truss is a mechanism before gravity
    /// is applied. Measured on a 40-member bridge: 167 joint points for 17 nodes, and every
    /// one of them its own particle.
    ///
    /// The clustering radius is not a constant. Under single linkage the number of clusters
    /// changes only at the merge distances of the minimum spanning tree over the points, so
    /// sweeping a radius and looking for a plateau is the same thing as reading the largest
    /// gap in those distances - which is exact, and free. Points within one node sit tens of
    /// millimetres apart while neighbouring nodes are metres away, so the gap is decisive.
    ///
    /// Two safeguards. The knee is found per body rather than globally, so a small bracket
    /// and a large beam each set their own scale, which matters in a model whose parts vary
    /// by orders of magnitude. And whatever the knee says, a cluster may not span more than
    /// the element's own section: geometry vetoes, so a degenerate spread of points cannot
    /// fuse two real nodes and silently weld the structure.
    /// </remarks>
    private static void ClusterJointsIntoNodes(
        List<PinnedBody> bodies, List<JointLink> links, JArray report,
        JointTypeRules jointTypeRules = null)
    {
        if (links.Count == 0)
        {
            return;
        }

        var parent = new int[links.Count];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        void Union(int i, int j)
        {
            var ri = Find(i);
            var rj = Find(j);
            if (ri != rj)
            {
                parent[rj] = ri;
            }
        }

        // What each link is, resolved from its own two elements before anything is merged.
        //
        // The order matters and used to be the other way round. Resolving per cluster asks
        // "what is this node", but a node is a thing the clustering invented: at a truss
        // support the bottom chord, a vertical, a diagonal, a brace and the pad all sit
        // within one cross-section of each other, so the members' bolted connections and the
        // assembly's bearing on the pad arrive as one node. Weakest-governs then applied
        // contact to the bolted connections too, and since a site has one bearing normal for
        // every body at it, those joints opened under a downward pull and the truss came
        // apart at 0.6 m/s.
        //
        // A link, unlike a node, is a fact about the model: these two elements meet. So the
        // rules are asked about that, and the clustering below is told not to merge links
        // that answered differently.
        var rules = jointTypeRules ??
            new JointTypeRules(null, DefaultJointType);
        var linkTypes = new StabilityRigidBodies.JointType[links.Count];
        var linkRules = new string[links.Count];
        var linkCapacities = new double?[links.Count];
        for (var i = 0; i < links.Count; i++)
        {
            var elementA = bodies[links[i].A].Node;
            var elementB = bodies[links[i].B].Node;
            linkTypes[i] = rules.Resolve(
                elementA?.Node?["g"]?.ToString(), elementA?.LayerName, elementA?.ElementJointType,
                elementB?.Node?["g"]?.ToString(), elementB?.LayerName, elementB?.ElementJointType,
                elementA?.ElementJointCapacityNewtons, elementB?.ElementJointCapacityNewtons,
                out linkRules[i], out linkCapacities[i]);
        }

        var byBody = new List<int>[bodies.Count];
        for (var i = 0; i < links.Count; i++)
        {
            (byBody[links[i].A] ??= new List<int>()).Add(i);
            (byBody[links[i].B] ??= new List<int>()).Add(i);
        }

        for (var b = 0; b < bodies.Count; b++)
        {
            var indices = byBody[b];
            if (indices == null || indices.Count < 2)
            {
                continue;
            }

            // The merge distance is the body's own cross-section, not a knee found in its
            // contact points.
            //
            // A knee is a statistical cut, and a per-body one moves whenever that body's
            // set of contacts changes - which happens for reasons that have nothing to do
            // with the body. Adding five diagonals to the bottom plane of the bridge split
            // seven nodes that the diagonals do not touch, three of them at the ridge, one
            // fragment landing 0.1 mm from the true node and the other 30 mm away. A node
            // split in two is worse than a node found approximately: the members meeting
            // there stop sharing a particle, and the joint silently stops being a joint.
            //
            // The separation of scales is already known and does not have to be discovered.
            // Contact between two members spreads over the thickness of the thinner one -
            // measured at 111 to 134 mm here for a 150 mm section - while two distinct
            // joints on the same member are a member length apart, 2000 mm. Merging within
            // one cross-section therefore captures a joint and cannot reach the next one,
            // and unlike a knee it gives the same answer whatever else is in the model.
            var box = bodies[b].SolverMesh.GetBoundingBox(true);
            var section = Math.Min(box.Diagonal.X, Math.Min(box.Diagonal.Y, box.Diagonal.Z));
            var radius = Math.Max(section, DefaultAssignToleranceMeters);
            if (!(radius > 0.0))
            {
                continue;
            }

            // Where the body's own middle is, which is what says whether two contacts are on
            // the same face of it or on opposite ones.
            var middle = box.Center;

            for (var i = 0; i < indices.Count; i++)
            {
                for (var j = i + 1; j < indices.Count; j++)
                {
                    // Two joints that are near each other and are not the same joint stay
                    // apart. Where every link agrees - which is every model given one joint
                    // type, and nearly all of the suite - this never fires and the clustering
                    // is exactly what it was.
                    if (linkTypes[indices[i]] != linkTypes[indices[j]])
                    {
                        continue;
                    }

                    var here = links[indices[i]].Point;
                    var there = links[indices[j]].Point;

                    // Two contacts with the body's own middle between them are on opposite
                    // faces of it, and opposite faces are two joints however close together
                    // they are.
                    //
                    // The radius above is the body's smallest dimension, chosen because
                    // contact between two members spreads over the thickness of the thinner
                    // one while two joints on the same member are a member length apart. That
                    // separation of scales is a fact about slender members and not about
                    // plates: a 200 mm spacer's smallest dimension IS the distance between its
                    // two faces, so its top and bottom joints sat exactly one radius apart and
                    // merged. The two storeys either side of it came back as three bodies
                    // meeting at one point 100 mm from where either face is.
                    //
                    // Distance cannot separate those two cases, because in one of them the
                    // right answer and the wrong answer are the same number. Which side of the
                    // body each contact is on can.
                    var axis = there - here;
                    if (axis.Unitize() &&
                        ((here - middle) * axis) * ((there - middle) * axis) < 0.0)
                    {
                        continue;
                    }

                    if (here.DistanceTo(there) <= radius)
                    {
                        Union(indices[i], indices[j]);
                    }
                }
            }
        }

        var centres = new Dictionary<int, (Point3d Sum, int Count)>();
        foreach (var index in Enumerable.Range(0, links.Count))
        {
            var root = Find(index);
            var entry = centres.TryGetValue(root, out var found) ? found : (Point3d.Origin, 0);
            centres[root] = (entry.Item1 + links[index].Point, entry.Item2 + 1);
        }

        var nodePoints = new Dictionary<int, Point3d>();
        foreach (var pair in centres)
        {
            nodePoints[pair.Key] = pair.Value.Sum / pair.Value.Count;
        }

        // The largest measured region in each cluster, since a cluster is one node and one
        // node gets one region. Largest rather than merged: two contacts that clustered are
        // the same joint seen twice, and the bigger bearing is the one carrying the load.
        var nodeExtents = new Dictionary<int, ContactExtent>();
        foreach (var index in Enumerable.Range(0, links.Count))
        {
            var extent = links[index].Extent;
            if (!extent.IsValid)
            {
                continue;
            }

            var root = Find(index);
            if (!nodeExtents.TryGetValue(root, out var best) || extent.Area > best.Area)
            {
                nodeExtents[root] = extent;
            }
        }

        // What each node is. Its links agree by construction now - the clustering above
        // refused to merge any that did not - so this reads the answer rather than reducing
        // to it. The weakest is still taken, which costs nothing when they are all equal and
        // keeps the rule honest if that ever stops being true.
        var nodeTypes = new Dictionary<int, StabilityRigidBodies.JointType>();
        var nodeRules = new Dictionary<int, string>();
        var nodeCapacities = new Dictionary<int, double?>();
        foreach (var index in Enumerable.Range(0, links.Count))
        {
            var root = Find(index);
            if (!nodeTypes.TryGetValue(root, out var best) || linkTypes[index] < best)
            {
                nodeTypes[root] = linkTypes[index];
                nodeRules[root] = linkRules[index];
            }

            // The smallest capacity in the node, treating unstated as unlimited: a node is no
            // stronger than the weakest thing meeting in it.
            var stated = linkCapacities[index];
            if (stated.HasValue)
            {
                nodeCapacities[root] = nodeCapacities.TryGetValue(root, out var held) && held.HasValue
                    ? Math.Min(held.Value, stated.Value)
                    : stated.Value;
            }
        }

        // One point per body per node, so the bodies meeting there share a single particle.
        var placed = new HashSet<(int Body, int Node)>();
        foreach (var index in Enumerable.Range(0, links.Count))
        {
            var root = Find(index);
            var point = nodePoints[root];
            nodeExtents.TryGetValue(root, out var nodeExtent);
            var nodeType = nodeTypes.TryGetValue(root, out var found)
                ? found
                : rules.Default;
            foreach (var body in new[] { links[index].A, links[index].B })
            {
                if (placed.Add((body, root)))
                {
                    bodies[body].JointPoints.Add(point);
                    bodies[body].JointExtents.Add(nodeExtent);
                    bodies[body].JointTypes.Add(nodeType);
                    bodies[body].JointCapacities.Add(
                        nodeCapacities.TryGetValue(root, out var stated) ? stated : null);
                }
            }
        }

        if (report == null)
        {
            return;
        }

        foreach (var pair in centres)
        {
            var members = new SortedSet<int>();
            var diameter = 0.0;
            var points = new List<Point3d>();
            foreach (var index in Enumerable.Range(0, links.Count))
            {
                if (Find(index) != pair.Key)
                {
                    continue;
                }

                members.Add(links[index].A);
                members.Add(links[index].B);
                points.Add(links[index].Point);
            }

            for (var i = 0; i < points.Count; i++)
            {
                for (var j = i + 1; j < points.Count; j++)
                {
                    diameter = Math.Max(diameter, points[i].DistanceTo(points[j]));
                }
            }

            var centre = nodePoints[pair.Key];

            // Which bodies meet here, not merely how many. Counts cannot tell a node that
            // gathered the right elements from one that gathered a plausible number of the
            // wrong ones, and the pinned verdict is only as good as this topology.
            var memberList = new JArray();
            foreach (var member in members)
            {
                memberList.Add(bodies[member].Node.Node["g"]?.ToString());
            }

            // The type each node came out as, and the rule that decided it. A verdict that
            // changed because a rule matched more joints than intended has to be diagnosable
            // without re-deriving the rules by hand.
            report.Add(new JObject
            {
                ["bodies"] = members.Count,
                ["edges"] = pair.Value.Count,
                ["diameter_m"] = diameter,
                ["centre_m"] = new JArray(centre.X, centre.Y, centre.Z),
                ["members"] = memberList,
                ["joint_type"] = TypeName(
                    nodeTypes.TryGetValue(pair.Key, out var nodeType) ? nodeType : rules.Default),
                ["joint_type_rule"] = nodeRules.TryGetValue(pair.Key, out var nodeRule)
                    ? nodeRule
                    : "default"
            });
        }
    }

    /// <summary>
    /// The clustering radius for one element's bearing points, read off the largest gap in
    /// its single-linkage merge distances.
    /// </summary>
    /// <remarks>
    /// Returns the ceiling when the points offer no real gap - a spread with no structure in
    /// it should not be forced into one, and the geometric clamp is the safer answer.
    /// </remarks>
    private static double KneeRadius(List<JointLink> links, List<int> indices, double ceiling)
    {
        // Prim's algorithm over the points, keeping the merge distances rather than the tree.
        var n = indices.Count;
        var inTree = new bool[n];
        var best = new double[n];
        for (var i = 0; i < n; i++)
        {
            best[i] = double.MaxValue;
        }

        best[0] = 0.0;
        var merges = new List<double>(n - 1);
        for (var step = 0; step < n; step++)
        {
            var pick = -1;
            for (var i = 0; i < n; i++)
            {
                if (!inTree[i] && (pick < 0 || best[i] < best[pick]))
                {
                    pick = i;
                }
            }

            if (pick < 0)
            {
                break;
            }

            inTree[pick] = true;
            if (step > 0)
            {
                merges.Add(best[pick]);
            }

            for (var i = 0; i < n; i++)
            {
                if (inTree[i])
                {
                    continue;
                }

                var d = links[indices[pick]].Point.DistanceTo(links[indices[i]].Point);
                if (d < best[i])
                {
                    best[i] = d;
                }
            }
        }

        if (merges.Count == 0)
        {
            return ceiling;
        }

        merges.Sort();

        // The largest ratio jump is the knee. Anything below the floor is noise rather than
        // a scale change, and a jump from a near-zero distance would divide by nothing.
        var kneeIndex = -1;
        var bestRatio = NodeKneeMinimumRatio;
        for (var i = 0; i + 1 < merges.Count; i++)
        {
            var lower = Math.Max(merges[i], DefaultAssignToleranceMeters);
            var ratio = merges[i + 1] / lower;
            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                kneeIndex = i;
            }
        }

        if (kneeIndex < 0)
        {
            return ceiling;
        }

        // Cut between the two scales: above everything that belongs together, below the
        // first distance that does not.
        return 0.5 * (merges[kneeIndex] + merges[kneeIndex + 1]);
    }

    /// <summary>
    /// How much larger a merge distance must be than the one below it to count as a change
    /// of scale rather than scatter. A node's own points sit tens of millimetres apart while
    /// its neighbours are metres away, so a real gap clears this by an order of magnitude.
    /// </summary>
    private const double NodeKneeMinimumRatio = 2.0;

    /// <summary>
    /// Three marker points per body, spread on the body's own scale so the plane through
    /// them is well conditioned, and offset from the centroid so they cannot collide with a
    /// joint point and be merged into it.
    /// </summary>
    private static void AssignBodyMarkers(PinnedBody body)
    {
        var box = body.SolverMesh.GetBoundingBox(true);
        var span = Math.Max(box.Diagonal.Length, 1e-3) * 0.37;
        var c = body.Centroid;
        body.Markers = new[]
        {
            new Point3d(c.X + span, c.Y, c.Z),
            new Point3d(c.X, c.Y + span, c.Z),
            new Point3d(c.X, c.Y, c.Z + span)
        };
        body.InitialMarkerPlane = new Plane(body.Markers[0], body.Markers[1], body.Markers[2]);
    }

    /// <summary>
    /// Assembles the pinned mode's result. It deliberately does not carry the welded mode's
    /// fields: there is no single assembly transform, no floor strength and no support
    /// margin here, and reporting them as zero would invite the wrong reading.
    /// </summary>
    private static JObject BuildPinnedResult(
        JObject graph,
        RhinoDoc doc,
        StabilityUnitContext unitContext,
        bool stable,
        double gravity,
        double floorZ,
        bool floorZIsAuto,
        double jointStrength,
        double totalMassKilograms,
        JArray unitWarnings)
    {
        return new JObject
        {
            ["success"] = true,
            ["stable"] = stable,
            ["evaluation_mode"] = PinnedEvaluationMode,
            ["body_count"] = graph["body_count"],
            ["joint_count"] = graph["joint_count"],
            ["particle_count"] = graph["particle_count"],
            ["anchored_ground_points"] = graph["anchored_ground_points"],
            ["solver_iterations"] = graph["solver_steps_run"],
            ["solver_steps_run"] = graph["solver_steps_run"],
            ["document_length_unit"] = doc.ModelUnitSystem.ToString(),
            ["length_to_meters"] = unitContext.LengthToMeters,
            ["mass_unit"] = StabilityUnits.KilogramUnit,
            ["gravity_m_s2"] = gravity,
            ["total_mass_kg"] = totalMassKilograms,
            ["joint_strength"] = jointStrength,
            ["floor_z"] = floorZ,
            ["floor_z_m"] = unitContext.ToMeters(floorZ),
            ["floor_z_auto"] = floorZIsAuto,
            ["motion_trend"] = graph["motion_trend"],
            ["rotation_trend"] = graph["rotation_trend"],
            ["motion_samples_m"] = graph["motion_samples_m"],
            ["rotation_samples_deg"] = graph["rotation_samples_deg"],
            // Which element moved, and how far, is the whole point of this mode: a
            // mechanism is local, and the welded mode can only ever report one number for
            // the entire assembly.
            ["max_body_displacement_m"] = graph["max_body_displacement_m"],
            ["max_body_rotation_deg"] = graph["max_body_rotation_deg"],
            ["worst_body"] = graph["worst_body"],
            ["bodies"] = graph["bodies"],
            ["rotation_threshold_deg"] = DefaultRotationThresholdDegrees,
            ["mechanism_displacement_threshold_m"] = graph["mechanism_threshold_m"],
            ["span_m"] = graph["span_m"],
            ["max_pin_displacement_m"] = graph["max_pin_displacement_m"],
            ["unit_warnings"] = unitWarnings,
            ["evaluation_graph_key"] = EvaluationGraphKey
        };
    }

    private static Vector3d ContactNormalFromCentres(Point3d centreA, Point3d centreB)
    {
        var normal = centreB - centreA;
        if (normal.Length <= RhinoMath.ZeroTolerance)
        {
            return Vector3d.ZAxis;
        }

        normal.Unitize();
        return normal;
    }

    public const string ContactEvaluationMode = "multi_body_contact";


    /// <summary>
    /// Holds each body's total goal weight to the load it carries, so that the pseudo-time
    /// step is set by the load and not by how many joints a body happens to have.
    /// </summary>
    /// <remarks>
    /// Sizing each joint from its own load fixes the magnitude but not the sum. Kangaroo
    /// divides the residual that drives a collapse by the total weight on the body, so a
    /// body held by two joints moves at half the rate of one held by a single joint, and a
    /// long chain that has to rotate as a unit develops its topple several times slower
    /// than a short one. Measured: a three-block stair whose upper pair sat 150 mm outside
    /// its base toppled at 28.8 deg, while a six-block stair at exactly the same 150 mm
    /// settled at 0.08 deg and read as stable.
    ///
    /// Scaling every joint on a body so its weights sum to load/d restores one clock for
    /// the whole model. A joint is shared, so it takes the smaller of the two scales its
    /// bodies ask for - erring towards the softer side, which slows nothing down and keeps
    /// the joint from being stiffer than either body can justify. Two passes are enough to
    /// settle the mutual dependency; a third changes the weights by well under a percent.
    /// </remarks>
    private static void NormaliseBodyWeights(
        List<PinnedBody> bodies,
        List<PatchSpec> specs,
        LoadPath loads,
        double jointPenetrationMeters,
        double[] patchWeights,
        double[] groundWeights)
    {
        var targets = new double[bodies.Count];
        for (var i = 0; i < bodies.Count; i++)
        {
            // What this body carries is what leaves it downwards: the ground under it plus
            // the joints it stands on. Counting the joints above as well would count the
            // same load twice - it arrives through them and departs through the supports -
            // and the target would then equal the actual by construction, making the whole
            // pass a no-op. It did, on first writing.
            var carried = loads.Ground[i];
            for (var s = 0; s < specs.Count; s++)
            {
                var spec = specs[s];
                if (spec.A != i && spec.B != i)
                {
                    continue;
                }

                var other = spec.A == i ? spec.B : spec.A;
                if (bodies[other].BodyPlane.Origin.Z < bodies[i].BodyPlane.Origin.Z)
                {
                    carried += loads.Patch[s];
                }
            }

            targets[i] = Math.Max(carried, MinimumJointLoadNewtons) / jointPenetrationMeters;
        }

        for (var pass = 0; pass < 2; pass++)
        {
            var scales = new double[bodies.Count];
            for (var i = 0; i < bodies.Count; i++)
            {
                var actual = groundWeights[i];
                for (var s = 0; s < specs.Count; s++)
                {
                    if (specs[s].A == i || specs[s].B == i)
                    {
                        actual += patchWeights[s];
                    }
                }

                scales[i] = actual > 0.0 ? targets[i] / actual : 1.0;
            }

            for (var s = 0; s < specs.Count; s++)
            {
                patchWeights[s] *= Math.Min(scales[specs[s].A], scales[specs[s].B]);
            }

            for (var i = 0; i < bodies.Count; i++)
            {
                groundWeights[i] *= scales[i];
            }
        }
    }

    /// <summary>One bearing surface, resolved from the geometry before any stiffness is chosen.</summary>
    private sealed class PatchSpec
    {
        public int A { get; set; }
        public int B { get; set; }
        public Point3d Contact { get; set; }
        public List<Point3d> Points { get; set; }
        public List<double> Areas { get; set; }
        public Vector3d Normal { get; set; }
    }

    /// <summary>The load reaching each bearing surface and each body's ground bearing, in newtons.</summary>
    private sealed class LoadPath
    {
        public double[] Patch { get; set; }
        public double[] Ground { get; set; }
    }

    // A joint carrying nothing would otherwise be given zero stiffness and hold nothing at
    // all. This is a floor, not a calibration: one newton is far below any real bearing
    // load and only keeps a load-free joint from vanishing.
    private const double MinimumJointLoadNewtons = 1.0;

    /// <summary>
    /// Walks the assembly's weight down to the ground, one storey at a time, so that every
    /// bearing surface knows the load it actually carries.
    /// </summary>
    /// <remarks>
    /// Bodies are taken from the top down. Each one sheds the weight it has accumulated
    /// through whatever supports it - the joints to bodies below it, plus the ground if it
    /// stands on it - splitting equally between them. Equal splitting is crude where a body
    /// rests on supports of very different stiffness, but the result only sets the pseudo-
    /// time step, and being out by a factor of two on one joint costs nothing: it is the
    /// scale-free ratio to gravity that matters, not the exact share.
    /// </remarks>
    private static LoadPath TributaryLoads(
        List<PinnedBody> bodies, List<PatchSpec> specs, double gravity)
    {
        var patchLoads = new double[specs.Count];
        var groundLoads = new double[bodies.Count];
        var carried = new double[bodies.Count];
        var height = new double[bodies.Count];

        for (var i = 0; i < bodies.Count; i++)
        {
            carried[i] = gravity * bodies[i].Node.MassKilograms;
            height[i] = bodies[i].BodyPlane.Origin.Z;
        }

        // Which side of a joint is above the other is what makes this a load path rather
        // than a graph traversal, and the body's own centre is the only ordering available
        // that survives a joint whose patch normal is not vertical.
        var order = Enumerable.Range(0, bodies.Count).OrderByDescending(i => height[i]).ToList();

        foreach (var index in order)
        {
            var supports = new List<int>();
            for (var s = 0; s < specs.Count; s++)
            {
                if (specs[s].A == index && height[specs[s].B] < height[index])
                {
                    supports.Add(s);
                }
                else if (specs[s].B == index && height[specs[s].A] < height[index])
                {
                    supports.Add(s);
                }
            }

            var standsOnGround = bodies[index].GroundPoints.Count > 0;
            var ways = supports.Count + (standsOnGround ? 1 : 0);
            if (ways == 0)
            {
                // Nothing below it: the body is hanging on its neighbours, and its weight
                // has nowhere to descend to. Leave it on the joints it does have.
                continue;
            }

            var share = carried[index] / ways;
            foreach (var s in supports)
            {
                patchLoads[s] += share;
                var other = specs[s].A == index ? specs[s].B : specs[s].A;
                carried[other] += share;
            }

            if (standsOnGround)
            {
                groundLoads[index] += share;
            }
        }

        return new LoadPath { Patch = patchLoads, Ground = groundLoads };
    }

    // A ground point's share of its body's footprint. The welded mode derives this from
    // mesh tributary areas; here the footprint is split evenly, which is exact for the flat
    // rectangular bases in this catalogue.
    private static double GroundPatchAreaPerPoint(PinnedBody body)
    {
        var box = body.SolverMesh.GetBoundingBox(true);
        var footprint = Math.Max((box.Max.X - box.Min.X) * (box.Max.Y - box.Min.Y), 1e-9);
        return footprint / Math.Max(1, body.GroundPoints.Count);
    }

    public const double FallbackContactAreaM2 = 1e-3;

    // Coulomb friction coefficient for the bearing surfaces. 0.6 is the usual figure for
    // concrete on concrete; without it a dry stack has nothing at all resisting sliding and
    // drifts apart at a steady rate under any lateral load.
    public const double DefaultContactFriction = 0.6;

    // Contact stiffness per square metre of bearing area, for the multi-body modes only.
    // It is five orders above the welded mode's floor strength because gravity is applied
    // differently: welded spreads a body's weight over every mesh vertex, so each Unary
    // carries a few grams, while here one Unary per body carries the whole mass and its
    // Move is g*M - about 14900 for a 1.5 t pedestal. At the welded default of 1e5 the
    // assembly tore itself apart to 1349 m.
    //
    // Measured in Rhino: penetration = 7.7 * bearing pressure / strength, exactly linear
    // over four decades. At 1e10 a 2.4 t cube settles 0.018 mm, a stable stack 0.027 mm and
    // a 39-body tower 0.275 mm, while a block whose centre of mass sits 186 mm outside its
    // support still falls 249 mm and is caught on both trends.
    //
    // Stiffer is not better, for the same reason it was not in the welded mode: at 1e12 the
    // same topple is suppressed to 1.5 mm and 0.0005 deg and survives on the rotation trend
    // alone. 1e10 leaves two decades of margin below that.
    // How far a bearing surface is allowed to close under the load it actually carries,
    // and how far a body is allowed to settle into the ground under the same. These, not a
    // stiffness in Pa/m, are what the automatic mode fixes.
    //
    // A stiffness stated in the absolute is not a material property in this solver, it is
    // the size of the pseudo-time step. Kangaroo blends goals as sum(w*move)/sum(w) and
    // gravity is a Unary of weight 1 proposing g*M, so a body's step down is g*M/sum(w).
    // Pin the stiffness and that step becomes a function of the model's mass and bearing
    // area: at 1e10 a six-block stack whose centre of mass sat 75 mm outside its base moved
    // 1e-9 m per step and read as settled after 2000 steps, while the same stack at 1e7
    // toppled - the same mechanics at a thousand times the clock rate.
    //
    // Referencing the stiffness to the load fixes the ratio instead of the magnitude. With
    // sum(w) = P/d for a body carrying its own weight P = g*M, the step down is exactly d
    // whatever the mass, the bearing area or the document's units. Collapse then develops
    // over the same number of steps in every model.
    // Fraction of a patch's eccentric compression that becomes rotation of the bodies it
    // joins. Carried as a knob because it is the term that decides whether a marginally
    // eccentric joint opens at all.
    public const double DefaultJointPenetrationMeters = 1e-4;
    public const double DefaultGroundSettlementMeters = 1e-4;

    // Bodies must stay rigid against the joints holding them, so the rigid goal is sized
    // from the stiffest joint in the model rather than from an absolute.
    public const double AutoBodyStiffnessRatio = 10.0;

    private sealed class BodyReport
    {
        public JArray Nodes { get; set; }
        public double WorstDisplacement { get; set; }
        public double WorstRotation { get; set; }
        public string WorstNode { get; set; }
    }

    private static double MaxMarkerMotion(PhysicalSystem system, List<PinnedBody> bodies)
    {
        var positions = system.GetPositionArray();
        var worst = 0.0;
        foreach (var body in bodies)
        {
            for (var m = 0; m < BodyMarkerCount; m++)
            {
                var index = body.MarkerParticles[m];
                if (index < positions.Length)
                {
                    worst = Math.Max(worst, positions[index].DistanceTo(body.Markers[m]));
                }
            }
        }

        return worst;
    }

    private static double MaxMarkerRotation(PhysicalSystem system, List<PinnedBody> bodies)
    {
        var positions = system.GetPositionArray();
        var worst = 0.0;
        foreach (var body in bodies)
        {
            var plane = MarkerPlane(positions, body);
            if (plane.HasValue)
            {
                worst = Math.Max(worst, RotationDegreesFromTransform(
                    Transform.PlaneToPlane(body.InitialMarkerPlane, plane.Value)));
            }
        }

        return worst;
    }

    private static Plane? MarkerPlane(Point3d[] positions, PinnedBody body)
    {
        var p0 = body.MarkerParticles[0];
        var p1 = body.MarkerParticles[1];
        var p2 = body.MarkerParticles[2];
        if (p0 >= positions.Length || p1 >= positions.Length || p2 >= positions.Length)
        {
            return null;
        }

        var plane = new Plane(positions[p0], positions[p1], positions[p2]);
        return plane.IsValid ? plane : null;
    }

    private static BodyReport ReportBodies(
        PhysicalSystem system, List<PinnedBody> bodies, JObject graph, double lengthToMeters)
    {
        var positions = system.GetPositionArray();
        var report = new BodyReport { Nodes = new JArray() };
        foreach (var body in bodies)
        {
            var displacement = 0.0;
            var rotationDegrees = 0.0;
            var plane = MarkerPlane(positions, body);
            if (plane.HasValue)
            {
                var xform = Transform.PlaneToPlane(body.InitialMarkerPlane, plane.Value);
                rotationDegrees = RotationDegreesFromTransform(xform);
                var moved = new Point3d(body.Centroid);
                moved.Transform(xform);
                displacement = moved.DistanceTo(body.Centroid);
                body.DocumentTransform = StabilityUnits.SolverTransformToDocument(xform, lengthToMeters);
            }

            if (displacement > report.WorstDisplacement)
            {
                report.WorstDisplacement = displacement;
                report.WorstNode = body.Node.Node["g"]?.ToString();
            }

            report.WorstRotation = Math.Max(report.WorstRotation, rotationDegrees);
            report.Nodes.Add(new JObject
            {
                ["g"] = body.Node.Node["g"],
                ["joints"] = body.JointCount,
                ["ground_points"] = body.GroundPoints.Count,
                ["displacement_m"] = displacement,
                ["rotation_deg"] = rotationDegrees
            });
        }

        return report;
    }

    /// <summary>
    /// Writes the settled pose for the preview, one transform per body.
    /// </summary>
    /// <remarks>
    /// The welded display applies a single assembly transform to every object, which is all
    /// that mode has. Here each element moved on its own, and showing them all shifted by
    /// one average transform would hide exactly the thing this mode exists to reveal.
    /// </remarks>
    private static void WriteMultiBodyDisplay(RhinoDoc doc, List<PinnedBody> bodies)
    {
        foreach (var body in bodies)
        {
            try
            {
                var guidText = body.Node.Node["g"]?.ToString();
                if (string.IsNullOrWhiteSpace(guidText) || !Guid.TryParse(guidText, out var id))
                {
                    continue;
                }

                var obj = doc.Objects.FindId(id);
                if (obj?.Geometry == null)
                {
                    continue;
                }

                var moved = obj.Geometry.Duplicate();
                if (moved == null || !moved.Transform(body.DocumentTransform))
                {
                    continue;
                }

                var mesh = AsMesh(moved);
                if (mesh == null)
                {
                    continue;
                }

                var verts = new JArray();
                foreach (var v in mesh.Vertices)
                {
                    verts.Add(new JArray { v.X, v.Y, v.Z });
                }

                var faces = new JArray();
                foreach (var f in mesh.Faces)
                {
                    faces.Add(f.IsTriangle
                        ? new JArray { f.A, f.B, f.C }
                        : new JArray { f.A, f.B, f.C, f.D });
                }

                var box = mesh.GetBoundingBox(true);
                var summary = new JObject
                {
                    ["type"] = "MESH",
                    ["bbox"] = new JArray(
                        box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z)
                };

                WriteAfterEvaluationFullGeometry(obj, summary, new JObject
                {
                    ["type"] = "MESH",
                    ["vertices"] = verts,
                    ["faces"] = faces
                });
            }
            catch
            {
                // A body that cannot be drawn must not stop the rest of the preview.
            }
        }

        doc.Views.Redraw();
    }
}
