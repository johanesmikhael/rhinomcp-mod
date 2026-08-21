using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Geometry;
using KangarooSolver;

namespace RhinoMCPModPlugin.Functions;

/// <summary>
/// A bearing surface between two rigid bodies: carries compression, carries no tension, and
/// opens on one side under moment.
/// </summary>
/// <remarks>
/// Neither of the two things already available does this. A shared particle - the pin - is
/// bilateral, so it holds in tension and an element can never lift off; and because three
/// non-collinear shared points weld two bodies solid, pins offer only a mechanism or a
/// weld with nothing in between. Kangaroo's own SolidCollide is unilateral but only reacts
/// once the meshes actually interpenetrate, since it is driven by
/// Intersection.MeshMeshAccurate; elements that sit exactly face to face produce no
/// intersection and it returns no force at all. It also divides its response by an integer
/// (1 / array.Length), so any contact yielding more than one intersection loop silently
/// loses its entire translational term.
///
/// This goal instead takes the contact patch as given - computed once from the geometry -
/// and places several push-only springs across it. The moment resistance is then emergent
/// rather than prescribed: as the joint tilts, points on the opening side stop pushing
/// while those on the closing side push harder, which is precisely how a dry joint carries
/// moment in proportion to the compression across it.
/// </remarks>
internal sealed class ContactPatch : GoalObject
{
    private readonly Point3d[] _points;
    private readonly double[] _stiffness;
    private readonly Vector3d _normal;
    private readonly Point3d _centreA;
    private readonly Point3d _centreB;
    private readonly double _friction;
    private readonly double _torqueGain;

    /// <summary>Contact points that carried load on the most recent Calculate.</summary>
    public int ActivePoints { get; private set; }

    /// <summary>Number of springs across this patch, and their total area in square metres.</summary>
    public int PointCount => _points.Length;

    /// <summary>Where the compression across the patch actually acts, on the most recent
    /// Calculate. A patch carrying its load centrally reports its own centre; one about to
    /// open reports a point at the closing edge. Reported for diagnosis: if this sits well
    /// inside the patch while statics says the resultant is outside it, the patch is
    /// carrying a moment it should not.</summary>
    public Point3d Resultant { get; private set; } = Point3d.Unset;

    /// <summary>The patch's own centre, for comparison against <see cref="Resultant"/>.</summary>
    public Point3d Centre
    {
        get
        {
            var sum = Point3d.Origin;
            for (var i = 0; i < _points.Length; i++)
            {
                sum += _points[i];
            }

            return _points.Length > 0 ? sum / _points.Length : Point3d.Unset;
        }
    }

    /// <summary>Total compression across the patch on the most recent Calculate.</summary>
    public double Compression { get; private set; }

    public ContactPatch(
        Plane bodyA,
        Plane bodyB,
        IReadOnlyList<Point3d> patchPoints,
        IReadOnlyList<double> patchAreas,
        Vector3d normal,
        double strength,
        double friction,
        double torqueGain)
    {
        if (patchPoints == null || patchAreas == null || patchPoints.Count != patchAreas.Count ||
            patchPoints.Count == 0)
        {
            throw new ArgumentException("A contact patch needs one area per point.");
        }

        _points = new Point3d[patchPoints.Count];
        _stiffness = new double[patchPoints.Count];
        for (var i = 0; i < patchPoints.Count; i++)
        {
            _points[i] = patchPoints[i];
            _stiffness[i] = strength * patchAreas[i];
        }

        _friction = Math.Max(0.0, friction);
        _torqueGain = torqueGain;
        _normal = normal;
        _normal.Unitize();
        _centreA = bodyA.Origin;
        _centreB = bodyB.Origin;

        // Two particles, one per body, bound by their orientation planes exactly as
        // SolidCollide binds its pair.
        PPos = new[] { bodyA.Origin, bodyB.Origin };
        Move = new Vector3d[2];
        Weighting = new double[2];
        Torque = new Vector3d[2];
        TorqueWeighting = new double[2];
        InitialOrientation = new[] { bodyA, bodyB };
    }

