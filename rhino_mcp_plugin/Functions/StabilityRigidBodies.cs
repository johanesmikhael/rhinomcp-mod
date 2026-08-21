using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace RhinoMCPModPlugin.Functions;

/// <summary>
/// Newtonian dynamics with the body, rather than the particle, as the primitive.
/// </summary>
/// <remarks>
/// The particle integrator could not make anything fall. A body was a handful of particles
/// held to a fitted frame by a stiff penalty, and that frame was a *measurement* of where
/// the particles had ended up rather than something integrated. The particles could leave
/// it by only mg/k - about 1.5 micron at 3.6e8 N/m - and the frame then followed at a
/// quarter of that per step, so a body with nothing holding it up descended at the solver's
/// update rate instead of at g: 2.82 mm in half a second where free fall is 1226.
///
/// Here a body is the thing being integrated. It carries mass, a centre of mass, an inertia
/// tensor taken from its own mesh, a position, an orientation, a linear velocity and an
/// angular velocity, and it obeys F = ma and tau = I alpha. Gravity acts at its centre. It
/// falls because nothing is stopping it, which is the whole point, and an element that
/// rotates off its support does so at the rate gravity dictates rather than at the rate the
/// solver happens to iterate.
///
/// The joints are unchanged in spirit: a pin is a stiff spring pulling every body that
/// meets there toward their common point, applied at the attachment rather than at the
/// centre, so it delivers a moment as well as a force. That is what lets a body rotate
/// about its pin - the freedom the pinned idealisation is supposed to have and the particle
/// model could only imitate.
/// </remarks>
internal static class StabilityRigidBodies
{
    /// <summary>One rigid body, integrated.</summary>
    internal sealed class Body
    {
        public double Mass;
        public Point3d Centre;
        public Point3d StartCentre;
        public Vector3d Velocity;
        public Vector3d AngularVelocity;

        /// <summary>Orientation, as the rotation from the body's own axes to the world.</summary>
        public Transform Rotation = Transform.Identity;
        public Transform StartRotation = Transform.Identity;

        /// <summary>Principal inertia about the centre of mass, in body axes.</summary>
        public Vector3d Inertia;

        public Vector3d Force;
        public Vector3d Torque;

        /// <summary>Attachment points, held in body axes relative to the centre of mass.</summary>
        public readonly List<Vector3d> Local = new();
        public readonly List<int> Sites = new();

        public Point3d WorldPoint(int index)
        {
            var offset = Local[index];
            offset.Transform(Rotation);
            return Centre + offset;
        }

        public Point3d StartWorldPoint(int index)
        {
            var offset = Local[index];
            offset.Transform(StartRotation);
            return StartCentre + offset;
        }

        public void Reset()
        {
            Centre = StartCentre;
            Rotation = StartRotation;
            Velocity = Vector3d.Zero;
            AngularVelocity = Vector3d.Zero;
            Force = Vector3d.Zero;
            Torque = Vector3d.Zero;
        }
    }

    /// <summary>A place where bodies meet, or where one is held to the ground.</summary>
    internal sealed class Site
    {
        public readonly List<int> Bodies = new();
        public readonly List<int> Slots = new();
        public double Stiffness;
        public bool Grounded;
        public Point3d Anchor;
    }

