using System;
using System.Collections.Generic;
using System.Linq;
using KangarooSolver;
using Particle = KangarooSolver.Particle;
using KangarooSolver.Goals;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace RhinoMCPModPlugin.Functions;

/// <summary>
/// A Newtonian integrator over the same goals the pinned mode already builds.
/// </summary>
/// <remarks>
/// Kangaroo's PhysicalSystem is an equilibrium finder, not a simulator, and its own Step
/// says so: it advances <c>Position += Velocity</c> with no timestep and no mass - the
/// Particle.Mass field is never read - then damps kinetically, halving velocity whenever a
/// correction opposes it and zeroing it whenever nothing pushes at all. Structures
/// therefore creep toward equilibrium rather than falling, and "unstable" has to be
/// inferred from how far something crept inside an iteration budget. That is why the
/// unbraced bridge needed 5250 iterations to be caught and why every stiffness constant in
/// this evaluator doubles as a rate.
///
/// The goals themselves are fine. A goal returns <c>Move</c>, a displacement, and
/// <c>Weighting</c>, a stiffness in N/m, so their product is a force in newtons - and
/// Kangaroo's own Unary confirms the reading, carrying the applied force as Move against a
/// weight of exactly one. So the goals can be kept and only the integrator replaced:
/// accumulate <c>F = sum(Weighting * Move)</c>, take <c>a = F/m</c>, and step real time.
///
/// What that buys is a verdict that no longer depends on a budget. A mechanism accelerates
/// under gravity - it covers L/200 in a time that follows from s = at^2/2 - while a sound
/// structure oscillates about its static deflection and damps out. The question becomes
/// "how far had it moved after half a second", which is a question about the structure
/// rather than about how long the solver was allowed to run.
///
/// One part stays projective. A RigidMesh's first particle is not a physical object: it
/// carries the body's best-fit frame, and Move[0]/Torque[0] are the fit's own correction,
/// not a force on anything. Those particles keep Kangaroo's update. Every other particle -
/// the pins, the ground points, the markers - carries mass and is integrated.
/// </remarks>
internal static class StabilityDynamics
{
    /// <summary>
    /// Fraction of the explicit-integration stability limit to actually use.
    /// </summary>
    /// <remarks>
    /// Semi-implicit Euler on a spring-mass pair is stable up to dt = 2/omega. Sitting at
    /// the limit is stable but not accurate, and the penalty stiffnesses here are large, so
    /// the step is held well below it.
    /// </remarks>
    public const double TimestepSafety = 0.1;

    /// <summary>
    /// Viscous damping as a fraction of critical, applied per particle.
    /// </summary>
    /// <remarks>
    /// Real structures dissipate; codes assume 2-5% of critical for steel framing. Without
    /// any damping a sound structure released from its undeflected position oscillates
    /// forever about its static deflection and peaks at twice it, which is correct dynamics
    /// but makes the peak, not the structure, decide a displacement verdict. This is a
    /// material property rather than a solver knob, and it is reported with the result.
    /// </remarks>
    public const double DefaultDampingRatio = 0.02;

    /// <summary>
    /// Geometric imperfection, as a fraction of the span, applied before the run.
    /// </summary>
    /// <remarks>
    /// A mechanism whose mode is antisymmetric is not excited by symmetric gravity: the
    /// unbraced bridge's mode slides one transverse tie sideways while seesawing it, and
    /// the structure sits exactly on that equilibrium. Integrated from a perfect starting
    /// position it therefore never moves, which is correct dynamics and a useless verdict -
    /// both bridges reported the same 0.216 mm.
    ///
    /// Real structures are not built perfect, and the codes say by how much: erection and
    /// straightness tolerances of span/500 to span/1000 are what stability checks are
    /// required to assume. Seeding that same imperfection is not a numerical trick to break
    /// symmetry, it is the modelling assumption an engineer already makes - and it is what
    /// makes the test meaningful, since a mechanism grows away from an imperfect start
    /// while a sound structure damps back toward its deflected shape.
    ///
    /// The direction is derived from the particle index rather than drawn at random, so a
    /// verdict is reproducible.
    /// </remarks>
    public const double DefaultImperfectionFraction = 1.0 / 1000.0;

    /// <summary>
    /// The horizontal probe load, as a fraction of the vertical load carried.
    /// </summary>
    /// <remarks>
    /// This is a measuring instrument, not a design load. Sway stiffness is what separates
    /// a braced structure from one held up only by second-order effects, and it is read by
    /// pushing sideways and seeing how far the structure goes.
    ///
    /// The codes' equivalent horizontal force is a few parts per thousand, and that is too
    /// small to measure with here: at 0.5% the stiffest direction of the test bridge moved
    /// 0.2 micron, under the solver's own settling residual, and the reported stiffness
    /// duly failed to converge - 9.7e8, 1.9e9, 2.4e9 as the run was lengthened, tracking
    /// nothing but the residual shrinking. Five percent puts the response at 1.6 to 8 micron,
    /// clear of that floor.
    ///
    /// It stays a secant stiffness, and the linearity is checked rather than assumed:
    /// quadrupling the probe to 20% changes the stiff direction by 0.2%. The soft direction
    /// does move, about 7% lower, which is the geometric softening an infinitesimal
    /// mechanism is expected to show.
    /// </remarks>
    public const double DefaultNotionalLoadFraction = 0.05;