    public override void Calculate(List<KangarooSolver.Particle> p)
    {
        var particleA = p[PIndex[0]];
        var particleB = p[PIndex[1]];
        var toA = Transform.PlaneToPlane(particleA.StartOrientation, particleA.Orientation);
        var toB = Transform.PlaneToPlane(particleB.StartOrientation, particleB.Orientation);

        var normal = _normal;
        normal.Transform(toA);
        normal.Unitize();

        var moveA = Vector3d.Zero;
        var moveB = Vector3d.Zero;
        var torqueA = Vector3d.Zero;
        var torqueB = Vector3d.Zero;
        var weight = 0.0;
        var active = 0;
        var compression = 0.0;
        var resultantMoment = Vector3d.Zero;

        for (var i = 0; i < _points.Length; i++)
        {
            var onA = _points[i];
            var onB = _points[i];
            onA.Transform(toA);
            onB.Transform(toB);

            // Positive gap means the two faces have separated; a bearing surface does
            // nothing at all in that case, and that is what lets the joint open.
            var gap = (onB - onA) * normal;
            if (gap >= 0.0)
            {
                continue;
            }

            var push = normal * (-gap * 0.5);
            var stiffness = _stiffness[i];
            var response = push;

            // Coulomb friction. The two faces have slipped by the tangential part of their
            // separation; pull that back, but only as hard as the compression across this
            // point allows. Beyond mu times the penetration the surfaces slide, which is
            // the whole point - a dry joint holds until it does not, and then it slides
            // rather than resisting indefinitely.
            if (_friction > 0.0)
            {
                var separation = onB - onA;
                var slip = separation - (normal * (separation * normal));
                var slipLength = slip.Length;
                if (slipLength > RhinoMath.ZeroTolerance)
                {
                    var limit = _friction * (-gap);
                    var correction = Math.Min(slipLength, limit) * 0.5;
                    // B has slid by +slip relative to A, so the restoring move on B is
                    // -slip. response is applied as (A -= response, B += response), which
                    // makes the tangential part of response the move B should take.
                    slip.Unitize();
                    response -= slip * correction;
                }
            }

            moveA -= response * stiffness;
            moveB += response * stiffness;
            weight += stiffness;
            active++;
            compression += -gap * stiffness;
            resultantMoment += new Vector3d(onA) * (-gap * stiffness);

            // The same lever-arm construction SolidCollide uses: the angle swept by moving
            // the contact point, about each body's own centre.
            torqueA += TorqueAbout(particleA.Position, onA, -response) * stiffness;
            torqueB += TorqueAbout(particleB.Position, onB, response) * stiffness;
        }

        ActivePoints = active;
        Compression = compression;
        Resultant = compression > 0.0 ? new Point3d(resultantMoment / compression) : Point3d.Unset;

        if (active == 0 || weight <= 0.0)
        {
            Move[0] = Move[1] = Vector3d.Zero;
            Torque[0] = Torque[1] = Vector3d.Zero;
            Weighting[0] = Weighting[1] = 0.0;
            TorqueWeighting[0] = TorqueWeighting[1] = 0.0;
            return;
        }

        Move[0] = moveA / weight;
        Move[1] = moveB / weight;
        // How much of the patch's eccentric compression turns into rotation of the bodies
        // it joins. A dry joint opens only when the body above it rotates, so a gain that
        // is too low keeps the compression spread evenly across the patch, the joint never
        // opens, and an assembly that has to topple instead settles.
        Torque[0] = _torqueGain * (torqueA / weight);
        Torque[1] = _torqueGain * (torqueB / weight);
        Weighting[0] = Weighting[1] = weight;
        TorqueWeighting[0] = TorqueWeighting[1] = weight;
    }

    /// <summary>Rotation vector produced by displacing <paramref name="point"/> by <paramref name="move"/> about <paramref name="centre"/>.</summary>
    private static Vector3d TorqueAbout(Point3d centre, Point3d point, Vector3d move)
    {
        var arm = point - centre;
        var moved = point + move - centre;
        if (arm.Length <= RhinoMath.ZeroTolerance || moved.Length <= RhinoMath.ZeroTolerance)
        {
            return Vector3d.Zero;
        }

        arm.Unitize();
        moved.Unitize();
        var axis = Vector3d.CrossProduct(arm, moved);
        var sine = Math.Min(1.0, axis.Length);
        if (sine <= RhinoMath.ZeroTolerance)
        {
            return Vector3d.Zero;
        }

        axis.Unitize();
        return axis * Math.Asin(sine);
    }
}