    /// <summary>
    /// The inertia of a body about its own centre of mass, scaled to its actual mass.
    /// </summary>
    /// <remarks>
    /// Rhino computes moments for unit density, so they carry the shape and not the mass;
    /// multiplying by mass over volume puts them in kg m^2. Products of inertia are
    /// discarded and the principal values used directly: the bodies here are prismatic
    /// members whose axes are already their principal ones, and carrying a full tensor
    /// would mean inverting it every step for a correction far below the modelling error in
    /// treating a bolted joint as a point.
    /// </remarks>
    private static Vector3d InertiaOf(Mesh mesh, double mass, out Point3d centre)
    {
        centre = Point3d.Origin;
        if (mesh == null)
        {
            return new Vector3d(mass, mass, mass);
        }

        var properties = VolumeMassProperties.Compute(mesh);
        if (properties == null || !(properties.Volume > 0.0))
        {
            var box = mesh.GetBoundingBox(true);
            centre = box.Center;
            var d = box.Diagonal;
            // Fall back to a solid box of the same extent.
            return new Vector3d(
                mass * (d.Y * d.Y + d.Z * d.Z) / 12.0,
                mass * (d.X * d.X + d.Z * d.Z) / 12.0,
                mass * (d.X * d.X + d.Y * d.Y) / 12.0);
        }

        centre = properties.Centroid;
        var scale = mass / properties.Volume;
        var moments = properties.CentroidCoordinatesMomentsOfInertia;
        var inertia = new Vector3d(moments.X * scale, moments.Y * scale, moments.Z * scale);

        // A degenerate axis would divide by nothing when the angular update is applied.
        var floor = 1e-9 * Math.Max(mass, 1e-9);
        return new Vector3d(
            Math.Max(inertia.X, floor),
            Math.Max(inertia.Y, floor),
            Math.Max(inertia.Z, floor));
    }

    internal static Body Create(Mesh mesh, double mass, IEnumerable<Point3d> attachments)
    {
        var inertia = InertiaOf(mesh, mass, out var centre);
        var body = new Body
        {
            Mass = mass,
            Centre = centre,
            StartCentre = centre,
            Inertia = inertia
        };

        foreach (var point in attachments)
        {
            body.Local.Add(point - centre);
        }

        return body;
    }

    /// <summary>
    /// The step size, from the fastest rotation any body can be driven into.
    /// </summary>
    /// <remarks>
    /// Explicit integration diverges above dt = 2/omega. The linear limit is the familiar
    /// sqrt(k/m), but a pin near a body's centre is stiffer in rotation than in
    /// translation - the same force acts on a smaller lever against a small moment of
    /// inertia - so the rotational limit governs and both are taken.
    /// </remarks>
    internal static double Timestep(List<Body> bodies, List<Site> sites, double safety)
    {
        var fastest = 0.0;
        foreach (var site in sites)
        {
            for (var i = 0; i < site.Bodies.Count; i++)
            {
                var body = bodies[site.Bodies[i]];
                if (!(body.Mass > 0.0))
                {
                    continue;
                }

                fastest = Math.Max(fastest, Math.Sqrt(site.Stiffness / body.Mass));

                var lever = body.Local[site.Slots[i]].Length;
                var smallest = Math.Min(body.Inertia.X, Math.Min(body.Inertia.Y, body.Inertia.Z));
                if (smallest > 0.0 && lever > 0.0)
                {
                    fastest = Math.Max(fastest, Math.Sqrt(site.Stiffness * lever * lever / smallest));
                }
            }
        }

        return fastest > 0.0 ? safety * 2.0 / fastest : 1e-4;
    }

