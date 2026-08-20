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
        bool sharePins = true)
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

                bodies[a].JointPoints.Add(contact);
                bodies[b].JointPoints.Add(contact);
            }
        }

        return bodies;
    }

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
        double lengthToMeters)
    {
        var bodies = BuildPinnedBodies(
            graph, nodes, lengthToMeters, floorZMeters, GroundContactToleranceMeters);
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

        graph["evaluation_mode"] = PinnedEvaluationMode;
        graph["body_count"] = bodies.Count;
        graph["particle_count"] = particleCount;
        graph["joint_count"] = bodies.Sum(b => b.JointCount) / 2;
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
        double bodyStrength,
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

        foreach (var body in bodies)
        {
            AssignBodyMarkers(body);
            var points = new List<Point3d>(body.GroundPoints);
            points.AddRange(body.Markers);
            var rigid = new RigidMesh(body.SolverMesh, body.BodyPlane, points, bodyStrength);
            rigidGoals.Add(rigid);
            goals.Add(rigid);
            goals.Add(new Unary(
                body.BodyPlane.Origin, new Vector3d(0.0, 0.0, -gravity * body.Node.MassKilograms)));
        }

        // Ground bearing, per body, reusing the same area-weighted contact the welded mode
        // uses against the floor.
        var groundSites = 0;
        foreach (var body in bodies)
        {
            if (body.GroundPoints.Count == 0)
            {
                continue;
            }

            var strengths = new List<double>();
            var boxArea = GroundPatchAreaPerPoint(body);
            foreach (var _ in body.GroundPoints)
            {
                strengths.Add(contactStrength * boxArea);
            }

            goals.Add(new AreaFloor(new List<Point3d>(body.GroundPoints), strengths, floorZMeters));
            groundSites += body.GroundPoints.Count;
        }

        // One bearing surface per graph edge.
        var patches = new List<ContactPatch>();
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

                var patch = new ContactPatch(
                    bodies[a].BodyPlane, bodies[b].BodyPlane, points, areas, normal,
                    contactStrength, DefaultContactFriction);
                patches.Add(patch);
                goals.Add(patch);
                bodies[a].JointPoints.Add(contact);
                bodies[b].JointPoints.Add(contact);
            }
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
        foreach (var patch in patches)
        {
            if (patch.ActivePoints == 0)
            {
                openJoints++;
            }
        }

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
