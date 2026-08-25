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

    /// <summary>
    /// Damping as a fraction of critical, for this path's own mode.
    /// </summary>
    /// <remarks>
    /// Not the particle path's 2%, and the difference is not a detail. That figure damps each
    /// particle against its own local stiffness, which over-damps the slow global mode; here
    /// each joint is damped against relative motion at that joint, which barely touches a mode
    /// where both ends of a joint move together. A number measured for one is meaningless for
    /// the other, and sharing one was how the rigid path came to be quietly under-damped.
    ///
    /// 0.2 is what the contact and overturning cases settle at. The closed-form axial cases
    /// want 1.0 and say so in the suite: they measure a settled deflection, where any residual
    /// ringing is error, while a verdict only needs the motion to be bounded. Two questions,
    /// two answers, and the case that needs the other one names it.
    /// </remarks>
    public const double DefaultDampingRatio = 0.2;

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

    /// <summary>
    /// What a joint is, as three switches over one spring.
    /// </summary>
    /// <remarks>
    /// The three modes this replaces were three solvers answering three questions. They are
    /// one spring with two flags, and the flags decide how the *measured bearing region* is
    /// used rather than adding behaviour of their own:
    ///
    /// - <see cref="Pin"/> collapses the region to its centre. One point has no lever arm, so
    ///   it carries force in three directions and no moment. That is the pinned idealisation,
    ///   and it is now something chosen rather than something the geometry forced.
    /// - <see cref="Fixed"/> uses the region as measured. Points d apart resist rotation with
    ///   k d^2, so the moment comes from the bearing rather than from a constant.
    /// - <see cref="Contact"/> is welded with each point able to push and not pull, plus
    ///   friction across it. A bearing that carries no tension opens when the load leaves it,
    ///   which is what a dry-stacked assembly does and what a pin cannot express.
    ///
    /// There is deliberately no "free". It would not be a construction type but a correction
    /// to the graph - a statement that a detected contact is not a connection - and anything
    /// that physically touches at least pushes, so contact is the honest floor. It would also
    /// be a poor fit for the weakest-governs rule below: one such rule on an element would
    /// silently delete every joint it has, including those the element opposite declared
    /// welded, and a structure would come apart with nothing in the report saying why. A
    /// spurious contact is a real problem and wants a pairwise suppression, where it cannot
    /// reach past the pair it names.
    ///
    /// Ordered weakest to strongest deliberately: where two elements disagree about the joint
    /// between them, the weaker governs, because a hinge assumed where a moment connection
    /// exists reports a structure softer and more mechanism-prone than it is. That fails safe
    /// for a stability verdict, and unlike "last rule wins" it does not depend on the order
    /// the rules were given in.
    /// </remarks>
    internal enum JointType
    {
        Contact = 0,
        Pin = 1,
        Fixed = 2
    }

    /// <summary>
    /// A joint type by name, as an engineer would say it.
    /// </summary>
    /// <remarks>
    /// Synonyms are accepted because the construction word differs by trade and the type does
    /// not: a hinge is a pin, a weld and a moment connection are the same joint here.
    /// </remarks>
    internal static bool TryParseJointType(string text, out JointType type)
    {
        type = JointType.Fixed;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "contact":
            case "bearing":
            case "dry":
                type = JointType.Contact;
                return true;
            case "pin":
            case "pinned":
            case "hinge":
                type = JointType.Pin;
                return true;
            // "welded" is kept because documents written before the rename hold it, and
            // because it is what a fabricator would say. It is not the canonical name: the
            // type means a moment connection however it is made, a bolted rigid plate as much
            // as a weld - and "welded" is already the name of an evaluation mode, where it
            // means the whole scope solved as one rigid body rather than anything about a
            // joint.
            case "fixed":
            case "welded":
            case "weld":
            case "moment":
                type = JointType.Fixed;
                return true;
            default:
                return false;
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

        /// <summary>What this joint is. Set from the model; defaults to a bearing.</summary>
        public JointType Type = JointType.Contact;

        /// <summary>
        /// The bearing normal, taken from the measured contact region's own plane.
        /// </summary>
        /// <remarks>
        /// Only a <see cref="JointType.Contact"/> joint reads it, and it is what decides which
        /// way "apart" is. Left unset - which happens when the joint was found by proximity
        /// rather than by two faces meeting, so there is no region and no plane - a contact
        /// joint has no direction to open along and behaves as welded. That is deliberate:
        /// inventing a normal from the line of centres would let a joint open along an axis
        /// nothing was measured about.
        /// </remarks>
        public Vector3d Normal = Vector3d.Unset;

        /// <summary>Coefficient of friction across the bearing, for a contact joint.</summary>
        public double Friction = RhinoMCPModFunctions.DefaultContactFriction;

        /// <summary>
        /// The most tension this bearing point may carry, in newtons. Infinity where nobody
        /// said, which is every joint until someone does.
        /// </summary>
        /// <remarks>
        /// Per bearing point rather than per joint, holding the joint's capacity divided by
        /// the points it was spread over. That is what gives a capacity a moment as well as a
        /// force, and by the mechanism a contact joint already uses: load an eccentric bearing
        /// hard enough and its far point reaches the limit first and stops holding, so the
        /// joint sheds its edge and rotates rather than failing everywhere at once.
        ///
        /// Tension only. A joint yields rather than breaking - the force holds at the limit
        /// and the structure redistributes, and if it cannot it moves, which the verdict is
        /// already watching for. Releasing it outright is more conservative and less useful:
        /// it makes the answer hinge on one number being exactly right.
        /// </remarks>
        public double Capacity = double.PositiveInfinity;

        /// <summary>
        /// Whether this bearing point ever reached its limit during the run.
        /// </summary>
        /// <remarks>
        /// Ever, not currently. A joint that yields settles at its limit, so the step that
        /// finds it exceeded is the step before it stops being exceeded - and reading the last
        /// step alone reports nothing happened.
        /// </remarks>
        public bool ReachedCapacity;

        /// <summary>
        /// Which way is "out of the bearing" for each body listed here: +1 or -1 against
        /// <see cref="Normal"/>.
        /// </summary>
        /// <remarks>
        /// Decided once from the undeformed geometry rather than every step, because it is a
        /// fact about which side of the joint a body sits on and that cannot change without
        /// the body passing through the joint. Deciding it per step from the current centre
        /// would also make it a function of the motion, and a body drifting across the bearing
        /// plane would flip the sign and turn a compression into a tension.
        ///
        /// Empty means the site could not be sided - the joint lies in a body's own centre
        /// plane, so there is no outward direction - and a contact there is treated as welded.
        /// Sidedness has to hold for every body at the joint or for none: siding one and not
        /// the other would leave the two ends of the joint applying forces that are not equal
        /// and opposite, which is the defect the per-body dashpot already cost a day to.
        /// </remarks>
        public readonly List<double> Outward = new();

        /// <summary>How many bodies here found the bearing open, on the most recent step.</summary>
        public int Opened;

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
    internal static List<Point3d> BearingPoints(
        Point3d centre, ContactExtent extent, JointType type)
    {
        var points = new List<Point3d>();

        // A pin is the bearing deliberately not used. Collapsing it to one point is what
        // makes it carry no moment - the freedom is granted rather than discovered, which is
        // the whole point of naming the joint rather than measuring it.
        if (!extent.IsValid || type == JointType.Pin)
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

    /// <summary>
    /// The axis a body can spin about with nothing resisting it, or unset if it has none.
    /// </summary>
    /// <remarks>
    /// That is the case exactly when every attachment lies on one line: the joint dashpots act
    /// at those points, a rotation about the line through them moves none of them, so the
    /// motion is invisible to every joint the body has. Two points always define such a line.
    /// Three off it do not, and a body with fewer than two attachments is not held at all -
    /// nothing should slow its rotation, which is what lets something in mid-air tumble.
    ///
    /// The axis is taken through the two furthest-apart attachments, so a near-collinear set
    /// is judged against its own longest chord rather than against whichever pair came first.
    /// </remarks>
    /// <summary>
    /// Whether a body's spin about its attachments is a freedom some joint granted it.
    /// </summary>
    /// <remarks>
    /// A pin carries no moment by construction, so a member pinned at two points may spin
    /// about the line through them and a real pin resists that with friction. A contact
    /// bearing grants nothing: it is a measured region that carries moment across its own
    /// width until it opens, and where that region is a line, rocking about the line is how
    /// the thing falls over. Friction there would hold up something that cannot stand.
    ///
    /// One contact is enough to disqualify the body. The friction is a property of how it is
    /// held, and a body held partly on a bearing is free to leave that bearing.
    /// </remarks>
    internal static bool SpinIsGranted(Body body, List<Site> sites)
    {
        if (body.Sites.Count == 0)
        {
            return false;
        }

        foreach (var index in body.Sites)
        {
            if (index < 0 || index >= sites.Count || sites[index].Type == JointType.Contact)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFinite(Point3d point)
    {
        return double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);
    }

    private static bool IsFinite(Vector3d vector)
    {
        return double.IsFinite(vector.X) && double.IsFinite(vector.Y) && double.IsFinite(vector.Z);
    }

    internal static Vector3d CollinearSpinAxis(Body body)
    {
        if (body.Local.Count < 2)
        {
            return Vector3d.Unset;
        }

        var first = 0;
        var second = 1;
        var longest = 0.0;
        for (var i = 0; i < body.Local.Count; i++)
        {
            for (var j = i + 1; j < body.Local.Count; j++)
            {
                var span = (body.Local[i] - body.Local[j]).SquareLength;
                if (span > longest)
                {
                    longest = span;
                    first = i;
                    second = j;
                }
            }
        }

        var axis = body.Local[second] - body.Local[first];
        if (!axis.Unitize())
        {
            return Vector3d.Unset;
        }

        var tolerance = RhinoMCPModFunctions.DefaultAssignToleranceMeters;
        foreach (var point in body.Local)
        {
            var offset = point - body.Local[first];
            if (Vector3d.CrossProduct(offset, axis).Length > tolerance)
            {
                return Vector3d.Unset;
            }
        }

        return axis;
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
        Func<double, bool> stopEarly,
        Vector3d[][] siteForces = null)
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

        // Pin friction acts about one axis, and only where there is one to act about.
        //
        // It was applied to the whole angular velocity, which makes it a rotational air drag:
        // it resists any turn, including the one an element makes as it falls off its support.
        // Sized as it is - a fraction of critical for the *joint* mode, where omega is tens of
        // thousands - it over-damps the slow overturning mode by four orders of magnitude. A
        // 192 kg cap overhanging its pedestal by 250 mm carries 570 N m of overturning moment
        // against 3.5e4 N m s of drag: a terminal 0.016 rad/s, about 2 mm in half a second,
        // where the truth is that it falls off. It is the logbook's own linear-damping defect
        // in rotation, and it was invisible until a joint could open.
        //
        // The freedom it exists for is specific: a body held at points that are all on one
        // line spins about that line with nothing resisting it, because the joint dashpots sit
        // exactly on the axis and see no velocity. A body held at three points off a line has
        // no such freedom - the joint dashpots already damp every rotation, with the right
        // lever arms - so it needs no friction and must be free to topple.
        //
        // And the freedom has to have been granted, not merely left over. A body bearing on a
        // line is held at two points as surely as a member pinned at both ends is, so the two
        // are the same picture to the geometry - but rocking off an edge IS the rotation about
        // that line, and damping it is damping the collapse. A 400 mm column stood on its base
        // edge with its centre of mass 212 mm to one side of the only thing under it read
        // stable, and moved 0.0003 mm under half its own weight applied sideways. So the axis
        // is taken only where every joint holding the body is one that grants the spin on
        // purpose. A dry bearing grants nothing.
        var spinAxis = new Vector3d[bodies.Count];
        for (var b = 0; b < bodies.Count; b++)
        {
            spinAxis[b] = SpinIsGranted(bodies[b], sites)
                ? CollinearSpinAxis(bodies[b])
                : Vector3d.Unset;
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

        // How often the motion is looked at, in simulated time rather than as a fixed count.
        //
        // Dividing the step count by a sample count means a longer run is sampled more
        // coarsely, so the answer depends on how long the run was asked to be - and not
        // monotonically. This bridge reported 3.0 mm and inconclusive over half a second,
        // 10.8 mm and stable over two, 5.1 mm and inconclusive over five, 5.1 mm and stable
        // over ten. It is one trajectory; what changed is how much of it anyone looked at.
        //
        // Everything the verdict rests on is read from these samples: the largest
        // displacement, the reversals that say a motion is bounded, the check that fires when
        // the collapse threshold is crossed. A peak between two samples is a peak that never
        // happened. So the cadence is a property of the physics - the default run, divided
        // into the same number of samples it always was - and a run ten times as long gets ten
        // times as many samples rather than ten times the gap between them.
        // How far a body may move in one step before the run has stopped being a simulation.
        //
        // Taken from the assembly's own size rather than from a stated speed, so it means the
        // same thing to a footbridge and to a pedestal: nothing crosses the whole structure
        // between two steps. A run past this point records nothing useful - it is stopped and
        // says so, instead of falling through to a verdict.
        var reach = 0.0;
        for (var i = 0; i < bodies.Count; i++)
        {
            for (var j = i + 1; j < bodies.Count; j++)
            {
                reach = Math.Max(reach, bodies[i].Centre.DistanceTo(bodies[j].Centre));
            }
        }

        var runawayStep = reach > 0.0 ? reach : double.MaxValue;

        var sampleInterval = StabilityDynamics.DefaultDurationSeconds /
            Math.Max(1, sampleCount);
        var sampleEvery = Math.Max(1, (int)Math.Round(sampleInterval / timestep));
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

                // A bearing pushes and does not pull.
                //
                // This is the only place the three joint types differ, and it is where the
                // relaxed contact solver's physics moves to. A pin and a weld hold in every
                // direction, so the spring below is the whole of them. A contact holds only in
                // compression, and only up to friction across the face - so the same spring is
                // split along the measured bearing normal, the tensile half is dropped, and
                // the tangential half is capped at mu times what is actually being pressed.
                //
                // The moment follows for free. Each bearing point drops out of the joint
                // independently as the load leaves it, so an element rotating off its support
                // sheds its far edge first and overturns on the near one, at the rate r x F
                // dictates. That is why torque_gain is not ported: it existed because
                // Kangaroo's projective step has no moments, and the fraction of eccentric
                // compression that became rotation had to be dialled in by hand.
                var contact = site.Type == JointType.Contact && site.Normal.IsValid &&
                    site.Outward.Count == site.Bodies.Count;
                site.Opened = 0;

                // Whether this joint has a stated limit and a plane to measure tension across.
                // Without a normal there is no way to tell a pull from a push, and capping a
                // magnitude would limit compression too - which no fastener does.
                var limited = !double.IsPositiveInfinity(site.Capacity) &&
                    site.Normal.IsValid && site.Outward.Count == site.Bodies.Count;

                for (var i = 0; i < site.Bodies.Count; i++)
                {
                    var body = bodies[site.Bodies[i]];
                    var here = body.WorldPoint(site.Slots[i]);
                    var arm = here - body.Centre;
                    var pull = site.EffectiveStiffness * (target - here);

                    var damping = Vector3d.Zero;
                    if (siteDamping[s] > 0.0 && body.Mass > 0.0)
                    {
                        var pointVelocity = body.Velocity + Vector3d.CrossProduct(body.AngularVelocity, arm);
                        var slip = jointVelocity - pointVelocity;
                        damping = siteDamping[s] * slip;
                    }

                    if (contact)
                    {
                        // Outward from this body, so "apart" means the same thing to every body
                        // meeting here whatever order they were listed in. The bearing plane's
                        // own normal has no preferred side; the body does.
                        var normal = site.Normal * site.Outward[i];

                        // The gap, as a force rather than a distance: positive means the spring
                        // is trying to hold the faces together, which a dry bearing cannot do.
                        var bearing = pull * normal;
                        if (bearing > 0.0)
                        {
                            // Open. Nothing touches, so there is nothing to damp either - the
                            // dashpot is friction in the joint, not drag on the body.
                            site.Opened++;
                            continue;
                        }

                        var total = pull + damping;
                        var along = total * normal;
                        var across = total - along * normal;
                        var limit = site.Friction * (-bearing);
                        var magnitude = across.Length;
                        if (magnitude > limit && magnitude > 0.0)
                        {
                            across *= limit / magnitude;
                        }

                        pull = along * normal + across;
                    }
                    else
                    {
                        pull += damping;
                    }

                    // A stated capacity, reached. The joint yields: the part of the force
                    // pulling the two apart holds at the limit while everything else is
                    // unchanged, so the structure redistributes and, if it cannot, moves -
                    // which is what the verdict is already watching for.
                    //
                    // After the contact branch rather than inside the else, because a contact
                    // joint can be given a capacity too. It refuses tension outright, so the
                    // limit never binds on one and costs nothing to check.
                    if (limited)
                    {
                        var outward = site.Normal * site.Outward[i];
                        var tension = pull * outward;
                        if (tension > site.Capacity)
                        {
                            pull -= (tension - site.Capacity) * outward;
                            site.ReachedCapacity = true;
                        }
                    }

                    // What this joint is carrying, overwritten every step so the last one
                    // survives. At the end of a run that settled, that is the reaction at
                    // rest - the force the joint has to be able to hold.
                    if (siteForces != null)
                    {
                        siteForces[s][i] = pull;
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
                var axis = spinAxis[bodies.IndexOf(body)];
                var friction = Vector3d.Zero;
                if (axis.IsValid)
                {
                    var spinDamping = 2.0 * dampingRatio * Math.Sqrt(
                        Math.Max(spinStiffness, 0.0) * Math.Min(inertiaWorld.X,
                            Math.Min(inertiaWorld.Y, inertiaWorld.Z)));
                    friction = spinDamping * (body.AngularVelocity * axis) * axis;
                }

                var torque = body.Torque - gyroscopic - friction;

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

                // Non-finite first: once a velocity is NaN every comparison below it is
                // false, so a diverged run slides past every test that would have caught it.
                var speed = body.Velocity.Length;
                if (!IsFinite(body.Centre) || !IsFinite(body.Velocity) ||
                    !IsFinite(body.AngularVelocity) || speed * timestep > runawayStep)
                {
                    result.Diverged = true;
                }

                peakSpeed = Math.Max(peakSpeed, speed);
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

            if (result.Diverged)
            {
                break;
            }

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
    /// What every joint of the model is carrying, once it has settled.
    /// </summary>
    /// <remarks>
    /// "Is it stable" is a yes or no, and the next question an engineer asks is which parts
    /// are working and how hard. Nothing here is new physics: the force at each bearing point
    /// is what the solver already applies every step, kept from the last one.
    ///
    /// The up-to-four Gauss points of one bearing are summed back into the single force that
    /// joint carries, and the force is reported as the lowest-numbered body at the joint
    /// receives it, so a pair reads once rather than twice with opposite signs.
    ///
    /// Tension is positive, following the sign the contact branch already uses: a force along
    /// the outward normal of a body is one pulling it away from the joint, which is what a dry
    /// bearing cannot supply and a bolt can.
    ///
    /// That is tension <em>across the bearing plane</em>, and not a member's axial force. For
    /// a column standing on a pad the two coincide; for a diagonal pinned at a node they do
    /// not, and reading one as the other would be wrong by the cosine between them. What is
    /// reported is what the joint carries, which is the question a fastener is chosen to
    /// answer.
    /// </remarks>
    private static JArray JointForceReport(
        List<PinnedBody> pinned,
        List<StabilityRigidBodies.Site> sites,
        Vector3d[][] siteForces,
        List<int[]> slotJoints)
    {
        var report = new JArray();
        if (siteForces == null)
        {
            return report;
        }

        // Keyed by the joint rather than by the site: one bearing is up to four sites.
        var totals = new Dictionary<(int Body, int Joint), (Vector3d Force, Vector3d Normal,
            Point3d Point, StabilityRigidBodies.JointType Type, List<int> Bodies,
            double PeakTension, int Points, double Capacity, bool Reached)>();

        for (var s = 0; s < sites.Count; s++)
        {
            var site = sites[s];
            if (site.Grounded || site.Bodies.Count == 0)
            {
                continue;
            }

            // Every body at the site, not only the lowest-numbered one. A site is a star:
            // at a truss node seven members are pulled toward one shared target, and
            // reporting one of them describes one member's force and discards the other six.
            // Newton's third law makes the two-body case redundant and the many-body case
            // not: the seven do not come in pairs, they sum to nothing between them.
            for (var i = 0; i < site.Bodies.Count; i++)
            {
                var body = site.Bodies[i];
                var slot = site.Slots[i];
                if (body >= slotJoints.Count || slot >= slotJoints[body].Length)
                {
                    continue;
                }

                var joint = slotJoints[body][slot];
                if (joint < 0)
                {
                    continue;
                }

                var normal = site.Normal.IsValid && site.Outward.Count == site.Bodies.Count
                    ? site.Normal * site.Outward[i]
                    : Vector3d.Unset;

                var key = (body, joint);
                if (!totals.TryGetValue(key, out var entry))
                {
                    entry = (Vector3d.Zero, normal, site.Anchor, site.Type,
                        new List<int>(site.Bodies), double.NegativeInfinity, 0, 0.0, false);
                }

                var force = siteForces[s][i];
                entry.Force += force;
                entry.Points++;
                // The joint's capacity is what its points hold between them.
                entry.Capacity += site.Capacity;
                entry.Reached |= site.ReachedCapacity;

                // The most any single bearing point is being pulled outward. Summing the four
                // points of a bearing gives the force the joint carries, and hides the one
                // thing a fastener is sized for: an eccentric bearing can be in net
                // compression while its far edge is in tension, which is what lifts and what
                // a bolt has to hold.
                if (normal.IsValid)
                {
                    entry.PeakTension = Math.Max(entry.PeakTension, force * normal);
                }

                // The joint's position, which is where any picture of the force has to put
                // it. Averaged over the site's own points so a bearing with extent reports
                // its centre rather than whichever Gauss point was seen first.
                entry.Point = entry.Points == 1
                    ? site.Anchor
                    : entry.Point + (site.Anchor - entry.Point) / entry.Points;

                totals[key] = entry;
            }
        }

        foreach (var pair in totals)
        {
            var entry = pair.Value;
            var magnitude = entry.Force.Length;
            var record = new JObject
            {
                ["body"] = pair.Key.Body,
                ["capacity_n"] = double.IsPositiveInfinity(entry.Capacity)
                    ? (double?)null
                    : Math.Round(entry.Capacity, 3),
                ["reached_capacity"] = entry.Reached,
                ["with"] = new JArray(entry.Bodies.Where(b => b != pair.Key.Body)
                    .Select(b => (object)b).ToArray()),
                ["joint_type"] = TypeName(entry.Type),
                ["force_n"] = Math.Round(magnitude, 3)
            };

            var guid = pinned[pair.Key.Body].Node?.Node?["g"]?.ToString();
            if (!string.IsNullOrEmpty(guid))
            {
                record["guid"] = guid;
            }

            record["bearing_points"] = entry.Points;

            // Where the force acts and which way it points, in solver metres. Without these
            // a force can be tabulated and not drawn: the magnitude says how hard, and
            // nothing says where or which way.
            record["at_m"] = new JArray(
                Math.Round(entry.Point.X, 6),
                Math.Round(entry.Point.Y, 6),
                Math.Round(entry.Point.Z, 6));
            record["vector_n"] = new JArray(
                Math.Round(entry.Force.X, 3),
                Math.Round(entry.Force.Y, 3),
                Math.Round(entry.Force.Z, 3));
            if (entry.Normal.IsValid)
            {
                var tension = entry.Force * entry.Normal;
                var shear = (entry.Force - tension * entry.Normal).Length;
                record["tension_n"] = Math.Round(tension, 3);
                record["shear_n"] = Math.Round(shear, 3);
                if (entry.Points > 1 && !double.IsNegativeInfinity(entry.PeakTension))
                {
                    record["peak_point_tension_n"] = Math.Round(entry.PeakTension, 3);
                }
            }

            report.Add(record);
        }

        return report;
    }

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
        JointTypeRules jointTypeRules,
        double lengthToMeters,
        RhinoDoc displayDoc,
        bool preferExactBearings = false,
        bool allowBuriedBearings = false)
    {
        var clusterReport = new JArray();
        var pinned = BuildPinnedBodies(
            graph, nodes, lengthToMeters, floorZMeters, GroundContactToleranceMeters,
            sharePins: true, clusterReport: clusterReport, jointTypeRules: jointTypeRules,
            preferExactBearings: preferExactBearings,
            allowBuriedBearings: allowBuriedBearings);
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
        var slotTypes = new List<StabilityRigidBodies.JointType[]>(pinned.Count);
        var slotNormals = new List<Vector3d[]>(pinned.Count);
        // Which joint each attachment belongs to, so the up-to-four Gauss points of one
        // bearing can be summed back into the one force that joint carries. Reporting them
        // separately would quarter every number and describe a bearing nobody built.
        var slotJoints = new List<int[]>(pinned.Count);
        // A joint's capacity divided among the points it was spread over, so four points of a
        // bearing each hold a quarter of what the joint can.
        var slotCapacities = new List<double[]>(pinned.Count);
        for (var i = 0; i < pinned.Count; i++)
        {
            var attachments = new List<Point3d>();
            var share = new List<double>();
            var types = new List<StabilityRigidBodies.JointType>();
            var normals = new List<Vector3d>();
            var joints = new List<int>();
            var capacities = new List<double>();
            for (var j = 0; j < pinned[i].JointPoints.Count; j++)
            {
                var extent = j < pinned[i].JointExtents.Count
                    ? pinned[i].JointExtents[j]
                    : default;
                var type = j < pinned[i].JointTypes.Count
                    ? pinned[i].JointTypes[j]
                    : StabilityRigidBodies.JointType.Fixed;

                var spread = StabilityRigidBodies.BearingPoints(
                    pinned[i].JointPoints[j], extent, type);
                // The bearing's own plane, which is what a contact joint opens across. Taken
                // from the region that was measured rather than from the line of centres: a
                // joint found by proximity has no region, no plane and therefore no direction
                // to open along, and says so by leaving this unset.
                var normal = extent.IsValid ? extent.Frame.ZAxis : Vector3d.Unset;
                foreach (var point in spread)
                {
                    attachments.Add(point);
                    share.Add(1.0 / spread.Count);
                    types.Add(type);
                    normals.Add(normal);
                    joints.Add(j);
                    capacities.Add(
                        j < pinned[i].JointCapacities.Count && pinned[i].JointCapacities[j].HasValue
                            ? pinned[i].JointCapacities[j].Value / spread.Count
                            : double.PositiveInfinity);
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
            while (joints.Count < attachments.Count)
            {
                // Ground attachments belong to no joint of the model's own.
                joints.Add(-1);
            }

            while (capacities.Count < attachments.Count)
            {
                // The ground holds whatever stands on it.
                capacities.Add(double.PositiveInfinity);
            }

            slotJoints.Add(joints.ToArray());
            slotCapacities.Add(capacities.ToArray());
            while (types.Count < attachments.Count)
            {
                // Ground attachments: the earth holds what stands on it, both ways.
                types.Add(StabilityRigidBodies.JointType.Fixed);
                normals.Add(Vector3d.ZAxis);
            }

            slotTypes.Add(types.ToArray());
            slotNormals.Add(normals.ToArray());
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
                        Stiffness = double.MaxValue,
                        Type = StabilityRigidBodies.JointType.Fixed
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
                site.Capacity = Math.Min(site.Capacity, slotCapacities[b][slot]);

                // Where the elements meeting here disagree about what the joint is, the
                // weaker governs: a hinge assumed where a moment connection exists reports
                // the structure softer and more mechanism-prone than it is, which fails safe
                // for a stability verdict.
                if (slotTypes[b][slot] < site.Type)
                {
                    site.Type = slotTypes[b][slot];
                }

                if (!site.Normal.IsValid && slotNormals[b][slot].IsValid)
                {
                    site.Normal = slotNormals[b][slot];
                }
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

                // The floor pushes and does not pull.
                //
                // Ground attachments were built with this integrator, before joints had
                // types, as bare points held to an anchor. When contact became one-sided the
                // gate it added asked for a type and a measured normal, and a ground point has
                // neither - so it fell through to a two-sided spring and stayed there, holding
                // a body down as firmly as it holds it up. Anything resting on the floor was
                // glued to it: a welded bridge cantilevered ten metres past its only footing,
                // 5042 kNm of overturning against 178 restoring, did not tip.
                //
                // A joint found by proximity is left unsided on purpose, because inventing a
                // normal from the line of centres would let it open along an axis nothing was
                // measured about. That caution does not apply here. The floor is a horizontal
                // plane at a single z, so its normal is known exactly and needs no measuring.
                site.Type = StabilityRigidBodies.JointType.Contact;
                site.Normal = Vector3d.ZAxis;
                anchoredGround++;
            }
        }

        // Which side of each bearing its bodies sit on, decided once from the geometry as
        // built rather than every step from where the bodies currently are. It is a fact about
        // which side of the joint a body is on, and that cannot change without the body
        // passing through the joint; read from the live centre it would instead be a function
        // of the motion, and a body drifting across the bearing plane would flip the sign and
        // read its own compression as a tension.
        //
        // A joint that cannot be sided - a bearing lying in a body's own centre plane, where
        // "outward" is not defined - is left unsided and behaves as welded. All or nothing:
        // siding one body at a joint and not the other would leave its two ends applying
        // forces that are not equal and opposite.
        foreach (var site in sites)
        {
            // Sided for every type, not only for contact. Only a contact joint acts on it -
            // the force loop still checks the type before using it - but which side of a
            // bearing a body sits on is a fact about the geometry, and it is what turns a
            // reported force into a tension or a compression. Restricting it to contact left
            // every welded and pinned joint reporting a magnitude with no sense to it.
            if (!site.Normal.IsValid)
            {
                continue;
            }

            for (var i = 0; i < site.Bodies.Count; i++)
            {
                var offset = site.Anchor - bodies[site.Bodies[i]].StartCentre;
                var along = offset * site.Normal;
                if (Math.Abs(along) <= DefaultAssignToleranceMeters)
                {
                    site.Outward.Clear();
                    break;
                }

                site.Outward.Add(Math.Sign(along));
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
                // The centre counts, and not only the attachments.
                //
                // A body rocking off a line bearing turns about the very points its joints
                // hold, so every attachment stays exactly where it started while the body
                // goes over. Measured on the attachments alone that motion is invisible: a
                // column stood on its base edge, centre of mass 212 mm to one side of the
                // only thing under it, read 0.0002 mm and stable while it was falling at
                // 1.7 m/s. The centre of mass is where the weight acts and it swings
                // furthest, so it is what says the body moved.
                worst = Math.Max(worst, body.StartCentre.DistanceTo(body.Centre));

                for (var slot = 0; slot < body.Local.Count; slot++)
                {
                    worst = Math.Max(
                        worst, body.StartWorldPoint(slot).DistanceTo(body.WorldPoint(slot)));
                }
            }

            return worst;
        }

        // What each joint carries, captured on the last step of the settling run and so, for
        // a run that settled, the reaction at rest. Only this run records them: the sway runs
        // below re-run the model under a lateral load and would overwrite the answer with the
        // forces of a differently loaded structure.
        var siteForces = new Vector3d[sites.Count][];
        for (var i = 0; i < sites.Count; i++)
        {
            siteForces[i] = new Vector3d[sites[i].Bodies.Count];
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
            },
            siteForces);

        var worstPin = run.DisplacementSamples.Count > 0 ? run.DisplacementSamples.Max() : 0.0;
        // A diverged run's samples are meaningless in both directions, so it cannot be read
        // as a collapse any more than as a standing structure.
        var isMechanism = !run.Diverged && worstPin > threshold;

        // A structure that rings without growing is standing.
        //
        // The three existing conclusions - it settled, it converged, it passed the threshold -
        // all assume the motion dies away or runs away. A dry bearing does neither: it
        // dissipates only while it is closed, so a block rocking on one rings for far longer
        // than half a second and the run ends undecided. Undecided is then read as not stable,
        // which is the wrong answer for a stack whose margin is +150 mm and whose motion never
        // reaches two thirds of the mechanism limit.
        //
        // What separates it from a mechanism is not how far it moved but which way: a
        // mechanism creeps, one direction, while a rocking block reverses. So the test is
        // reversals plus a peak that is not growing, and it adds no new tuned number - the
        // distance limit is the same threshold the mechanism test already uses.
        var bounded = false;
        var reversals = 0;
        if (!run.Settled && !run.Converged && !isMechanism &&
            run.DisplacementSamples.Count >= MotionSampleCount / 2)
        {
            var samples = run.DisplacementSamples;
            var half = samples.Count / 2;
            var early = 0.0;
            var late = 0.0;
            for (var i = 0; i < half; i++)
            {
                early = Math.Max(early, samples[i]);
            }

            for (var i = half; i < samples.Count; i++)
            {
                late = Math.Max(late, samples[i]);
            }

            for (var i = 2; i < samples.Count; i++)
            {
                if ((samples[i] - samples[i - 1]) * (samples[i - 1] - samples[i - 2]) < 0.0)
                {
                    reversals++;
                }
            }

            bounded = late <= early && reversals >= 2;
        }

        // A run that ran away concluded nothing, whatever else it looks like. It is not
        // stable and it is not a mechanism either: it is a failed integration, and the only
        // honest report is that there is no answer.
        var conclusive = !run.Diverged &&
            (run.Settled || run.Converged || isMechanism || bounded);
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

        // What each joint turned out to be, and how many of the contacts could actually be
        // sided. A verdict that changed because a rule matched more joints than intended has
        // to be diagnosable without re-deriving the rules by hand - and a contact that fell
        // back to welded for want of a measured bearing plane is a silent stiffening
        // otherwise.
        graph["joint_forces"] = JointForceReport(pinned, sites, siteForces, slotJoints);
        // Joints held at their stated limit. A verdict that changed because a joint yielded
        // has to say so, rather than leaving it to be inferred from a deflection.
        graph["joints_with_capacity"] = sites.Count(
            s => !s.Grounded && !double.IsPositiveInfinity(s.Capacity));
        graph["joints_at_capacity"] = sites.Count(s => s.ReachedCapacity);
        graph["bearing_source"] = allowBuriedBearings
            ? "buried"
            : preferExactBearings ? "exact" : "sampled";
        graph["joint_type_default"] = TypeName(jointTypeRules.Default);
        graph["joint_type_pair_rules"] = jointTypeRules.PairCount;
        graph["joint_type_counts"] = new JObject
        {
            ["contact"] = sites.Count(s => !s.Grounded && s.Type == StabilityRigidBodies.JointType.Contact),
            ["pin"] = sites.Count(s => !s.Grounded && s.Type == StabilityRigidBodies.JointType.Pin),
            ["fixed"] = sites.Count(s => !s.Grounded && s.Type == StabilityRigidBodies.JointType.Fixed)
        };
        graph["contact_joints_open"] = sites.Count(s => s.Opened > 0);
        graph["contact_joints_sided"] = sites.Count(
            s => s.Type == StabilityRigidBodies.JointType.Contact && s.Outward.Count == s.Bodies.Count);
        graph["stable"] = stable;
        graph["verdict"] = run.Diverged
            ? "inconclusive"
            : isMechanism ? "unstable" : (conclusive ? "stable" : "inconclusive");
        graph["diverged"] = run.Diverged;
        if (run.Diverged)
        {
            graph["diverged_reason"] =
                "the integration ran away rather than answering: peak speed " +
                run.PeakSpeed.ToString("G3", System.Globalization.CultureInfo.InvariantCulture) +
                " m/s after " + run.Steps + " steps. Nothing is reported about this model.";
        }
        graph["conclusive"] = conclusive;
        graph["settled"] = run.Settled;
        graph["converged"] = run.Converged;
        graph["bounded_response"] = bounded;
        graph["motion_reversals"] = reversals;
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
