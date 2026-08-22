using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;
using RhinoMCPModPlugin.Functions;

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
    /// <summary>
    /// Fraction of the explicit stability limit this integrator uses.
    /// </summary>
    /// <remarks>
    /// This was 0.025, set when an assembly at a tenth of the limit never settled: the
    /// response held its amplitude after 896k steps and damping saturated, which read as the
    /// step returning as much energy as the damping removed. That reading was wrong. The
    /// energy was coming from a dashpot sized per body rather than per joint, so the forces
    /// at the two ends of a pin were not equal and opposite - see SiteDamping. With that
    /// fixed, and with the joint stiffnesses corrected, the fine step buys nothing.
    ///
    /// Measured across the four closed-form cases on this path: 0.05, 0.1 and 0.2 give
    /// identical results, and the one case that fails does so at every setting for an
    /// unrelated reason. 0.4 does not - the splayed case there terminates after 5004 steps
    /// reporting zero displacement, which is the settling test accepting a run that never
    /// happened rather than a gradual loss of accuracy. So the limit is real and sits
    /// between, and 0.2 is the coarsest verified value: eight times fewer steps than 0.025,
    /// with every verdict in the fast tier unchanged.
    ///
    /// A constant that exists to work around a defect outlives the defect unless something
    /// re-measures it.
    /// </remarks>
    public const double TimestepSafety = 0.2;

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

        /// <summary>
        /// Undoes the dilution in pulling each body toward the average of everything meeting
        /// here.
        /// </summary>
        /// <remarks>
        /// The offset to that average is (sum p - n p_i)/n, which is the mean separation to
        /// the other n-1 bodies scaled by (n-1)/n. Left uncorrected, two bodies at a site
        /// feel a spring of half the site's stated stiffness, and a member's two ends then
        /// deliver a quarter of it rather than a half - so a stiffness set to twice EA/L to
        /// survive the series came out at EA/2L. Multiplying by n/(n-1) makes the pull the
        /// mean pairwise separation, and Stiffness then means what its comment says.
        ///
        /// A grounded site pulls toward a fixed anchor rather than an average, so nothing is
        /// diluted and the gain is one.
        /// </remarks>
        public double Gain => Grounded || Bodies.Count < 2
            ? 1.0
            : Bodies.Count / (double)(Bodies.Count - 1);

        public double EffectiveStiffness => Stiffness * Gain;
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

    /// <summary>
    /// Where a joint acts, given the bearing region it was measured over.
    /// </summary>
    /// <remarks>
    /// Two-point Gauss positions per in-plane axis: half/sqrt(3) either side of centre. With
    /// each point carrying an equal share of the joint's stiffness this reproduces a
    /// uniformly loaded elastic bearing exactly, in force and in moment - see the caller.
    ///
    /// An axis narrower than the assignment tolerance collapses to one position, so a line
    /// contact yields two points and a point contact one. A joint with no measured region at
    /// all stays where it was, which keeps every contact found by intersection or proximity
    /// behaving exactly as before.
    /// </remarks>
    internal static List<Point3d> BearingPoints(Point3d centre, ContactExtent extent)
    {
        var points = new List<Point3d>();
        if (!extent.IsValid)
        {
            points.Add(centre);
            return points;
        }

        const double OverRootThree = 0.5773502691896257;
        var floor = RhinoMCPModFunctions.DefaultAssignToleranceMeters;
        var offsetsU = extent.HalfU > floor
            ? new[] { -extent.HalfU * OverRootThree, extent.HalfU * OverRootThree }
            : new[] { 0.0 };
        var offsetsV = extent.HalfV > floor
            ? new[] { -extent.HalfV * OverRootThree, extent.HalfV * OverRootThree }
            : new[] { 0.0 };

        foreach (var u in offsetsU)
        {
            foreach (var v in offsetsV)
            {
                points.Add(extent.Frame.PointAt(u, v));
            }
        }

        return points;
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
    /// The dashpot at each joint, one coefficient per joint rather than one per body.
    /// </summary>
    /// <remarks>
    /// Sized on the body's own mass it differed between the bodies meeting at a pin, and the
    /// forces it applied to the two ends of that pin were then not equal and opposite. A 5 t
    /// block on a 5.4 kg column got twenty-five times the coefficient the column got, so
    /// every relative motion left a net force on the pair and the assembly wound itself up
    /// instead of settling: 17 mm of steady drift where the static answer was 0.45, at a rate
    /// that barely changed when the load was quartered. With damping off entirely the same
    /// model oscillated cleanly about 0.49 mm, which is what said the spring was right and the
    /// dashpot was not. A site-wide coefficient sums to zero over the bodies at the joint, so
    /// momentum is conserved and the joint can only remove energy.
    ///
    /// Sized on the heaviest body there, not the lightest. A damping ratio is a fraction of
    /// critical for some mode, and the mode a joint has to still is the one carrying the most
    /// inertia through it. Against the lightest, a nominal zeta of 0.02 came out at 0.0008 for
    /// the assembly's own mode - a settling time of nine seconds for a run lasting half of
    /// one, which is why the response still looked undamped after the momentum error was
    /// fixed.
    /// </remarks>
    internal static double[] SiteDamping(List<Body> bodies, List<Site> sites, double dampingRatio)
    {
        var damping = new double[sites.Count];
        if (!(dampingRatio > 0.0))
        {
            return damping;
        }

        for (var s = 0; s < sites.Count; s++)
        {
            var heaviest = 0.0;
            foreach (var index in sites[s].Bodies)
            {
                heaviest = Math.Max(heaviest, bodies[index].Mass);
            }

            // The gain the spring gets, for the same reason: the slip this force sees is the
            // offset to the average of what meets here, which is the mean pairwise separation
            // scaled by (n-1)/n. Without it the dashpot is diluted where the spring is not.
            damping[s] = heaviest > 0.0
                ? 2.0 * dampingRatio * sites[s].Gain * Math.Sqrt(sites[s].EffectiveStiffness * heaviest)
                : 0.0;
        }

        return damping;
    }

    /// <summary>
    /// The step size, from the fastest motion any body can be driven into.
    /// </summary>
    /// <remarks>
    /// Explicit integration diverges above dt = 2/omega. The linear limit is the familiar
    /// sqrt(k/m), but a pin near a body's centre is stiffer in rotation than in
    /// translation - the same force acts on a smaller lever against a small moment of
    /// inertia - so the rotational limit governs and both are taken.
    ///
    /// The stiffnesses are summed over the joints holding each body, not maximised over them.
    /// A body pinned at n places is held by all n at once and rings at sqrt(sum k / m); taking
    /// the largest single joint understates that by sqrt(n), and the understatement grows with
    /// the model. It was enough to matter: a block on three columns held together, the same
    /// block in a two-storey stack with six joints diverged from 0.9 mm to 17 in a quarter of
    /// a second, and quartering the step by hand removed it - the logbook's test for marginal
    /// stability against a real model error. sqrt(6) is 2.4, which is what a fix has to
    /// recover, and quartering was the next thing tried after halving.
    ///
    /// An explicit dashpot has its own limit, dt = 2m/c, and it is summed the same way.
    /// Sizing a joint on its heaviest body means the lightest one there is damped far beyond
    /// critical, so this bound governs whenever the masses at a pin are wildly different.
    /// </remarks>
    internal static double Timestep(
        List<Body> bodies, List<Site> sites, double safety, double dampingRatio)
    {
        var damping = SiteDamping(bodies, sites, dampingRatio);

        var heldLinear = new double[bodies.Count];
        var heldSpin = new double[bodies.Count];
        var heldDamping = new double[bodies.Count];
        for (var s = 0; s < sites.Count; s++)
        {
            var site = sites[s];
            for (var i = 0; i < site.Bodies.Count; i++)
            {
                var index = site.Bodies[i];
                var lever = bodies[index].Local[site.Slots[i]].Length;
                heldLinear[index] += site.EffectiveStiffness;
                heldSpin[index] += site.EffectiveStiffness * lever * lever;
                heldDamping[index] += damping[s];
            }
        }

        var fastest = 0.0;
        for (var b = 0; b < bodies.Count; b++)
        {
            var body = bodies[b];
            if (!(body.Mass > 0.0))
            {
                continue;
            }

            fastest = Math.Max(fastest, Math.Sqrt(heldLinear[b] / body.Mass));
            fastest = Math.Max(fastest, heldDamping[b] / body.Mass);

            var smallest = Math.Min(body.Inertia.X, Math.Min(body.Inertia.Y, body.Inertia.Z));
            if (smallest > 0.0 && heldSpin[b] > 0.0)
            {
                fastest = Math.Max(fastest, Math.Sqrt(heldSpin[b] / smallest));
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
                spinHeld[index] += site.EffectiveStiffness * lever * lever;
            }
        }

        // One damping coefficient per joint, not one per body.
        //
        // Sized on the body's own mass it was different for each body meeting there, and the
        // forces it applied to the two ends of a joint were then not equal and opposite. A
        // 5 t block on a 5.4 kg column got twenty-five times the coefficient the column got,
        // so every relative motion at that pin left a net force on the pair, and the assembly
        // wound itself up instead of settling: 17 mm of steady drift where the static answer
        // was 0.45, growing at a rate that barely changed when the load was quartered. With
        // damping switched off entirely the same model oscillated cleanly about 0.49 mm,
        // which is what said the spring was right and the dashpot was not.
        //
        // A site-wide coefficient sums to zero over the bodies at the joint - c times the
        // sum of (joint velocity minus each point velocity) - so momentum is conserved and
        // the joint can only remove energy. It is sized on the lightest body there, which is
        // the one whose motion sets the fastest mode the damping has to stay stable against.
        var siteDamping = SiteDamping(bodies, sites, dampingRatio);

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

            for (var s = 0; s < sites.Count; s++)
            {
                var site = sites[s];
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
                    var pull = site.EffectiveStiffness * (target - here);

                    if (siteDamping[s] > 0.0 && body.Mass > 0.0)
                    {
                        var pointVelocity = body.Velocity + Vector3d.CrossProduct(body.AngularVelocity, arm);
                        var slip = jointVelocity - pointVelocity;
                        pull += siteDamping[s] * slip;
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
        double specificStiffness,
        double floorZMeters,
        double gravity,
        double durationSeconds,
        double dampingRatio,
        double imperfectionFraction,
        double lateralLoadFraction,
        double timestepSafety,
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
                    pinned[i], specificStiffness, carried[i], jointSlipMeters)
                : jointStrength;
        }

        var bodies = new List<StabilityRigidBodies.Body>(pinned.Count);
        var groundSlots = new List<HashSet<int>>(pinned.Count);
        // A joint becomes the bearing it was measured to be, rather than its centre point.
        //
        // A single point transmits force in three directions and no moment, because it has no
        // lever arm - which is why a 1150 mm wall and a 150 mm column behaved identically
        // here. Spreading the joint over its own bearing gives it one.
        //
        // The points are two-point Gauss positions, at half/sqrt(3) either side of centre
        // along each in-plane axis, each carrying its share of the joint's stiffness. That is
        // not a convenient guess: two-point Gauss integrates a uniformly loaded elastic
        // bearing exactly. Four points of k/4 at half/sqrt(3) sum to k in translation, and
        // their moment about the centre is 4 (k/4) (half/sqrt(3))^2 = k L^2 / 12, which is
        // the analytic rotational stiffness of that bearing. Corners would have given
        // k L^2 / 4, three times too stiff.
        //
        // A bearing with no width in one direction - a member cut square standing at an angle
        // on a flat pad touches along one edge - collapses to two points, and restrains
        // rotation about the line it touches along and not about the other axis. That is
        // correct rather than a degenerate case to guard against.
        var jointShare = new List<double[]>(pinned.Count);
        for (var i = 0; i < pinned.Count; i++)
        {
            var attachments = new List<Point3d>();
            var share = new List<double>();
            for (var j = 0; j < pinned[i].JointPoints.Count; j++)
            {
                var extent = j < pinned[i].JointExtents.Count
                    ? pinned[i].JointExtents[j]
                    : default;
                var spread = StabilityRigidBodies.BearingPoints(pinned[i].JointPoints[j], extent);
                foreach (var point in spread)
                {
                    attachments.Add(point);
                    share.Add(1.0 / spread.Count);
                }
            }

            var grounded = new HashSet<int>();
            foreach (var point in pinned[i].GroundPoints)
            {
                grounded.Add(attachments.Count);
                attachments.Add(point);
                share.Add(1.0);
            }

            groundSlots.Add(grounded);
            jointShare.Add(share.ToArray());
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
                // Divided by the number of points the joint was spread over, so spreading it
                // changes what the joint can resist and not how stiff it is. Without this a
                // four-point bearing would be four times stiffer in pure compression than the
                // member feeding it, and the axial answer would move for a reason that has
                // nothing to do with axial behaviour.
                site.Stiffness = Math.Min(site.Stiffness, 2.0 * stiffness[b] * jointShare[b][slot]);
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
                // Always relative to the joints it holds, never to a stated figure: a
                // ground sized from a stated joint stiffness is soft enough for the assembly
                // to sink into it.
                site.Stiffness = site.Stiffness * AutoBodyStiffnessRatio;
                anchoredGround++;
            }
        }

        var span = PinnedSpanMeters(pinned);
        var threshold = PinnedMechanismThresholdMeters(pinned);
        var imperfection = span * imperfectionFraction;
        var jolt = Math.Sqrt(2.0 * gravity * imperfection);
        var settledSpeed = threshold / Math.Max(durationSeconds, 1e-9) / 1000.0;
        var timestep = StabilityRigidBodies.Timestep(bodies, sites, timestepSafety, dampingRatio);

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

        // Where it came to rest, as distinct from the furthest it went on the way. A load
        // applied suddenly overshoots to twice its static deflection and rings back - correct
        // physics, and the right thing for a verdict to judge, but the wrong thing to
        // calibrate against. The last sample is the projected limit on a run that converged,
        // so it means the settled value in both cases and nothing at all when the run reached
        // no conclusion.
        graph["settled_displacement_m"] = run.DisplacementSamples.Count > 0
            ? run.DisplacementSamples[run.DisplacementSamples.Count - 1]
            : 0.0;
        graph["timestep_s"] = timestep;
        graph["timestep_safety"] = timestepSafety;
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