    internal static StabilityDynamics.Result Run(
        List<Body> bodies,
        List<Site> sites,
        Vector3d gravity,
        double durationSeconds,
        double timestep,
        double dampingRatio,
        double joltSpeed,
        double settledSpeed,
        bool kineticDamping,
        int sampleCount,
        Func<double> measure,
        Func<double, bool> stopEarly)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(durationSeconds / timestep));
        var result = new StabilityDynamics.Result
        {
            TimestepSeconds = timestep,
            DampingRatio = dampingRatio
        };

        foreach (var body in bodies)
        {
            body.Reset();
        }

        // The same stress-free disturbance the particle integrator used, applied to whole
        // bodies because whole bodies are now what move.
        if (joltSpeed > 0.0)
        {
            for (var i = 0; i < bodies.Count; i++)
            {
                bodies[i].Velocity = StabilityDynamics.ImperfectionDirection(i) * joltSpeed;
            }
        }

        // How stiffly each body is held in rotation, for sizing pin friction: a joint
        // stiffness acting on the lever it has to the centre of mass.
        var spinHeld = new double[bodies.Count];
        foreach (var site in sites)
        {
            for (var i = 0; i < site.Bodies.Count; i++)
            {
                var index = site.Bodies[i];
                var lever = bodies[index].Local[site.Slots[i]].Length;
                spinHeld[index] += site.Stiffness * lever * lever;
            }
        }

        var sampleEvery = Math.Max(1, steps / Math.Max(1, sampleCount));
        var previousKinetic = 0.0;
        var lastSampled = -1.0;
        var recentDeltas = new List<double>(StabilityDynamics.ConvergenceWindow);
        var signs = new List<int>(StabilityDynamics.ConvergenceWindow);

        for (var step = 0; step < steps; step++)
        {
            foreach (var body in bodies)
            {
                body.Force = body.Mass * gravity;
                body.Torque = Vector3d.Zero;
            }

            foreach (var site in sites)
            {
                // Where the bodies meeting here agree the joint is. A pin carries force in
                // three directions and no moment of its own; the moment on each body comes
                // from that force acting at the attachment rather than at the centre.
                var target = Point3d.Origin;
                if (site.Grounded)
                {
                    target = site.Anchor;
                }
                else
                {
                    var sum = Vector3d.Zero;
                    for (var i = 0; i < site.Bodies.Count; i++)
                    {
                        sum += (Vector3d)bodies[site.Bodies[i]].WorldPoint(site.Slots[i]);
                    }

                    target = new Point3d(sum / site.Bodies.Count);
                }

                // Damping belongs to the joint, not to the body.
                //
                // Applied to a body's absolute velocity it behaves as air drag: it resists
                // free fall and gives the assembly a terminal velocity of mg/c. Two members
                // dropped in mid-air then descended at 0.095 m/s and covered 10.5 mm where
                // gravity asks for 76.6 - the free-body defect returning through the damping
                // term after the integrator had been fixed.
                //
                // Real structural damping is internal: it dissipates when parts of the
                // structure move relative to each other, which is why it is normally taken
                // proportional to stiffness rather than to mass. Here it opposes the
                // velocity of each attachment relative to the joint's own, so a body falling
                // freely - nothing moving relative to anything - loses nothing at all, while
                // a structure vibrating against its joints still damps out.
                var jointVelocity = Vector3d.Zero;
                if (!site.Grounded)
                {
                    for (var i = 0; i < site.Bodies.Count; i++)
                    {
                        var body = bodies[site.Bodies[i]];
                        var arm = body.WorldPoint(site.Slots[i]) - body.Centre;
                        jointVelocity += body.Velocity + Vector3d.CrossProduct(body.AngularVelocity, arm);
                    }

                    jointVelocity /= site.Bodies.Count;
                }

                for (var i = 0; i < site.Bodies.Count; i++)
                {
                    var body = bodies[site.Bodies[i]];
                    var here = body.WorldPoint(site.Slots[i]);
                    var arm = here - body.Centre;
                    var pull = site.Stiffness * (target - here);

                    if (dampingRatio > 0.0 && body.Mass > 0.0)
                    {
                        var pointVelocity = body.Velocity + Vector3d.CrossProduct(body.AngularVelocity, arm);
                        var slip = jointVelocity - pointVelocity;
                        pull += 2.0 * dampingRatio * Math.Sqrt(site.Stiffness * body.Mass) * slip;
                    }

                    body.Force += pull;
                    body.Torque += Vector3d.CrossProduct(arm, pull);
                }
            }

            var peakSpeed = 0.0;
            var kinetic = 0.0;
            foreach (var body in bodies)
            {
                if (!(body.Mass > 0.0))
                {
                    continue;
                }

                body.Velocity += body.Force / body.Mass * timestep;

                // Euler's equations, with the gyroscopic term. Bodies here turn slowly
                // enough that it is small, but leaving it out is a different equation.
                var spinStiffness = spinHeld[bodies.IndexOf(body)];
                var inertiaWorld = body.Inertia;
                var spin = body.AngularVelocity;
                var gyroscopic = new Vector3d(
                    (inertiaWorld.Z - inertiaWorld.Y) * spin.Y * spin.Z,
                    (inertiaWorld.X - inertiaWorld.Z) * spin.Z * spin.X,
                    (inertiaWorld.Y - inertiaWorld.X) * spin.X * spin.Y);
                // Friction at the pin.
                //
                // A member pinned at two points can spin about the axis through them, and
                // that freedom has no stiffness at all - the pinned idealisation grants it
                // deliberately. Joint damping cannot reach it either: a body spinning about
                // that axis has zero velocity at the very points where the damping acts. So
                // the spin, once started, never stops, the assembly's kinetic energy never
                // turns over cleanly, and the runs that settle it by kinetic damping never
                // settle.
                //
                // A real pin has friction. This is that, sized against the body's own
                // rotational scale so it is a fraction of critical for the spin rather than
                // an arbitrary number, and it acts only on rotation - it cannot resist a
                // body falling or translating.
                var spinDamping = 2.0 * dampingRatio * Math.Sqrt(
                    Math.Max(spinStiffness, 0.0) * Math.Min(inertiaWorld.X,
                        Math.Min(inertiaWorld.Y, inertiaWorld.Z)));
                var torque = body.Torque - gyroscopic - spinDamping * body.AngularVelocity;

                body.AngularVelocity += new Vector3d(
                    torque.X / inertiaWorld.X,
                    torque.Y / inertiaWorld.Y,
                    torque.Z / inertiaWorld.Z) * timestep;

                body.Centre += body.Velocity * timestep;

                var turn = body.AngularVelocity * timestep;
                if (turn.Length > 0.0)
                {
                    var spinStep = Transform.Rotation(turn.Length, turn, Point3d.Origin);
                    body.Rotation = spinStep * body.Rotation;
                }

                peakSpeed = Math.Max(peakSpeed, body.Velocity.Length);
                // Rotation counts. These members are pinned at both ends and much of their
                // energy is angular, so a kinetic energy built from linear velocity alone
                // turns over at the wrong moments - and kinetic damping, which acts on
                // exactly those turnovers, then never settles the assembly.
                var w = body.AngularVelocity;
                kinetic += body.Mass * body.Velocity.SquareLength +
                    body.Inertia.X * w.X * w.X +
                    body.Inertia.Y * w.Y * w.Y +
                    body.Inertia.Z * w.Z * w.Z;
            }

            result.PeakSpeed = Math.Max(result.PeakSpeed, peakSpeed);
            result.Steps = step + 1;
            result.SimulatedSeconds = result.Steps * timestep;

            if (kineticDamping)
            {
                if (kinetic < previousKinetic)
                {
                    foreach (var body in bodies)
                    {
                        body.Velocity = Vector3d.Zero;
                        body.AngularVelocity = Vector3d.Zero;
                    }

                    previousKinetic = 0.0;
                    if (peakSpeed < settledSpeed)
                    {
                        result.Settled = true;
                        result.DisplacementSamples.Add(measure());
                        result.TimeSamples.Add(result.SimulatedSeconds);
                        result.SpeedSamples.Add(peakSpeed);
                        break;
                    }
                }
                else
                {
                    previousKinetic = kinetic;
                }

                continue;
            }

            var uniform = (step + 1) % sampleEvery == 0;
            if (!uniform && step + 1 != steps)
            {
                continue;
            }

            var displacement = measure();
            result.TimeSamples.Add(result.SimulatedSeconds);
            result.DisplacementSamples.Add(displacement);
            result.SpeedSamples.Add(peakSpeed);

            if (stopEarly != null && stopEarly(displacement))
            {
                break;
            }

            if (peakSpeed < settledSpeed)
            {
                result.Settled = true;
                break;
            }

            if (lastSampled >= 0.0 && uniform)
            {
                var change = displacement - lastSampled;
                recentDeltas.Add(Math.Abs(change));
                signs.Add(Math.Sign(change));
                if (recentDeltas.Count > StabilityDynamics.ConvergenceWindow)
                {
                    recentDeltas.RemoveAt(0);
                    signs.RemoveAt(0);
                }

                // Every step across the window must go the same way. A ringing structure
                // produces increments that alternate in sign while shrinking on average,
                // and reading a decay ratio from that projected 41.7 mm of sag where the
                // truth was nearer 1. Approaching a limit means moving toward it, not
                // oscillating about it with a slowly falling amplitude.
                var monotone = signs.Count == StabilityDynamics.ConvergenceWindow &&
                    signs.All(sign => sign == signs[0] && sign != 0);

                if (monotone &&
                    recentDeltas.Count == StabilityDynamics.ConvergenceWindow &&
                    recentDeltas[0] > 0.0 &&
                    recentDeltas[StabilityDynamics.ConvergenceWindow - 1] > 0.0)
                {
                    var span = recentDeltas[StabilityDynamics.ConvergenceWindow - 1] / recentDeltas[0];
                    var ratio = Math.Pow(span, 1.0 / (StabilityDynamics.ConvergenceWindow - 1));
                    result.DecayRatio = ratio;
                    if (ratio < StabilityDynamics.ConvergenceDecayPerInterval)
                    {
                        result.Converged = true;
                        result.ProjectedDisplacement = displacement +
                            recentDeltas[StabilityDynamics.ConvergenceWindow - 1] * ratio / (1.0 - ratio);
                        result.DisplacementSamples.Add(result.ProjectedDisplacement);
                        result.TimeSamples.Add(result.SimulatedSeconds);
                        result.SpeedSamples.Add(peakSpeed);
                        break;
                    }
                }
            }

            if (uniform)
            {
                lastSampled = displacement;
            }
        }

        return result;
    }
}

