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

    /// <summary>Everything the pinned solver needs to know about one element.</summary>
    private sealed class PinnedBody
    {
        public StabilityNode Node { get; set; }
        public Mesh SolverMesh { get; set; }
        public Plane BodyPlane { get; set; }
        public Point3d Centroid { get; set; }
        public List<Point3d> JointPoints { get; } = new();
        public List<Point3d> GroundPoints { get; } = new();
        public Point3d[] Markers { get; set; }
        public int[] MarkerParticles { get; set; }
        public Plane InitialMarkerPlane { get; set; }
        public Transform DocumentTransform { get; set; } = Transform.Identity;
        public int JointCount => JointPoints.Count;
    }

    /// <summary>
    /// Reads the graph's edges as joints. The welded mode ignores them entirely; here each
    /// edge is a pin shared by the two bodies it connects.
    /// </summary>
    private static List<PinnedBody> BuildPinnedBodies(
        JObject graph,
        List<StabilityNode> nodes,
        double lengthToMeters,
        double floorZMeters,
        double groundToleranceMeters,
        bool sharePins = true,
        JArray clusterReport = null)
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
        if (graph["e"] is JArray edges)
        {
            foreach (var edgeToken in edges)
            {
                if (edgeToken is not JArray edge || edge.Count < 5)
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

                links.Add(new JointLink { A = a, B = b, Point = contact });
            }
        }

        ClusterJointsIntoNodes(bodies, links, clusterReport);

        return bodies;
    }

    /// <summary>One graph edge read as a joint: the two bodies and where they bear.</summary>
    private sealed class JointLink
    {
        public int A { get; set; }
        public int B { get; set; }
        public Point3d Point { get; set; }
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
        List<PinnedBody> bodies, List<JointLink> links, JArray report)
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

            var box = bodies[b].SolverMesh.GetBoundingBox(true);
            var section = Math.Min(box.Diagonal.X, Math.Min(box.Diagonal.Y, box.Diagonal.Z));
            var ceiling = Math.Max(section, DefaultAssignToleranceMeters);
            var radius = Math.Min(KneeRadius(links, indices, ceiling), ceiling);
            if (!(radius > 0.0))
            {
                continue;
            }

            for (var i = 0; i < indices.Count; i++)
            {
                for (var j = i + 1; j < indices.Count; j++)
                {
                    if (links[indices[i]].Point.DistanceTo(links[indices[j]].Point) <= radius)
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

        // One point per body per node, so the bodies meeting there share a single particle.
        var placed = new HashSet<(int Body, int Node)>();
        foreach (var index in Enumerable.Range(0, links.Count))
        {
            var root = Find(index);
            var point = nodePoints[root];
            foreach (var body in new[] { links[index].A, links[index].B })
            {
                if (placed.Add((body, root)))
                {
                    bodies[body].JointPoints.Add(point);
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
            report.Add(new JObject
            {
                ["bodies"] = members.Count,
                ["edges"] = pair.Value.Count,
                ["diameter_m"] = diameter,
                ["centre_m"] = new JArray(centre.X, centre.Y, centre.Z)
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
    /// Solves the assembly as pinned rigid bodies and reports how far each element moved.
    /// </summary>
    private static bool SolvePinnedFromGraph(
        JObject graph,
        List<StabilityNode> nodes,
        int currentStep,
        double jointStrength,
        double anchorStrength,
        double floorZMeters,
        double gravity,
        double assignToleranceMeters,
        double solverThresholdMeters,
        int solverSubsteps,
        double lengthToMeters,
        RhinoDoc displayDoc)
    {
        var clusterReport = new JArray();
        var bodies = BuildPinnedBodies(
            graph, nodes, lengthToMeters, floorZMeters, GroundContactToleranceMeters,
            sharePins: true, clusterReport: clusterReport);
        if (bodies.Count == 0)
        {
            throw new InvalidOperationException("No bodies were built for the pinned solver.");
        }

        var physicalSystem = new PhysicalSystem();
        var goals = new List<IGoal>();
        var rigidGoals = new List<RigidMesh>(bodies.Count);
        var anchoredGround = 0;

        foreach (var body in bodies)
        {
            AssignBodyMarkers(body);

            var points = new List<Point3d>();
            points.AddRange(body.JointPoints);
            points.AddRange(body.GroundPoints);
            points.AddRange(body.Markers);

            var rigid = new RigidMesh(body.SolverMesh, body.BodyPlane, points, jointStrength);
            rigidGoals.Add(rigid);
            goals.Add(rigid);

            // Gravity acts at the body's own centroid, which is also its orientation
            // particle, so a body with no joints at all still falls rather than sitting
            // still for want of anywhere to apply the load.
            goals.Add(new Unary(body.BodyPlane.Origin, new Vector3d(0.0, 0.0, -gravity * body.Node.MassKilograms)));

            foreach (var groundPoint in body.GroundPoints)
            {
                goals.Add(new Anchor(groundPoint, anchorStrength));
                anchoredGround++;
            }
        }

        foreach (var goal in goals)
        {
            physicalSystem.AssignPIndex(goal, assignToleranceMeters);
        }

        // Marker particles sit last in each rigid goal's index list, after the joints and
        // the ground points, in the order they were added.
        for (var i = 0; i < bodies.Count; i++)
        {
            var indices = rigidGoals[i].PIndex;
            var markerStart = indices.Length - BodyMarkerCount;
            if (markerStart < 1)
            {
                throw new InvalidOperationException(
                    $"Body {bodies[i].Node.Node["g"]} did not receive its tracking markers.");
            }

            bodies[i].MarkerParticles = new[]
            {
                indices[markerStart], indices[markerStart + 1], indices[markerStart + 2]
            };
        }

        var particleCount = physicalSystem.ParticleCount();

        double MaxBodyMotion()
        {
            var positions = physicalSystem.GetPositionArray();
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

        double MaxBodyRotation()
        {
            var positions = physicalSystem.GetPositionArray();
            var worst = 0.0;
            foreach (var body in bodies)
            {
                var p0 = body.MarkerParticles[0];
                var p1 = body.MarkerParticles[1];
                var p2 = body.MarkerParticles[2];
                if (p0 >= positions.Length || p1 >= positions.Length || p2 >= positions.Length)
                {
                    continue;
                }

                var now = new Plane(positions[p0], positions[p1], positions[p2]);
                if (!now.IsValid)
                {
                    continue;
                }

                worst = Math.Max(worst, RotationDegreesFromTransform(
                    Transform.PlaneToPlane(body.InitialMarkerPlane, now)));
            }

            return worst;
        }

        var motionSamples = new List<double>();
        var rotationSamples = new List<double>();
        var sampleInterval = Math.Clamp(currentStep / MotionSampleCount, 1, MaxSampleInterval);
        var divergingSampleRun = 0;
        var stepsRun = 0;

        for (var step = 0; step < currentStep; step++)
        {
            for (var subStep = 0; subStep < solverSubsteps; subStep++)
            {
                physicalSystem.Step(goals, true, solverThresholdMeters);
            }

            stepsRun = step + 1;
            if (stepsRun % sampleInterval != 0 && stepsRun != currentStep)
            {
                continue;
            }

            motionSamples.Add(MaxBodyMotion());
            rotationSamples.Add(MaxBodyRotation());

            if (motionSamples.Count >= MinSettledSamples)
            {
                var settled = true;
                for (var back = 0; back < 3; back++)
                {
                    var index = motionSamples.Count - 1 - back;
                    var delta = Math.Abs(motionSamples[index] - motionSamples[index - 1]);
                    if (delta > SettledEpsilonMeters * (1.0 + motionSamples[index]))
                    {
                        settled = false;
                        break;
                    }
                }

                if (settled && IsGrowingRotation(rotationSamples))
                {
                    settled = false;
                }

                if (settled)
                {
                    break;
                }

                if (IsDivergingMotion(motionSamples) && IsGrowingRotation(rotationSamples))
                {
                    divergingSampleRun++;
                    if (divergingSampleRun >= DivergingSamplesToExit)
                    {
                        break;
                    }
                }
                else
                {
                    divergingSampleRun = 0;
                }
            }
        }

        var finalPositions = physicalSystem.GetPositionArray();
        var nodeReports = new JArray();
        var worstDisplacement = 0.0;
        var worstRotation = 0.0;
        string worstNode = null;

        foreach (var body in bodies)
        {
            var p0 = body.MarkerParticles[0];
            var p1 = body.MarkerParticles[1];
            var p2 = body.MarkerParticles[2];
            var displacement = 0.0;
            var rotationDegrees = 0.0;

            if (p0 < finalPositions.Length && p1 < finalPositions.Length && p2 < finalPositions.Length)
            {
                var now = new Plane(finalPositions[p0], finalPositions[p1], finalPositions[p2]);
                if (now.IsValid)
                {
                    var xform = Transform.PlaneToPlane(body.InitialMarkerPlane, now);
                    rotationDegrees = RotationDegreesFromTransform(xform);
                    var moved = new Point3d(body.Centroid);
                    moved.Transform(xform);
                    displacement = moved.DistanceTo(body.Centroid);

                    // Without this the display draws the model exactly as it was built. The
                    // contact path picks the transform up from ReportBodies; this loop is
                    // the pinned mode's own report and has to record it too.
                    body.DocumentTransform =
                        StabilityUnits.SolverTransformToDocument(xform, lengthToMeters);
                }
            }

            if (displacement > worstDisplacement)
            {
                worstDisplacement = displacement;
                worstNode = body.Node.Node["g"]?.ToString();
            }

            worstRotation = Math.Max(worstRotation, rotationDegrees);
            body.Node.Node["pinned_displacement_m"] = displacement;
            body.Node.Node["pinned_rotation_deg"] = rotationDegrees;
            body.Node.Node["joint_count"] = body.JointCount;

            nodeReports.Add(new JObject
            {
                ["g"] = body.Node.Node["g"],
                ["joints"] = body.JointCount,
                ["ground_points"] = body.GroundPoints.Count,
                ["displacement_m"] = displacement,
                ["rotation_deg"] = rotationDegrees
            });
        }

        var diverging = IsDivergingMotion(motionSamples);
        var turning = IsGrowingRotation(rotationSamples);

        // A pinned assembly that holds still is kinematically determinate. One that keeps
        // moving has a mechanism in it, and the body that moved furthest is where to look.
        var isMechanism = diverging || turning ||
            worstRotation > DefaultRotationThresholdDegrees ||
            worstDisplacement > PinnedMechanismDisplacementMeters;

        var widest = 0.0;
        foreach (var entry in clusterReport)
        {
            widest = Math.Max(widest, entry["diameter_m"]?.Value<double>() ?? 0.0);
        }

        graph["evaluation_mode"] = PinnedEvaluationMode;
        graph["body_count"] = bodies.Count;
        graph["particle_count"] = particleCount;
        graph["joint_count"] = bodies.Sum(b => b.JointCount) / 2;
        graph["node_count_clustered"] = clusterReport.Count;
        graph["node_widest_m"] = widest;
        graph["nodes"] = clusterReport;
        graph["anchored_ground_points"] = anchoredGround;
        graph["motion_trend"] = diverging ? "diverging" : "settling";
        graph["rotation_trend"] = turning ? "turning" : "steady";
        graph["motion_samples_m"] = new JArray(motionSamples.Select(v => (object)v).ToArray());
        graph["rotation_samples_deg"] = new JArray(rotationSamples.Select(v => (object)v).ToArray());
        graph["solver_steps_run"] = stepsRun;
        graph["max_body_displacement_m"] = worstDisplacement;
        graph["max_body_rotation_deg"] = worstRotation;
        graph["worst_body"] = worstNode;
        graph["bodies"] = nodeReports;
        graph["stable"] = !isMechanism;

        // The same cache the contact mode writes. Without it a pinned run reports numbers
        // and draws nothing, whether it was asked for from the command or over MCP.
        if (displayDoc != null)
        {
            ClearAfterEvaluationCache(displayDoc);
            WriteMultiBodyDisplay(displayDoc, bodies);
            global::RhinoMCPModPlugin.MCPStabilityController.SetEnabled(true);
        }

        return !isMechanism;
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
            ["mechanism_displacement_threshold_m"] = PinnedMechanismDisplacementMeters,
            ["unit_warnings"] = unitWarnings,
            ["evaluation_graph_key"] = EvaluationGraphKey
        };
    }

    /// <summary>
    /// The bearing surface between two elements, as a rectangle of push-only points.
    /// </summary>
    /// <remarks>
    /// Derived from the overlap of the two bodies' bounding boxes. The thinnest axis of
    /// that overlap is the contact normal - two elements resting on one another overlap by
    /// almost nothing across their shared face and by a lot in the plane of it - and the
    /// other two axes give the rectangle that carries the load. This is exact for the
    /// box-shaped elements in a reuse catalogue and approximate for anything else, which is
    /// why a patch that comes out degenerate falls back to the single contact point the
    /// graph already found rather than inventing a bearing area.
    /// </remarks>
    private static bool TryBuildContactPatch(
        Mesh meshA,
        Mesh meshB,
        Point3d graphContact,
        double fallbackAreaM2,
        out List<Point3d> points,
        out List<double> areas,
        out Vector3d normal)
    {
        points = new List<Point3d>();
        areas = new List<double>();
        normal = Vector3d.ZAxis;

        var boxA = meshA.GetBoundingBox(true);
        var boxB = meshB.GetBoundingBox(true);
        if (!boxA.IsValid || !boxB.IsValid)
        {
            return false;
        }

        var min = new Point3d(
            Math.Max(boxA.Min.X, boxB.Min.X),
            Math.Max(boxA.Min.Y, boxB.Min.Y),
            Math.Max(boxA.Min.Z, boxB.Min.Z));
        var max = new Point3d(
            Math.Min(boxA.Max.X, boxB.Max.X),
            Math.Min(boxA.Max.Y, boxB.Max.Y),
            Math.Min(boxA.Max.Z, boxB.Max.Z));

        var span = new[] { max.X - min.X, max.Y - min.Y, max.Z - min.Z };
        var thin = 0;
        for (var axis = 1; axis < 3; axis++)
        {
            if (span[axis] < span[thin])
            {
                thin = axis;
            }
        }

        var u = (thin + 1) % 3;
        var v = (thin + 2) % 3;
        if (span[u] <= 0.0 || span[v] <= 0.0)
        {
            // The boxes do not overlap in the plane of the face: fall back to the graph's
            // own contact point, with a nominal area, so the joint still exists.
            points.Add(graphContact);
            areas.Add(fallbackAreaM2);
            normal = ContactNormalFromCentres(boxA.Center, boxB.Center);
            return true;
        }

        normal = Vector3d.Zero;
        switch (thin)
        {
            case 0: normal = Vector3d.XAxis; break;
            case 1: normal = Vector3d.YAxis; break;
            default: normal = Vector3d.ZAxis; break;
        }

        // Point the normal from A into B so that a positive gap always means separation.
        if ((boxB.Center - boxA.Center) * normal < 0.0)
        {
            normal = -normal;
        }

        var mid = new[] { (min.X + max.X) * 0.5, (min.Y + max.Y) * 0.5, (min.Z + max.Z) * 0.5 };
        var lo = new[] { min.X, min.Y, min.Z };
        var hi = new[] { max.X, max.Y, max.Z };
        var area = span[u] * span[v] * 0.25;

        foreach (var (uu, vv) in new[] { (false, false), (true, false), (true, true), (false, true) })
        {
            var coords = new double[3];
            coords[thin] = mid[thin];
            coords[u] = uu ? hi[u] : lo[u];
            coords[v] = vv ? hi[v] : lo[v];
            points.Add(new Point3d(coords[0], coords[1], coords[2]));
            areas.Add(area);
        }

        return true;
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
    /// Solves the assembly as rigid bodies resting on one another across bearing surfaces.
    /// </summary>
    /// <remarks>
    /// This is the mode that answers the question the other two cannot. Welded makes the
    /// assembly one body and only sees it tip over; pinned glues every joint in tension and
    /// only sees mechanisms. Here each contact carries compression and no tension, so an
    /// element can rotate off its support, lift, or stay put according to where the load
    /// falls on its bearing surface.
    /// </remarks>
    private static bool SolveContactFromGraph(
        JObject graph,
        List<StabilityNode> nodes,
        int currentStep,
        double contactStrength,
        bool contactStrengthIsAuto,
        double groundStrength,
        bool groundStrengthIsAuto,
        double jointPenetrationMeters,
        double groundSettlementMeters,
        double torqueGain,
        double bodyStrength,
        bool bodyStrengthIsAuto,
        double floorZMeters,
        double gravity,
        double assignToleranceMeters,
        double solverThresholdMeters,
        int solverSubsteps,
        double lengthToMeters,
        RhinoDoc displayDoc)
    {
        var bodies = BuildPinnedBodies(
            graph, nodes, lengthToMeters, floorZMeters, GroundContactToleranceMeters, sharePins: false);
        if (bodies.Count == 0)
        {
            throw new InvalidOperationException("No bodies were built for the contact solver.");
        }

        var physicalSystem = new PhysicalSystem();
        var goals = new List<IGoal>();
        var rigidGoals = new List<RigidMesh>(bodies.Count);

        // The bearing surfaces are built before any stiffness is chosen, because in the
        // automatic mode the stiffness follows from the load each surface carries and the
        // load follows from the topology.
        var specs = new List<PatchSpec>();
        if (graph["e"] is JArray edges)
        {
            foreach (var edgeToken in edges)
            {
                if (edgeToken is not JArray edge || edge.Count < 5)
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

                if (!TryBuildContactPatch(
                        bodies[a].SolverMesh, bodies[b].SolverMesh, contact,
                        FallbackContactAreaM2, out var points, out var areas, out var normal))
                {
                    continue;
                }

                specs.Add(new PatchSpec
                {
                    A = a,
                    B = b,
                    Contact = contact,
                    Points = points,
                    Areas = areas,
                    Normal = normal
                });
            }
        }

        var loads = TributaryLoads(bodies, specs, gravity);

        // Every stiffness in the model, expressed as a total weight per joint rather than
        // as a modulus, so that the ratio to gravity is fixed instead of the magnitude.
        var patchWeights = new double[specs.Count];
        for (var i = 0; i < specs.Count; i++)
        {
            var areaSum = specs[i].Areas.Sum();
            patchWeights[i] = contactStrengthIsAuto
                ? Math.Max(loads.Patch[i], MinimumJointLoadNewtons) / jointPenetrationMeters
                : contactStrength * areaSum;
        }

        var groundWeights = new double[bodies.Count];
        for (var i = 0; i < bodies.Count; i++)
        {
            if (bodies[i].GroundPoints.Count == 0)
            {
                continue;
            }

            groundWeights[i] = groundStrengthIsAuto
                ? Math.Max(loads.Ground[i], MinimumJointLoadNewtons) / groundSettlementMeters
                : groundStrength * GroundPatchAreaPerPoint(bodies[i]) * bodies[i].GroundPoints.Count;
        }

        if (contactStrengthIsAuto && groundStrengthIsAuto)
        {
            NormaliseBodyWeights(bodies, specs, loads, jointPenetrationMeters, patchWeights, groundWeights);
        }

        var stiffestJoint = patchWeights.Length > 0 ? patchWeights.Max() : 0.0;
        for (var i = 0; i < bodies.Count; i++)
        {
            stiffestJoint = Math.Max(stiffestJoint, groundWeights[i]);
        }

        // A body has to be rigid against whatever holds it, so in the automatic mode the
        // rigid goal is sized from the stiffest joint rather than from an absolute.
        var effectiveBodyStrength = bodyStrengthIsAuto && stiffestJoint > 0.0
            ? stiffestJoint * AutoBodyStiffnessRatio
            : bodyStrength;

        foreach (var body in bodies)
        {
            AssignBodyMarkers(body);
            var points = new List<Point3d>(body.GroundPoints);
            points.AddRange(body.Markers);
            var rigid = new RigidMesh(body.SolverMesh, body.BodyPlane, points, effectiveBodyStrength);
            rigidGoals.Add(rigid);
            goals.Add(rigid);
            goals.Add(new Unary(
                body.BodyPlane.Origin, new Vector3d(0.0, 0.0, -gravity * body.Node.MassKilograms)));
        }

        // Ground bearing, per body. This is a separate knob from the joints on purpose: the
        // ground is a soil, the joints are dry masonry, and tying them to one number made a
        // ten-block tower with 157 mm of support margin rock as one piece and read as
        // failed - the ground going soft, not its joints.
        var groundSites = 0;
        for (var i = 0; i < bodies.Count; i++)
        {
            var body = bodies[i];
            if (body.GroundPoints.Count == 0)
            {
                continue;
            }

            var perPoint = groundWeights[i] / body.GroundPoints.Count;
            var strengths = new List<double>();
            foreach (var _ in body.GroundPoints)
            {
                strengths.Add(perPoint);
            }

            goals.Add(new AreaFloor(new List<Point3d>(body.GroundPoints), strengths, floorZMeters));
            groundSites += body.GroundPoints.Count;
        }

        // One bearing surface per graph edge, at the stiffness its own load earned it. The
        // goal multiplies each point by its area, so the total weight is spread over the
        // patch in proportion to area exactly as before.
        var patches = new List<ContactPatch>();
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var areaSum = spec.Areas.Sum();
            var strength = areaSum > 0.0 ? patchWeights[i] / areaSum : patchWeights[i];
            var patch = new ContactPatch(
                bodies[spec.A].BodyPlane, bodies[spec.B].BodyPlane, spec.Points, spec.Areas,
                spec.Normal, strength, DefaultContactFriction, torqueGain);
            patches.Add(patch);
            goals.Add(patch);
            bodies[spec.A].JointPoints.Add(spec.Contact);
            bodies[spec.B].JointPoints.Add(spec.Contact);
        }

        foreach (var goal in goals)
        {
            physicalSystem.AssignPIndex(goal, assignToleranceMeters);
        }

        for (var i = 0; i < bodies.Count; i++)
        {
            var indices = rigidGoals[i].PIndex;
            var markerStart = indices.Length - BodyMarkerCount;
            if (markerStart < 1)
            {
                throw new InvalidOperationException(
                    $"Body {bodies[i].Node.Node["g"]} did not receive its tracking markers.");
            }

            bodies[i].MarkerParticles = new[]
            {
                indices[markerStart], indices[markerStart + 1], indices[markerStart + 2]
            };
        }

        var particleCount = physicalSystem.ParticleCount();
        var motionSamples = new List<double>();
        var rotationSamples = new List<double>();
        var sampleInterval = Math.Clamp(currentStep / MotionSampleCount, 1, MaxSampleInterval);
        var divergingSampleRun = 0;
        var stepsRun = 0;

        for (var step = 0; step < currentStep; step++)
        {
            for (var subStep = 0; subStep < solverSubsteps; subStep++)
            {
                physicalSystem.Step(goals, true, solverThresholdMeters);
            }

            stepsRun = step + 1;
            if (stepsRun % sampleInterval != 0 && stepsRun != currentStep)
            {
                continue;
            }

            motionSamples.Add(MaxMarkerMotion(physicalSystem, bodies));
            rotationSamples.Add(MaxMarkerRotation(physicalSystem, bodies));

            if (motionSamples.Count >= MinSettledSamples)
            {
                var settled = true;
                for (var back = 0; back < 3; back++)
                {
                    var index = motionSamples.Count - 1 - back;
                    if (Math.Abs(motionSamples[index] - motionSamples[index - 1]) >
                        SettledEpsilonMeters * (1.0 + motionSamples[index]))
                    {
                        settled = false;
                        break;
                    }
                }

                if (settled && IsGrowingRotation(rotationSamples))
                {
                    settled = false;
                }

                if (settled)
                {
                    break;
                }

                if (IsDivergingMotion(motionSamples) && IsGrowingRotation(rotationSamples))
                {
                    divergingSampleRun++;
                    if (divergingSampleRun >= DivergingSamplesToExit)
                    {
                        break;
                    }
                }
                else
                {
                    divergingSampleRun = 0;
                }
            }
        }

        var report = ReportBodies(physicalSystem, bodies, graph, lengthToMeters);
        var openJoints = 0;
        var contactReport = new JArray();
        for (var i = 0; i < patches.Count; i++)
        {
            var patch = patches[i];
            if (patch.ActivePoints == 0)
            {
                openJoints++;
            }

            // Where the compression sits on the patch is the one number that separates a
            // joint carrying its load honestly from one quietly resisting a moment it
            // cannot: statics says the resultant of everything above must land inside the
            // patch, and if the solver reports it inside while the geometry puts it
            // outside, the patch is inventing restraint.
            var spec = specs[i];
            contactReport.Add(new JObject
            {
                ["a"] = spec.A,
                ["b"] = spec.B,
                ["points"] = patch.PointCount,
                ["active_points"] = patch.ActivePoints,
                ["area_m2"] = spec.Areas.Sum(),
                ["weight_n_per_m"] = patchWeights[i],
                ["compression_n"] = patch.Compression,
                ["normal"] = new JArray(spec.Normal.X, spec.Normal.Y, spec.Normal.Z),
                ["graph_contact_m"] = new JArray(
                    spec.Contact.X, spec.Contact.Y, spec.Contact.Z),
                ["patch_centre_m"] = patch.Centre.IsValid
                    ? new JArray(patch.Centre.X, patch.Centre.Y, patch.Centre.Z)
                    : null,
                ["resultant_m"] = patch.Resultant.IsValid
                    ? new JArray(patch.Resultant.X, patch.Resultant.Y, patch.Resultant.Z)
                    : null,
                ["patch_corners_m"] = new JArray(spec.Points
                    .Select(pt => (object)new JArray(pt.X, pt.Y, pt.Z))
                    .ToArray())
            });
        }

        graph["contacts"] = contactReport;

        var diverging = IsDivergingMotion(motionSamples);
        var turning = IsGrowingRotation(rotationSamples);
        var failed = diverging || turning ||
            report.WorstRotation > DefaultRotationThresholdDegrees ||
            report.WorstDisplacement > PinnedMechanismDisplacementMeters;

        graph["evaluation_mode"] = ContactEvaluationMode;
        graph["body_count"] = bodies.Count;
        graph["particle_count"] = particleCount;
        graph["contact_count"] = patches.Count;
        graph["open_contacts"] = openJoints;
        graph["ground_contact_points"] = groundSites;
        graph["joint_weight_min_n_per_m"] = patchWeights.Length > 0 ? patchWeights.Min() : 0.0;
        graph["joint_weight_max_n_per_m"] = stiffestJoint;
        graph["torque_gain"] = torqueGain;
        graph["motion_trend"] = diverging ? "diverging" : "settling";
        graph["rotation_trend"] = turning ? "turning" : "steady";
        graph["motion_samples_m"] = new JArray(motionSamples.Select(v => (object)v).ToArray());
        graph["rotation_samples_deg"] = new JArray(rotationSamples.Select(v => (object)v).ToArray());
        graph["solver_steps_run"] = stepsRun;
        graph["max_body_displacement_m"] = report.WorstDisplacement;
        graph["max_body_rotation_deg"] = report.WorstRotation;
        graph["worst_body"] = report.WorstNode;
        graph["bodies"] = report.Nodes;
        graph["stable"] = !failed;

        if (displayDoc != null)
        {
            ClearAfterEvaluationCache(displayDoc);
            WriteMultiBodyDisplay(displayDoc, bodies);
            // Writing the settled geometry is not enough on its own: the conduit that draws
            // it has to be switched on, which the welded path does separately.
            global::RhinoMCPModPlugin.MCPStabilityController.SetEnabled(true);
        }

        return !failed;
    }

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
    public const double DefaultContactStrength = 1e10;

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
    public const double DefaultTorqueGain = 0.25;

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