    /// <summary>How long to simulate, in seconds, when the caller does not say.</summary>
    /// <remarks>
    /// A mechanism with even a tenth of gravity available to it covers 50 mm in 0.32 s.
    /// Half a second therefore separates falling from standing with room to spare, and
    /// unlike an iteration count it means the same thing on every model.
    /// </remarks>
    public const double DefaultDurationSeconds = 0.5;


    public sealed class Result
    {
        public bool Settled { get; set; }
        public double TimestepSeconds { get; set; }
        public int Steps { get; set; }
        public double SimulatedSeconds { get; set; }
        public double DampingRatio { get; set; }
        public double PeakSpeed { get; set; }
        public List<double> DisplacementSamples { get; } = new();
        public List<double> TimeSamples { get; } = new();
        public List<double> SpeedSamples { get; } = new();
    }

    /// <summary>
    /// Integrate the goals forward in real time.
    /// </summary>
    /// <param name="particles">The system's particles, already positioned and indexed.</param>
    /// <param name="goals">Goals whose PIndex has been assigned against those particles.</param>
    /// <param name="isFrame">Particles carrying a best-fit frame rather than mass.</param>
    /// <param name="masses">Particle masses in kilograms; ignored where isFrame is set.</param>
    /// <param name="measure">Returns the quantity the verdict is read from, in metres.</param>
    public static Result Run(
        List<Particle> particles,
        List<IGoal> goals,
        bool[] isFrame,
        double[] masses,
        double durationSeconds,
        double dampingRatio,
        double imperfectionSpeed,
        double settledSpeed,
        bool kineticDamping,
        int sampleCount,
        Func<double> measure,
        Func<double, double, bool> stopEarly)
    {
        if (particles == null || goals == null)
        {
            throw new ArgumentNullException(nameof(particles));
        }

        var stiffness = PeakStiffness(particles, goals, isFrame);
        var timestep = Timestep(stiffness, masses, isFrame);
        var steps = Math.Max(1, (int)Math.Ceiling(durationSeconds / timestep));

        var result = new Result
        {
            TimestepSeconds = timestep,
            DampingRatio = dampingRatio
        };

        var velocity = new Vector3d[particles.Count];
        var settledFor = 0;
        var previousKinetic = 0.0;

        // The disturbance is a velocity, not a displacement.
        //
        // Displacing the particles would pull them out of the rigid bodies they belong to
        // and store the imperfection as strain in springs of 3.6e8 N/m - about 26 kJ for a
        // 12 mm flaw, against the 81 J of work gravity does over the same distance. The
        // structure would then be ringing from an energy 300 times larger than the load it
        // is being asked to carry. A real imperfection is stress-free: members are made
        // very slightly the wrong length, not forced into place.
        //
        // A jolt has the right size and costs no stored energy. Its scale is the speed the
        // assembly would reach falling through its own erection tolerance,
        // v = sqrt(2*g*delta), which is what settling onto imperfect bearings does to it.
        // A sound structure absorbs that in an amplitude of v/omega - tens of microns at
        // these stiffnesses - while a mechanism, having no stiffness along its own mode,
        // simply keeps going.
        if (imperfectionSpeed > 0.0)
        {
            for (var i = 0; i < particles.Count; i++)
            {
                if (!isFrame[i] && masses[i] > 0.0)
                {
                    velocity[i] = ImperfectionDirection(i) * imperfectionSpeed;
                }
            }
        }

        var sampleEvery = Math.Max(1, steps / Math.Max(1, sampleCount));

        for (var step = 0; step < steps; step++)
        {
            foreach (var particle in particles)
            {
                particle.ClearForces();
            }

            foreach (var goal in goals)
            {
                goal.Calculate(particles);
            }

            // Weighting is a stiffness and Move a displacement, so their product is a
            // force. This is the whole of the change: Kangaroo divides the same sum by the
            // accumulated weight to get a position correction, which is a relaxation step;
            // dividing by mass instead gives an acceleration, which is Newton's second law.
            foreach (var goal in goals)
            {
                for (var i = 0; i < goal.PIndex.Length; i++)
                {
                    var particle = particles[goal.PIndex[i]];
                    particle.MoveSum += goal.Move[i] * goal.Weighting[i];
                    particle.WeightSum += goal.Weighting[i];

                    if (goal.Torque != null)
                    {
                        particle.TorqueSum += goal.Torque[i] * goal.TorqueWeighting[i];
                        particle.TorqueWeightSum += goal.TorqueWeighting[i];
                    }
                }
            }

            var peakSpeed = 0.0;
            for (var i = 0; i < particles.Count; i++)
            {
                var particle = particles[i];

                if (isFrame[i])
                {
                    // A fitted frame, not a body. Its correction is where the body's points
                    // say it should be, which is a measurement rather than a force, so it
                    // is applied directly and carries no velocity of its own.
                    if (particle.WeightSum > 0.0 && !particle.MoveSum.IsZero)
                    {
                        particle.Position += particle.MoveSum / particle.WeightSum;
                    }

                    if (particle.Orientation.IsValid)
                    {
                        var orientation = particle.Orientation;
                        orientation.Origin = particle.Position;
                        if (particle.TorqueWeightSum > 0.0 && !particle.TorqueSum.IsZero)
                        {
                            var turn = particle.TorqueSum / particle.TorqueWeightSum;
                            orientation.Rotate(turn.Length, turn);
                        }

                        particle.Orientation = orientation;
                    }

                    continue;
                }

                var mass = masses[i];
                if (!(mass > 0.0))
                {
                    continue;
                }

                // Critical damping for this particle's own local stiffness, so the same
                // fraction of critical applies whether it is held by a stiff member or a
                // soft one.
                var damping = 2.0 * dampingRatio * Math.Sqrt(Math.Max(0.0, particle.WeightSum) * mass);
                var force = particle.MoveSum - damping * velocity[i];

                velocity[i] += force / mass * timestep;
                particle.Position += velocity[i] * timestep;
                particle.Velocity = velocity[i];

                peakSpeed = Math.Max(peakSpeed, velocity[i].Length);
            }

            result.PeakSpeed = Math.Max(result.PeakSpeed, peakSpeed);
            result.Steps = step + 1;
            result.SimulatedSeconds = result.Steps * timestep;

            // Kinetic damping, for the static runs only.
            //
            // A stiffness measurement wants the equilibrium position and nothing else, and
            // real time is the wrong tool for reaching it: at the structure's own 2% of
            // critical it rings for tens of periods, and viscous damping sized on each
            // particle's local stiffness over-damps the slow global mode it most needs to
            // settle - critical damping per particle made the run four times longer and
            // still had not converged. Dynamic relaxation is the standard answer. Whenever
            // the assembly's kinetic energy turns over, every velocity is zeroed: the
            // structure is at a displacement extreme, so dropping the energy there moves it
            // straight to equilibrium instead of past it.
            //
            // This is a solver for a static problem, not a claim about how the structure
            // behaves - which is exactly the distinction Kangaroo's own Step blurs, and why
            // the verdict never uses this path.
            if (kineticDamping)
            {
                var kinetic = 0.0;
                for (var i = 0; i < particles.Count; i++)
                {
                    if (!isFrame[i] && masses[i] > 0.0)
                    {
                        kinetic += masses[i] * velocity[i].SquareLength;
                    }
                }

                if (kinetic < previousKinetic)
                {
                    for (var i = 0; i < velocity.Length; i++)
                    {
                        velocity[i] = Vector3d.Zero;
                        particles[i].Velocity = Vector3d.Zero;
                    }

                    previousKinetic = 0.0;

                    // The speed reached before this turnover is what is still left to
                    // settle. Once that is small the position has stopped changing.
                    if (peakSpeed < settledSpeed)
                    {
                        result.Settled = true;
                        result.TimeSamples.Add(result.SimulatedSeconds);
                        result.DisplacementSamples.Add(measure());
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

            // Nothing is learned by integrating a structure that has stopped moving. The
            // test is a speed rather than a displacement so that it cannot be passed by a
            // body at the top of its swing, and it is scaled so that whatever travel is
            // left over the rest of the requested duration is negligible against the
            // displacement that would decide the verdict.
            if (settledSpeed > 0.0 && peakSpeed < settledSpeed)
            {
                settledFor++;
                if (settledFor >= SettledStepsRequired)
                {
                    result.Settled = true;
                    var displacementNow = measure();
                    result.TimeSamples.Add(result.SimulatedSeconds);
                    result.DisplacementSamples.Add(displacementNow);
                    result.SpeedSamples.Add(peakSpeed);
                    break;
                }
            }
            else
            {
                settledFor = 0;
            }

            if ((step + 1) % sampleEvery != 0 && step + 1 != steps)
            {
                continue;
            }

            var displacement = measure();
            result.TimeSamples.Add(result.SimulatedSeconds);
            result.DisplacementSamples.Add(displacement);
            result.SpeedSamples.Add(peakSpeed);

            // A collapse under way does not reverse, so there is nothing to learn from
            // simulating the rest of it.
            if (stopEarly != null && stopEarly(displacement, result.SimulatedSeconds))
            {
                break;
            }
        }

        return result;
    }


    /// <summary>
    /// Particle assignment, replicating PhysicalSystem.AssignPIndex.
    /// </summary>
    /// <remarks>
    /// The integrator has to own the particles it integrates, and the KangarooSolver.dll
    /// this project builds against does not expose PhysicalSystem's list. The rule is
    /// Kangaroo's own, followed exactly so that a dynamic run and a relaxed run see the same
    /// system: coincident points become one particle, and a goal carrying a valid initial
    /// orientation takes a particle of its own when the one already at that position is
    /// oriented differently. Lookup is by rounded grid key rather than by scanning, which is
    /// the same answer in linear time.
    /// </remarks>
    public static List<Particle> AssignParticles(List<IGoal> goals, double tolerance)
    {
        var particles = new List<Particle>();
        var byCell = new Dictionary<(long, long, long), List<int>>();
        var grid = Math.Max(tolerance, 1e-12);

        (long, long, long) Cell(Point3d point) => (
            (long)Math.Floor(point.X / grid),
            (long)Math.Floor(point.Y / grid),
            (long)Math.Floor(point.Z / grid));

        int Find(Point3d point)
        {
            var cell = Cell(point);
            for (var dx = -1L; dx <= 1L; dx++)
            {
                for (var dy = -1L; dy <= 1L; dy++)
                {
                    for (var dz = -1L; dz <= 1L; dz++)
                    {
                        var key = (cell.Item1 + dx, cell.Item2 + dy, cell.Item3 + dz);
                        if (!byCell.TryGetValue(key, out var candidates))
                        {
                            continue;
                        }

                        foreach (var index in candidates)
                        {
                            if (particles[index].StartPosition.DistanceTo(point) <= tolerance)
                            {
                                return index;
                            }
                        }
                    }
                }
            }

            return -1;
        }

        int Add(Point3d point)
        {
            var particle = new Particle(point, 1.0);
            particles.Add(particle);
            var index = particles.Count - 1;
            var cell = Cell(point);
            if (!byCell.TryGetValue(cell, out var list))
            {
                list = new List<int>();
                byCell[cell] = list;
            }

            list.Add(index);
            return index;
        }

        foreach (var goal in goals)
        {
            goal.PIndex = new int[goal.PPos.Length];
            for (var i = 0; i < goal.PPos.Length; i++)
            {
                var found = Find(goal.PPos[i]);
                goal.PIndex[i] = found >= 0 ? found : Add(goal.PPos[i]);

                if (goal.InitialOrientation == null || !goal.InitialOrientation[i].IsValid)
                {
                    continue;
                }

                var particle = particles[goal.PIndex[i]];
                if (!particle.Orientation.IsValid)
                {
                    particle.Orientation = goal.InitialOrientation[i];
                    particle.StartOrientation = goal.InitialOrientation[i];
                    continue;
                }

                // Already oriented, and not the same frame: this goal needs its own
                // particle rather than sharing one whose orientation means something else.
                if (particle.StartOrientation.Origin.DistanceTo(goal.InitialOrientation[i].Origin) > tolerance ||
                    particle.StartOrientation.ZAxis * goal.InitialOrientation[i].ZAxis < 1.0 - 1e-9)
                {
                    var index = Add(goal.PPos[i]);
                    goal.PIndex[i] = index;
                    particles[index].Orientation = goal.InitialOrientation[i];
                    particles[index].StartOrientation = goal.InitialOrientation[i];
                }
            }
        }

        return particles;
    }

    /// <summary>
    /// How many consecutive steps must be slow before the assembly counts as settled.
    /// </summary>
    /// <remarks>
    /// More than one, because a single step can be slow while the structure is merely
    /// changing direction. At these timesteps this is a small fraction of one period.
    /// </remarks>
    public const int SettledStepsRequired = 200;

    /// <summary>The largest stiffness any single mass-carrying particle is held by.</summary>
    private static double PeakStiffness(List<Particle> particles, List<IGoal> goals, bool[] isFrame)
    {
        var perParticle = new double[particles.Count];
        foreach (var goal in goals)
        {
            for (var i = 0; i < goal.PIndex.Length; i++)
            {
                var weight = goal.Weighting[i];
                if (double.IsFinite(weight) && weight > 0.0)
                {
                    perParticle[goal.PIndex[i]] += weight;
                }
            }
        }

        var peak = 0.0;
        for (var i = 0; i < perParticle.Length; i++)
        {
            if (!isFrame[i])
            {
                peak = Math.Max(peak, perParticle[i]);
            }
        }

        return peak;
    }

    /// <summary>
    /// A reproducible unit direction for particle i.
    /// </summary>
    /// <remarks>
    /// An integer hash rather than a random draw: the same model must return the same
    /// verdict every time it is asked, and a stability answer that moves between runs is
    /// not an answer. The three components are decorrelated by using different multipliers.
    /// </remarks>
    private static Vector3d ImperfectionDirection(int index)
    {
        double Component(uint salt)
        {
            var hash = (uint)(index + 1) * salt;
            hash ^= hash >> 15;
            hash *= 2246822519u;
            hash ^= hash >> 13;
            return (hash % 20001) / 10000.0 - 1.0;
        }

        var direction = new Vector3d(
            Component(374761393u), Component(668265263u), Component(2654435761u));
        return direction.IsZero ? Vector3d.ZAxis : direction / direction.Length;
    }

    /// <summary>Index one goal against particles that already exist.</summary>
    public static void IndexAgainst(IGoal goal, List<Particle> particles, double tolerance)
    {
        goal.PIndex = new int[goal.PPos.Length];
        for (var i = 0; i < goal.PPos.Length; i++)
        {
            var best = -1;
            var nearest = tolerance;
            for (var p = 0; p < particles.Count; p++)
            {
                var distance = particles[p].StartPosition.DistanceTo(goal.PPos[i]);
                if (distance <= nearest)
                {
                    nearest = distance;
                    best = p;
                }
            }

            if (best < 0)
            {
                throw new InvalidOperationException(
                    "A load was placed where the solver has no particle to carry it.");
            }

            goal.PIndex[i] = best;
        }
    }

    /// <summary>
    /// The step size, from the stiffest spring holding the lightest mass.
    /// </summary>
    /// <remarks>
    /// Explicit integration diverges above dt = 2/sqrt(k/m), so the step is a property of
    /// the model rather than a setting: stiffer members and lighter elements both demand a
    /// finer step, and both are already known before the run starts.
    /// </remarks>
    private static double Timestep(double peakStiffness, double[] masses, bool[] isFrame)
    {
        var lightest = double.MaxValue;
        for (var i = 0; i < masses.Length; i++)
        {
            if (!isFrame[i] && masses[i] > 0.0)
            {
                lightest = Math.Min(lightest, masses[i]);
            }
        }

        if (!(peakStiffness > 0.0) || lightest == double.MaxValue)
        {
            return 1e-4;
        }

        var omega = Math.Sqrt(peakStiffness / lightest);
        return TimestepSafety * 2.0 / omega;
    }
}

public partial class RhinoMCPModFunctions
{
    public const string PinnedDynamicEvaluationMode = "multi_body_pinned_dynamic";

    /// <summary>
    /// The pinned assembly, integrated in real time instead of relaxed.
    /// </summary>
    /// <remarks>
    /// Same bodies, same pins, same member stiffness as the pinned mode. Two things differ.
    ///
    /// Mass is distributed over each body's own particles rather than applied at its
    /// centroid, because in an integrator the particles are what carry it; gravity is then
    /// a force on each of them, which is the same total load applied where the inertia
    /// actually is.
    ///
    /// The verdict is read from time rather than from iterations. A mechanism accelerates,
    /// so it crosses L/200 at a moment that follows from the load it cannot carry, while a
    /// sound structure oscillates about its static deflection and damps toward it. Neither
    /// answer moves if the run is made longer.
    /// </remarks>
    private static bool SolvePinnedDynamicFromGraph(
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
        double assignToleranceMeters,
        double durationSeconds,
        double dampingRatio,
        double imperfectionFraction,
        double lateralLoadFraction,
        double lengthToMeters,
        RhinoDoc displayDoc)
    {
        var clusterReport = new JArray();
        var bodies = BuildPinnedBodies(
            graph, nodes, lengthToMeters, floorZMeters, GroundContactToleranceMeters,
            sharePins: true, clusterReport: clusterReport);
        if (bodies.Count == 0)
        {
            throw new InvalidOperationException("No bodies were built for the dynamic solver.");
        }

        var goals = new List<IGoal>();
        var rigidGoals = new List<RigidMesh>(bodies.Count);
        var anchoredGround = 0;

        var carried = PinnedCarriedLoads(bodies, gravity);
        var bodyStrengths = new double[bodies.Count];
        for (var i = 0; i < bodies.Count; i++)
        {
            bodyStrengths[i] = jointStrengthIsAuto
                ? RelaxationCompensation * MemberAxialStiffness(
                    bodies[i], youngsModulus, materialDensity, carried[i], jointSlipMeters)
                : jointStrength;
        }

        var stiffest = bodyStrengths.Length > 0 ? bodyStrengths.Max() : jointStrength;

        // Which body owns which listed point, so its mass can be shared out afterwards.
        var bodyPoints = new List<List<Point3d>>(bodies.Count);
        foreach (var body in bodies)
        {
            AssignBodyMarkers(body);
            var points = new List<Point3d>();
            points.AddRange(body.JointPoints);
            points.AddRange(body.GroundPoints);
            points.AddRange(body.Markers);
            bodyPoints.Add(points);
        }

        for (var i = 0; i < bodies.Count; i++)
        {
            var body = bodies[i];
            var rigid = new RigidMesh(body.SolverMesh, body.BodyPlane, bodyPoints[i], bodyStrengths[i]);
            rigidGoals.Add(rigid);
            goals.Add(rigid);

            var anchor = jointStrengthIsAuto ? stiffest * AutoBodyStiffnessRatio : anchorStrength;
            foreach (var groundPoint in body.GroundPoints)
            {
                goals.Add(new Anchor(groundPoint, anchor));
                anchoredGround++;
            }
        }

        var particles = StabilityDynamics.AssignParticles(goals, assignToleranceMeters);
        var isFrame = new bool[particles.Count];
        var masses = new double[particles.Count];

        // A RigidMesh's first particle carries the body's fitted frame and nothing else.
        foreach (var rigid in rigidGoals)
        {
            if (rigid.PIndex.Length > 0)
            {
                isFrame[rigid.PIndex[0]] = true;
            }
        }

        // Each body's mass spread over the particles that carry it. A pin shared by two
        // members receives a share from both, which is what a shared node weighs.
        for (var i = 0; i < bodies.Count; i++)
        {
            var rigid = rigidGoals[i];
            var carriers = 0;
            for (var j = 1; j < rigid.PIndex.Length; j++)
            {
                if (!isFrame[rigid.PIndex[j]])
                {
                    carriers++;
                }
            }

            if (carriers == 0)
            {
                continue;
            }

            var share = bodies[i].Node.MassKilograms / carriers;
            for (var j = 1; j < rigid.PIndex.Length; j++)
            {
                var index = rigid.PIndex[j];
                if (!isFrame[index])
                {
                    masses[index] += share;
                }
            }
        }

        // Gravity where the mass is. The pinned mode applies one Unary at each body's
        // centroid, which is the same resultant, but a centroid here is a fitted frame with
        // no mass to accelerate.
        var totalWeight = 0.0;
        var gravityGoals = new List<IGoal>();
        for (var i = 0; i < particles.Count; i++)
        {
            if (isFrame[i] || !(masses[i] > 0.0))
            {
                continue;
            }

            var weight = new Unary(particles[i].Position, new Vector3d(0.0, 0.0, -gravity * masses[i]));
            gravityGoals.Add(weight);
            goals.Add(weight);
            totalWeight += gravity * masses[i];
        }

        // A notional horizontal load, as a fraction of weight.
        //
        // Self-weight alone cannot tell a stiff structure from one that is merely balanced.
        // A first-order mechanism can sit in stable equilibrium under gravity the way a
        // hanging chain does - it has a mode, but gravity restores it - and no amount of
        // vertical load will reveal that. The codes handle this with equivalent horizontal
        // forces, a small fraction of the vertical load applied sideways, precisely because
        // sway stiffness is what separates the two cases. A structure braced against its
        // own mechanism barely notices such a load; one relying on gravity to stand up
        // does not.
        // Held aside rather than added: the stiffness measurement runs the same assembly
        // with the load on and off, so the goals have to be switchable.
        var swayGoals = new Dictionary<int, List<IGoal>>();
        var swayTotal = new Dictionary<int, double>();
        if (lateralLoadFraction > 0.0)
        {
            for (var axis = 0; axis < 2; axis++)
            {
                var forAxis = new List<IGoal>();
                var total = 0.0;
                for (var i = 0; i < particles.Count; i++)
                {
                    if (isFrame[i] || !(masses[i] > 0.0))
                    {
                        continue;
                    }

                    var magnitude = lateralLoadFraction * gravity * masses[i];
                    var direction = axis == 0
                        ? new Vector3d(magnitude, 0.0, 0.0)
                        : new Vector3d(0.0, magnitude, 0.0);
                    forAxis.Add(new Unary(particles[i].Position, direction));
                    total += magnitude;
                }

                swayGoals[axis] = forAxis;
                swayTotal[axis] = total;
                gravityGoals.AddRange(forAxis);
            }
        }

        // Re-assigning would renumber every particle, so the gravity goals are indexed
        // against the particles that already exist. Each was created at one of their
        // positions, so none of them adds a particle.
        foreach (var goal in gravityGoals)
        {
            StabilityDynamics.IndexAgainst(goal, particles, assignToleranceMeters);
        }

        var startPositions = particles.Select(p => p.Position).ToArray();
        var startOrientations = particles.Select(p => p.Orientation).ToArray();

        void Reset()
        {
            for (var i = 0; i < particles.Count; i++)
            {
                particles[i].Position = startPositions[i];
                particles[i].Velocity = Vector3d.Zero;
                particles[i].Orientation = startOrientations[i];
                particles[i].ClearForces();
            }
        }

        double MaxPinMotion()
        {
            var worst = 0.0;
            foreach (var rigid in rigidGoals)
            {
                // Index 0 is the body's frame, not one of its pins.
                for (var j = 1; j < rigid.PIndex.Length; j++)
                {
                    var index = rigid.PIndex[j];
                    worst = Math.Max(worst, startPositions[index].DistanceTo(particles[index].Position));
                }
            }

            return worst;
        }

        var span = PinnedSpanMeters(bodies);
        var threshold = PinnedMechanismThresholdMeters(bodies);
        var imperfection = span * imperfectionFraction;
        var imperfectionSpeed = Math.Sqrt(2.0 * gravity * imperfection);
        var collapsed = false;

        // Whatever travel is left at this speed, over the rest of the requested duration, is
        // a thousandth of the displacement that would decide the verdict.
        var settledSpeed = threshold / Math.Max(durationSeconds, 1e-9) / 1000.0;

        StabilityDynamics.Result Integrate(
            List<IGoal> active, double jolt, bool stopOnThreshold, bool statics)
        {
            Reset();
            return StabilityDynamics.Run(
                particles,
                active,
                isFrame,
                masses,
                durationSeconds,
                dampingRatio,
                jolt,
                settledSpeed,
                statics,
                MotionSampleCount,
                MaxPinMotion,
                (displacement, _) =>
                {
                    if (!stopOnThreshold)
                    {
                        return false;
                    }

                    if (displacement > threshold)
                    {
                        collapsed = true;
                    }

                    return collapsed;
                });
        }

        // The verdict run: does it fall over, given the imperfection it was built with.
        var run = Integrate(goals, imperfectionSpeed, true, false);

        // Whether it falls is not the whole answer.
        //
        // Four of this bridge's modes are infinitesimal mechanisms: the tie's ends separate
        // as 2*sqrt(1 + (0.71t)^2), so its length is preserved to first order and grows only
        // at second. A rank test counts such a mode as a mechanism, but the structure does
        // not collapse - it stiffens quadratically as it moves, held by the states of
        // self-stress that accompany the mode. Answering only "does it fall" would rate that
        // bridge identically to a properly braced one, which is not what an engineer needs
        // to know.
        //
        // Sway stiffness separates them, and it is measured rather than inferred: settle the
        // assembly, settle it again under a notional horizontal load of the kind codes
        // already prescribe, and divide the load by the distance between the two settled
        // shapes. That is a secant stiffness in N/m, taken in both horizontal directions
        // because the soft direction is a property of the structure rather than something to
        // assume. The disturbance is switched off for these runs so that what is measured is
        // the response to the load and nothing else.
        var stiffnessReport = new JObject();
        if (lateralLoadFraction > 0.0 && !collapsed && run.Settled)
        {
            Integrate(goals, 0.0, false, true);
            var settled = particles.Select(p => p.Position).ToArray();

            var softest = double.MaxValue;
            string softestAxis = null;
            foreach (var axis in swayGoals.Keys.OrderBy(k => k))
            {
                var loaded = new List<IGoal>(goals);
                loaded.AddRange(swayGoals[axis]);
                Integrate(loaded, 0.0, false, true);

                var sway = 0.0;
                for (var i = 0; i < particles.Count; i++)
                {
                    if (!isFrame[i])
                    {
                        sway = Math.Max(sway, settled[i].DistanceTo(particles[i].Position));
                    }
                }

                var name = axis == 0 ? "x" : "y";
                var stiffness = sway > 0.0 ? swayTotal[axis] / sway : double.PositiveInfinity;
                stiffnessReport[$"sway_{name}_m"] = sway;
                stiffnessReport[$"sway_{name}_drift_ratio"] = span > 0.0 ? sway / span : 0.0;
                stiffnessReport[$"sway_stiffness_{name}_n_per_m"] =
                    double.IsInfinity(stiffness) ? (JToken)JValue.CreateNull() : stiffness;

                if (stiffness < softest)
                {
                    softest = stiffness;
                    softestAxis = name;
                }
            }

            stiffnessReport["notional_load_n"] = swayTotal.Values.FirstOrDefault();
            stiffnessReport["softest_direction"] = softestAxis;
            stiffnessReport["sway_stiffness_min_n_per_m"] =
                double.IsInfinity(softest) ? (JToken)JValue.CreateNull() : softest;

            // Only worth re-running to restore the judged shape if something will draw it:
            // the stiffness runs leave the particles in their last loaded position, and a
            // fourth full integration is a quarter of the evaluation's cost.
            if (displayDoc != null)
            {
                Integrate(goals, imperfectionSpeed, true, false);
            }
        }

        graph["sway"] = stiffnessReport;

        var worstPin = run.DisplacementSamples.Count > 0 ? run.DisplacementSamples.Max() : 0.0;
        var isMechanism = worstPin > threshold;

        // A run that was still moving when time ran out has not shown anything.
        //
        // Settling time is set by the structure's lowest natural frequency, which falls as
        // it gets larger, while the timestep stays pinned by the stiffest member in it. On a
        // 24 m version of the test bridge the assembly was still moving at the end of the
        // requested half second, 5.2 mm and growing, and reporting "stable" there would mean
        // no more than "it had not fallen yet" - the same budget-dependence this mode exists
        // to remove, wearing different clothes.
        //
        // So stable means settled and below the limit. An unsettled run is reported as
        // inconclusive and does not claim stability; the caller can raise duration_seconds
        // and ask again.
        var conclusive = run.Settled || isMechanism;
        var stable = conclusive && !isMechanism;

        for (var i = 0; i < bodies.Count; i++)
        {
            var body = bodies[i];
            var rigid = rigidGoals[i];
            if (rigid.PIndex.Length > 0 && particles[rigid.PIndex[0]].Orientation.IsValid)
            {
                body.DocumentTransform = StabilityUnits.SolverTransformToDocument(
                    Transform.PlaneToPlane(body.BodyPlane, particles[rigid.PIndex[0]].Orientation),
                    lengthToMeters);
            }
        }

        // How pinned the pins actually are.
        //
        // Two bodies sharing one particle are pinned: the connection carries force in three
        // directions and no moment at all. Sharing two makes a hinge, free about that line
        // only. Sharing three non-collinear particles is a welded joint however the mode is
        // labelled, because three points fix a frame. This is not an inference from the
        // solver's behaviour - it is what the assembled system literally is - so it settles
        // whether a pinned run is modelling pins without appealing to a rank test.
        var sharing = new Dictionary<int, int>();
        var worstShare = 0;
        var weldedPairs = new JArray();
        for (var i = 0; i < rigidGoals.Count; i++)
        {
            var mine = new HashSet<int>();
            for (var k = 1; k < rigidGoals[i].PIndex.Length; k++)
            {
                mine.Add(rigidGoals[i].PIndex[k]);
            }

            for (var j = i + 1; j < rigidGoals.Count; j++)
            {
                var shared = new List<int>();
                for (var k = 1; k < rigidGoals[j].PIndex.Length; k++)
                {
                    if (mine.Contains(rigidGoals[j].PIndex[k]))
                    {
                        shared.Add(rigidGoals[j].PIndex[k]);
                    }
                }

                if (shared.Count == 0)
                {
                    continue;
                }

                sharing.TryGetValue(shared.Count, out var seen);
                sharing[shared.Count] = seen + 1;
                worstShare = Math.Max(worstShare, shared.Count);

                if (shared.Count >= 2 && weldedPairs.Count < 24)
                {
                    weldedPairs.Add(new JObject
                    {
                        ["a"] = bodies[i].Node.Node["g"],
                        ["b"] = bodies[j].Node.Node["g"],
                        ["shared_particles"] = shared.Count
                    });
                }
            }
        }

        var sharingReport = new JObject();
        foreach (var entry in sharing.OrderBy(pair => pair.Key))
        {
            sharingReport[entry.Key.ToString()] = entry.Value;
        }

        graph["joint_sharing_histogram"] = sharingReport;
        graph["joint_max_shared_particles"] = worstShare;
        graph["joint_welded_examples"] = weldedPairs;

        var widest = 0.0;
        foreach (var entry in clusterReport)
        {
            widest = Math.Max(widest, entry["diameter_m"]?.Value<double>() ?? 0.0);
        }

        graph["evaluation_mode"] = PinnedDynamicEvaluationMode;
        graph["body_count"] = bodies.Count;
        graph["particle_count"] = particles.Count;
        graph["joint_count"] = bodies.Sum(b => b.JointCount) / 2;
        graph["node_count_clustered"] = clusterReport.Count;
        graph["node_widest_m"] = widest;
        graph["nodes"] = clusterReport;
        graph["anchored_ground_points"] = anchoredGround;
        graph["stable"] = stable;
        graph["verdict"] = isMechanism ? "unstable" : (conclusive ? "stable" : "inconclusive");
        graph["conclusive"] = conclusive;
        graph["verdict_metric"] = "pin_displacement";
        graph["mechanism_threshold_m"] = threshold;
        graph["span_m"] = span;
        graph["max_pin_displacement_m"] = worstPin;
        graph["timestep_s"] = run.TimestepSeconds;
        graph["steps_run"] = run.Steps;
        graph["simulated_seconds"] = run.SimulatedSeconds;
        graph["duration_requested_s"] = durationSeconds;
        graph["damping_ratio"] = dampingRatio;
        graph["imperfection_m"] = imperfection;
        graph["imperfection_speed_m_s"] = imperfectionSpeed;
        graph["lateral_load_fraction"] = lateralLoadFraction;
        graph["imperfection_fraction"] = imperfectionFraction;
        graph["peak_speed_m_s"] = run.PeakSpeed;
        graph["settled"] = run.Settled;
        graph["settled_speed_m_s"] = settledSpeed;
        graph["total_weight_n"] = totalWeight;
        graph["time_samples_s"] = new JArray(run.TimeSamples.Select(v => (object)v).ToArray());
        graph["motion_samples_m"] = new JArray(run.DisplacementSamples.Select(v => (object)v).ToArray());
        graph["speed_samples_m_s"] = new JArray(run.SpeedSamples.Select(v => (object)v).ToArray());
        graph["member_stiffness_min_n_per_m"] = bodyStrengths.Length > 0
            ? bodyStrengths.Min() / RelaxationCompensation
            : 0.0;
        graph["member_stiffness_max_n_per_m"] = bodyStrengths.Length > 0
            ? bodyStrengths.Max() / RelaxationCompensation
            : 0.0;

        if (displayDoc != null)
        {
            ClearAfterEvaluationCache(displayDoc);
            WriteMultiBodyDisplay(displayDoc, bodies);
            global::RhinoMCPModPlugin.MCPStabilityController.SetEnabled(true);
        }

        return stable;
    }
}