public partial class RhinoMCPModFunctions
{
    /// <summary>
    /// The pinned assembly as rigid bodies obeying Newton's and Euler's equations.
    /// </summary>
    /// <remarks>
    /// Same bodies, same pins and same member stiffness as the particle version, with the
    /// body rather than the particle as the thing integrated. See StabilityRigidBodies for
    /// why that distinction decides whether anything can fall.
    ///
    /// One constant disappears with it. RelaxationCompensation exists because Kangaroo's
    /// RigidMesh proposes a quarter of its correction each iteration, so realising a
    /// stiffness k meant passing 4k. There is no such goal here: a pin is a spring of
    /// stiffness k and delivers exactly k times its extension.
    /// </remarks>
    private static bool SolvePinnedRigidFromGraph(
        JObject graph,
        List<StabilityNode> nodes,
        double jointStrength,
        bool jointStrengthIsAuto,
        double jointSlipMeters,
        double youngsModulus,
        double materialDensity,
        double anchorStrength,
        double floorZMeters,
        double gravity,
        double durationSeconds,
        double dampingRatio,
        double imperfectionFraction,
        double lateralLoadFraction,
        double lengthToMeters,
        RhinoDoc displayDoc)
    {
        var clusterReport = new JArray();
        var pinned = BuildPinnedBodies(
            graph, nodes, lengthToMeters, floorZMeters, GroundContactToleranceMeters,
            sharePins: true, clusterReport: clusterReport);
        if (pinned.Count == 0)
        {
            throw new InvalidOperationException("No bodies were built for the rigid-body solver.");
        }

        var carried = PinnedCarriedLoads(pinned, gravity);
        var stiffness = new double[pinned.Count];
        for (var i = 0; i < pinned.Count; i++)
        {
            // No relaxation compensation: this integrator applies the spring force directly.
            stiffness[i] = jointStrengthIsAuto
                ? MemberAxialStiffness(
                    pinned[i], youngsModulus, materialDensity, carried[i], jointSlipMeters)
                : jointStrength;
        }

        var bodies = new List<StabilityRigidBodies.Body>(pinned.Count);
        var groundSlots = new List<HashSet<int>>(pinned.Count);
        for (var i = 0; i < pinned.Count; i++)
        {
            var attachments = new List<Point3d>(pinned[i].JointPoints);
            var grounded = new HashSet<int>();
            foreach (var point in pinned[i].GroundPoints)
            {
                grounded.Add(attachments.Count);
                attachments.Add(point);
            }

            groundSlots.Add(grounded);
            bodies.Add(StabilityRigidBodies.Create(
                pinned[i].SolverMesh, pinned[i].Node.MassKilograms, attachments));
        }

        // Bodies that name the same point are pinned there. The softest member meeting at a
        // joint governs it, the way springs in series do.
        var sites = new List<StabilityRigidBodies.Site>();
        var byKey = new Dictionary<(long, long, long), int>();
        for (var b = 0; b < bodies.Count; b++)
        {
            for (var slot = 0; slot < bodies[b].Local.Count; slot++)
            {
                var point = bodies[b].StartWorldPoint(slot);
                if (!TrySiteKey(point, DefaultAssignToleranceMeters, out var key))
                {
                    continue;
                }

                if (!byKey.TryGetValue(key, out var index))
                {
                    index = sites.Count;
                    byKey[key] = index;
                    sites.Add(new StabilityRigidBodies.Site
                    {
                        Anchor = point,
                        Stiffness = double.MaxValue
                    });
                }

                var site = sites[index];
                site.Bodies.Add(b);
                site.Slots.Add(slot);
                // Two of these springs sit in series along a member - one at each end -
                // so each must be twice the member's own axial stiffness for the pair to
                // deliver EA/L end to end. The particle model did not need this: its pins
                // were shared particles, exact rather than sprung, with the compliance in
                // the body-to-frame goal instead.
                site.Stiffness = Math.Min(site.Stiffness, 2.0 * stiffness[b]);
                if (groundSlots[b].Contains(slot))
                {
                    site.Grounded = true;
                }

                bodies[b].Sites.Add(index);
            }
        }

        var anchoredGround = 0;
        foreach (var site in sites)
        {
            if (site.Grounded)
            {
                site.Stiffness = jointStrengthIsAuto
                    ? site.Stiffness * AutoBodyStiffnessRatio
                    : anchorStrength;
                anchoredGround++;
            }
        }

        var span = PinnedSpanMeters(pinned);
        var threshold = PinnedMechanismThresholdMeters(pinned);
        var imperfection = span * imperfectionFraction;
        var jolt = Math.Sqrt(2.0 * gravity * imperfection);
        var settledSpeed = threshold / Math.Max(durationSeconds, 1e-9) / 1000.0;
        var timestep = StabilityRigidBodies.Timestep(bodies, sites, StabilityDynamics.TimestepSafety);

        double Measure()
        {
            var worst = 0.0;
            foreach (var body in bodies)
            {
                for (var slot = 0; slot < body.Local.Count; slot++)
                {
                    worst = Math.Max(
                        worst, body.StartWorldPoint(slot).DistanceTo(body.WorldPoint(slot)));
                }
            }

            return worst;
        }

        var collapsed = false;
        var run = StabilityRigidBodies.Run(
            bodies, sites, new Vector3d(0.0, 0.0, -gravity), durationSeconds, timestep,
            dampingRatio, jolt, settledSpeed, false, MotionSampleCount, Measure,
            displacement =>
            {
                if (displacement > threshold)
                {
                    collapsed = true;
                }

                return collapsed;
            });

        var worstPin = run.DisplacementSamples.Count > 0 ? run.DisplacementSamples.Max() : 0.0;
        var isMechanism = worstPin > threshold;
        var conclusive = run.Settled || run.Converged || isMechanism;
        var stable = conclusive && !isMechanism;

        var sway = new JObject();
        if (lateralLoadFraction > 0.0 && !collapsed && (run.Settled || run.Converged))
        {
            StabilityRigidBodies.Run(
                bodies, sites, new Vector3d(0.0, 0.0, -gravity), durationSeconds, timestep,
                dampingRatio, 0.0, settledSpeed, true, MotionSampleCount, Measure, null);
            var settled = bodies.Select(b => b.Centre).ToArray();

            var softest = double.MaxValue;
            string softestAxis = null;
            var total = bodies.Sum(b => b.Mass) * gravity * lateralLoadFraction;
            for (var axis = 0; axis < 2; axis++)
            {
                var push = axis == 0
                    ? new Vector3d(lateralLoadFraction * gravity, 0.0, -gravity)
                    : new Vector3d(0.0, lateralLoadFraction * gravity, -gravity);
                StabilityRigidBodies.Run(
                    bodies, sites, push, durationSeconds, timestep, dampingRatio, 0.0,
                    settledSpeed, true, MotionSampleCount, Measure, null);

                var moved = 0.0;
                for (var i = 0; i < bodies.Count; i++)
                {
                    moved = Math.Max(moved, settled[i].DistanceTo(bodies[i].Centre));
                }

                var name = axis == 0 ? "x" : "y";
                var k = moved > 0.0 ? total / moved : double.PositiveInfinity;
                sway[$"sway_{name}_m"] = moved;
                sway[$"sway_stiffness_{name}_n_per_m"] =
                    double.IsInfinity(k) ? (JToken)JValue.CreateNull() : k;
                if (k < softest)
                {
                    softest = k;
                    softestAxis = name;
                }
            }

            sway["notional_load_n"] = total;
            sway["softest_direction"] = softestAxis;
        }

        for (var i = 0; i < pinned.Count; i++)
        {
            var move = Transform.Translation(bodies[i].Centre - bodies[i].StartCentre);
            var about = Transform.Translation((Vector3d)bodies[i].StartCentre) * bodies[i].Rotation *
                Transform.Translation(-(Vector3d)bodies[i].StartCentre);
            pinned[i].DocumentTransform =
                StabilityUnits.SolverTransformToDocument(move * about, lengthToMeters);
        }

        graph["evaluation_mode"] = PinnedDynamicEvaluationMode;
        graph["integrator"] = "rigid_bodies";
        graph["body_count"] = pinned.Count;
        graph["particle_count"] = sites.Count;
        graph["joint_count"] = sites.Count(s => !s.Grounded);
        graph["node_count_clustered"] = clusterReport.Count;
        graph["nodes"] = clusterReport;
        graph["anchored_ground_points"] = anchoredGround;
        graph["stable"] = stable;
        graph["verdict"] = isMechanism ? "unstable" : (conclusive ? "stable" : "inconclusive");
        graph["conclusive"] = conclusive;
        graph["settled"] = run.Settled;
        graph["converged"] = run.Converged;
        graph["decay_ratio_per_swing"] = run.DecayRatio;
        graph["projected_displacement_m"] = run.ProjectedDisplacement;
        graph["verdict_metric"] = "pin_displacement";
        graph["mechanism_threshold_m"] = threshold;
        graph["span_m"] = span;
        graph["max_pin_displacement_m"] = worstPin;
        graph["timestep_s"] = timestep;
        graph["steps_run"] = run.Steps;
        graph["simulated_seconds"] = run.SimulatedSeconds;
        graph["duration_requested_s"] = durationSeconds;
        graph["damping_ratio"] = dampingRatio;
        graph["imperfection_m"] = imperfection;
        graph["imperfection_speed_m_s"] = jolt;
        graph["lateral_load_fraction"] = lateralLoadFraction;
        graph["peak_speed_m_s"] = run.PeakSpeed;
        graph["total_weight_n"] = bodies.Sum(b => b.Mass) * gravity;
        graph["time_samples_s"] = new JArray(run.TimeSamples.Select(v => (object)v).ToArray());
        graph["motion_samples_m"] = new JArray(run.DisplacementSamples.Select(v => (object)v).ToArray());
        graph["speed_samples_m_s"] = new JArray(run.SpeedSamples.Select(v => (object)v).ToArray());
        graph["member_stiffness_min_n_per_m"] = stiffness.Length > 0 ? stiffness.Min() : 0.0;
        graph["member_stiffness_max_n_per_m"] = stiffness.Length > 0 ? stiffness.Max() : 0.0;
        graph["sway"] = sway;

        if (displayDoc != null)
        {
            ClearAfterEvaluationCache(displayDoc);
            WriteMultiBodyDisplay(displayDoc, pinned);
            global::RhinoMCPModPlugin.MCPStabilityController.SetEnabled(true);
        }

        return stable;
    }
}
