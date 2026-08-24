using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace RhinoMCPModPlugin;

/// <summary>
/// A flat piece of a body's surface: the plane it lies in, the closed outline that bounds it,
/// and enough bookkeeping to reject pairs cheaply.
/// </summary>
/// <remarks>
/// This is the one intermediate every geometry kind reduces to. A Brep contributes its planar
/// faces, a Mesh contributes its coplanar face groups, and once both sides are regions the
/// code that pairs them and intersects them is written once and does not care where they came
/// from - which is what makes Brep-Mesh a pair rather than a third implementation.
///
/// <see cref="Plane"/>'s Z axis points <em>out of</em> the body, so two faces that bear on one
/// another have antiparallel normals. That convention is what gives the offset between them a
/// sign, and the sign is what makes "almost touching", "touching" and "overlapping" one case
/// instead of three.
/// </remarks>
internal struct PlanarRegion
{
    public Plane Plane;
    public Curve Outline;
    public double Area;
    public BoundingBox Box;
}

/// <summary>
/// A bearing measured rather than sampled: the polygon two faces actually share.
/// </summary>
internal struct PlanarBearingResult
{
    public bool IsValid;

    /// <summary>Frame on the mean plane of the pair, X and Y along the bearing's own axes.</summary>
    public Plane Frame;

    public double HalfU;
    public double HalfV;

    /// <summary>True area of the shared polygon, which is not the rectangle's area unless the
    /// bearing happens to be rectangular. Reported separately for exactly that reason.</summary>
    public double PolygonArea;

    /// <summary>Signed offset between the two face planes. Positive is a gap, negative is
    /// interpenetration. Zero is the perfect-touching case that used to fail.</summary>
    public double Offset;

    /// <summary>Region pairs that passed the parallel and offset tests.</summary>
    public int Pairs;

    /// <summary>Disjoint pieces the boolean intersection returned for the governing pair. More
    /// than one means the bearing is not a single patch and the rectangle describes the
    /// largest piece only.</summary>
    public int Pieces;

    public int RegionsA;
    public int RegionsB;

    /// <summary>
    /// True when the two faces cross rather than bear, so what they share is a line.
    /// </summary>
    /// <remarks>
    /// A line is a real contact and a real answer, not a degenerate rectangle. It carries
    /// force and no moment about itself, which is what a slab resting on a wall's top edge
    /// does: it rocks. <see cref="HalfV"/> is zero for exactly that reason.
    /// </remarks>
    public bool IsLine;

    /// <summary>
    /// True when the reported area is the two bodies' shared surface inside the volume they
    /// overlap, rather than a bearing between faces that meet.
    /// </summary>
    /// <remarks>
    /// This is an assumption, and a different one from the line: it reads a deliberate overlap
    /// as a socket, so a member driven into another is treated as bearing over the region
    /// buried rather than rocking on the edge it would touch if lifted out. It reports more
    /// capacity than a line, so <see cref="PenetrationDepth"/> is the number to read beside it -
    /// the area exists only in proportion to how far the drawing goes through itself.
    /// </remarks>
    public bool IsBuried;

    /// <summary>Length of the line the two faces cross along, kept when an area is reported
    /// for the buried region instead, so the weaker reading is never simply lost.</summary>
    public double LineLength;

    /// <summary>How far from parallel the two faces are, in degrees. Zero for a flat bearing;
    /// reported for a line so the reading can be judged rather than taken.</summary>
    public double SkewDegrees;

    /// <summary>Angle between the normal reported and the bisector of the two face normals.
    /// The bisector is the other candidate rule for a line contact and this is what choosing
    /// between them is worth - measured rather than argued.</summary>
    public double BisectorDegrees;

    public double RectangleArea => 4.0 * HalfU * HalfV;

    public Point3d[] Corners()
    {
        return new[]
        {
            Frame.PointAt(-HalfU, -HalfV),
            Frame.PointAt(HalfU, -HalfV),
            Frame.PointAt(HalfU, HalfV),
            Frame.PointAt(-HalfU, HalfV)
        };
    }

    public double PenetrationDepth => Offset < 0.0 ? -Offset : 0.0;
}

internal static class PlanarBearing
{
    /// <summary>Same 20 degrees the sampled path uses to decide two faces are parallel enough
    /// to bear on one another, so the two measurements are answering the same question.</summary>
    public const double ParallelCosine = 0.94;

    /// <summary>Guard against a curved mesh producing thousands of one-face "planes". A body
    /// with more distinct flat regions than this is not a bearing candidate worth the work.</summary>
    private const int MaxRegionsPerBody = 256;

    private const int MinPolygonPoints = 3;

    /// <summary>
    /// The flat regions of a body's surface.
    /// </summary>
    /// <remarks>
    /// Brep-family geometry is read directly, because its faces already carry exact planes and
    /// exact trimmed outlines and meshing them would only throw that away. Everything else
    /// goes through the mesh path, which is also the path a genuine mesh object takes - so a
    /// Brep and a mesh of the same box give the same regions, and Brep-Mesh needs no code of
    /// its own.
    /// </remarks>
    public static List<PlanarRegion> ExtractRegions(GeometryBase geometry, Mesh proxy, double tolerance)
    {
        var regions = new List<PlanarRegion>();
        if (TryGetBrep(geometry, out var brep) && brep.Faces.Count > 0)
        {
            AddBrepRegions(brep, tolerance, regions);
            if (regions.Count > 0)
            {
                return regions;
            }
        }

        if (proxy != null)
        {
            AddMeshRegions(proxy, tolerance, regions);
        }

        return regions;
    }

    private static bool TryGetBrep(GeometryBase geometry, out Brep brep)
    {
        switch (geometry)
        {
            case Brep b:
                brep = b;
                return true;
            case Extrusion extrusion:
                brep = extrusion.ToBrep();
                return brep != null;
            case SubD subd:
                brep = subd.ToBrep(SubDToBrepOptions.Default);
                return brep != null;
            case Surface surface:
                brep = surface.ToBrep();
                return brep != null;
            default:
                brep = null;
                return false;
        }
    }

    private static void AddBrepRegions(Brep brep, double tolerance, List<PlanarRegion> regions)
    {
        foreach (var face in brep.Faces)
        {
            if (regions.Count >= MaxRegionsPerBody)
            {
                return;
            }

            if (!face.TryGetPlane(out var plane, tolerance))
            {
                continue;
            }

            // TryGetPlane answers in the surface's own parameterisation, which is not
            // necessarily the face's. A solid's faces point outward only once the trim
            // orientation is applied.
            if (face.OrientationIsReversed)
            {
                plane.Flip();
            }

            var loop = face.OuterLoop;
            var outline = loop?.To3dCurve();
            if (outline == null || !outline.IsClosed)
            {
                continue;
            }

            // Holes are ignored deliberately: a bearing measured through a bolt hole is wrong
            // by the area of the hole, which is negligible, and carrying inner loops through a
            // boolean intersection is not.
            var properties = AreaMassProperties.Compute(outline, tolerance);
            var area = properties?.Area ?? 0.0;
            if (!(Math.Abs(area) > 0.0))
            {
                continue;
            }

            // The plane's origin is moved to the face's centroid. Where TryGetPlane puts it
            // is a corner of the surface's parameterisation, and the offset between two
            // regions is measured from these origins - so for a pair that is not exactly
            // parallel the measurement was taken at two unrelated corners and came out as a
            // number about the corners rather than about the bearing. A slab tilted 10 degrees
            // on a wall was rejected as too deeply buried on that basis, well inside the
            // 20-degree window the parallel test allows.
            plane.Origin = properties.Centroid;

            regions.Add(new PlanarRegion
            {
                Plane = plane,
                Outline = outline,
                Area = Math.Abs(area),
                Box = outline.GetBoundingBox(true)
            });
        }
    }

    /// <summary>
    /// Coplanar face groups of a mesh, each reduced to the closed outline that bounds it.
    /// </summary>
    /// <remarks>
    /// Faces are agglomerated against existing groups rather than hashed into buckets, because
    /// hashing a normal splits a flat face in two whenever it lands on a bucket boundary and
    /// the split is invisible in the result.
    ///
    /// The outline is the group's boundary - the edges used by exactly one face of the group.
    /// Edges are keyed by quantised vertex position rather than by vertex index, so an
    /// unwelded mesh, where every face carries its own copy of each corner, is still bounded
    /// by its silhouette instead of by every edge it has.
    /// </remarks>
    private static void AddMeshRegions(Mesh mesh, double tolerance, List<PlanarRegion> regions)
    {
        if (mesh.Faces.Count == 0)
        {
            return;
        }

        mesh.FaceNormals.ComputeFaceNormals();

        var groupNormals = new List<Vector3d>();
        var groupOffsets = new List<double>();
        var groupFaces = new List<List<int>>();
        var planeTolerance = Math.Max(tolerance, RhinoMath.ZeroTolerance);

        for (var f = 0; f < mesh.Faces.Count; f++)
        {
            var normal = (Vector3d)mesh.FaceNormals[f];
            if (!normal.Unitize())
            {
                continue;
            }

            var anchor = new Point3d(mesh.Vertices[mesh.Faces[f].A]);
            var offset = normal * new Vector3d(anchor.X, anchor.Y, anchor.Z);

            var matched = -1;
            for (var g = 0; g < groupNormals.Count; g++)
            {
                if (groupNormals[g] * normal > 0.9999 &&
                    Math.Abs(groupOffsets[g] - offset) <= planeTolerance)
                {
                    matched = g;
                    break;
                }
            }

            if (matched < 0)
            {
                if (groupNormals.Count >= MaxRegionsPerBody)
                {
                    continue;
                }

                groupNormals.Add(normal);
                groupOffsets.Add(offset);
                groupFaces.Add(new List<int>());
                matched = groupNormals.Count - 1;
            }

            groupFaces[matched].Add(f);
        }

        var quantum = Math.Max(tolerance, RhinoMath.ZeroTolerance);
        for (var g = 0; g < groupFaces.Count; g++)
        {
            if (regions.Count >= MaxRegionsPerBody)
            {
                return;
            }

            foreach (var loop in BoundaryLoops(mesh, groupFaces[g], quantum))
            {
                var plane = new Plane(loop[0], groupNormals[g]);
                if (!plane.IsValid)
                {
                    continue;
                }

                var area = Math.Abs(PolygonArea(loop, plane));
                if (!(area > 0.0))
                {
                    continue;
                }

                // Same reason as the Brep path: the offset between two regions is read from
                // their origins, so an origin at a corner of the outline measures the corner.
                if (TryPolygonCentroid(loop, plane, out var centroid))
                {
                    plane.Origin = centroid;
                }

                var curve = new PolylineCurve(loop);
                regions.Add(new PlanarRegion
                {
                    Plane = plane,
                    Outline = curve,
                    Area = area,
                    Box = curve.GetBoundingBox(true)
                });
            }
        }
    }

    /// <summary>Closed boundaries of a set of mesh faces: the edges no second face of the set
    /// shares. A group split across the mesh yields one loop per piece, which is why no
    /// separate connectivity pass is needed.</summary>
    private static List<Polyline> BoundaryLoops(Mesh mesh, List<int> faces, double quantum)
    {
        var loops = new List<Polyline>();
        if (faces.Count == 0)
        {
            return loops;
        }

        var keyed = new Dictionary<(long, long, long), int>();
        var points = new List<Point3d>();

        int KeyOf(int vertex)
        {
            var p = mesh.Vertices[vertex];
            var key = (
                (long)Math.Round(p.X / quantum),
                (long)Math.Round(p.Y / quantum),
                (long)Math.Round(p.Z / quantum));
            if (keyed.TryGetValue(key, out var existing))
            {
                return existing;
            }

            keyed[key] = points.Count;
            points.Add(new Point3d(p));
            return points.Count - 1;
        }

        var used = new Dictionary<(int, int), int>();
        var directed = new List<(int From, int To)>();
        foreach (var f in faces)
        {
            var face = mesh.Faces[f];
            var corners = face.IsQuad
                ? new[] { KeyOf(face.A), KeyOf(face.B), KeyOf(face.C), KeyOf(face.D) }
                : new[] { KeyOf(face.A), KeyOf(face.B), KeyOf(face.C) };

            for (var i = 0; i < corners.Length; i++)
            {
                var from = corners[i];
                var to = corners[(i + 1) % corners.Length];
                if (from == to)
                {
                    continue;
                }

                var key = from < to ? (from, to) : (to, from);
                used.TryGetValue(key, out var count);
                used[key] = count + 1;
                directed.Add((from, to));
            }
        }

        var outgoing = new Dictionary<int, List<int>>();
        foreach (var (from, to) in directed)
        {
            var key = from < to ? (from, to) : (to, from);
            if (used[key] != 1)
            {
                continue;
            }

            if (!outgoing.TryGetValue(from, out var list))
            {
                list = new List<int>();
                outgoing[from] = list;
            }

            list.Add(to);
        }

        while (outgoing.Count > 0)
        {
            var start = outgoing.Keys.First();
            var loop = new Polyline();
            var current = start;
            var guard = directed.Count + 1;

            while (guard-- > 0 && outgoing.TryGetValue(current, out var next) && next.Count > 0)
            {
                loop.Add(points[current]);
                var following = next[0];
                next.RemoveAt(0);
                if (next.Count == 0)
                {
                    outgoing.Remove(current);
                }

                current = following;
                if (current == start)
                {
                    break;
                }
            }

            if (loop.Count >= MinPolygonPoints)
            {
                loop.Add(loop[0]);
                loops.Add(loop);
            }
        }

        return loops;
    }

    /// <summary>Area centroid of a closed polygon, in the plane it lies in.</summary>
    private static bool TryPolygonCentroid(Polyline loop, Plane plane, out Point3d centroid)
    {
        centroid = Point3d.Unset;
        double area = 0.0, cu = 0.0, cv = 0.0;
        for (var i = 0; i + 1 < loop.Count; i++)
        {
            plane.ClosestParameter(loop[i], out var u0, out var v0);
            plane.ClosestParameter(loop[i + 1], out var u1, out var v1);
            var cross = u0 * v1 - u1 * v0;
            area += cross;
            cu += (u0 + u1) * cross;
            cv += (v0 + v1) * cross;
        }

        if (Math.Abs(area) < RhinoMath.ZeroTolerance)
        {
            return false;
        }

        centroid = plane.PointAt(cu / (3.0 * area), cv / (3.0 * area));
        return centroid.IsValid;
    }

    private static double PolygonArea(IEnumerable<Point3d> loop, Plane plane)
    {
        double sum = 0.0;
        double firstU = 0.0, firstV = 0.0, prevU = 0.0, prevV = 0.0;
        var index = 0;
        foreach (var point in loop)
        {
            plane.ClosestParameter(point, out var u, out var v);
            if (index == 0)
            {
                firstU = u;
                firstV = v;
            }
            else
            {
                sum += prevU * v - u * prevV;
            }

            prevU = u;
            prevV = v;
            index++;
        }

        sum += prevU * firstV - firstU * prevV;
        return sum * 0.5;
    }

    /// <summary>
    /// The bearing between two bodies, as the polygon their flat faces share.
    /// </summary>
    /// <remarks>
    /// One condition covers all three ways two elements can be drawn to meet: the signed
    /// offset between the faces has to lie in <c>[-burial, gap]</c>. Almost touching is a
    /// small positive offset, perfect touching is zero, overlapping is negative, and none of
    /// them is special-cased - which is why this cannot have the fix-one-break-another
    /// behaviour that three separate patches to the sampler each had.
    ///
    /// The bearing sits on the <em>mean</em> plane of the pair, so a roof buried 20 mm into
    /// walls topping out at 2500 is measured at 2490 because that is where the shared surface
    /// is, not because of a rule about overlaps.
    /// </remarks>
    public static bool TryMeasure(
        IReadOnlyList<PlanarRegion> a,
        IReadOnlyList<PlanarRegion> b,
        BoundingBox boxA,
        BoundingBox boxB,
        GeometryBase solidA,
        Mesh proxyA,
        GeometryBase solidB,
        Mesh proxyB,
        double gap,
        double tolerance,
        out PlanarBearingResult result)
    {
        result = new PlanarBearingResult
        {
            RegionsA = a?.Count ?? 0,
            RegionsB = b?.Count ?? 0
        };

        if (a == null || b == null || a.Count == 0 || b.Count == 0)
        {
            return false;
        }

        var best = 0.0;
        foreach (var ra in a)
        {
            foreach (var rb in b)
            {
                var nA = ra.Plane.ZAxis;
                var nB = rb.Plane.ZAxis;
                if (nA * nB > -ParallelCosine)
                {
                    continue;
                }

                // The burial limit is a body's own thickness, so a face pairing with the far
                // face of the other body sits exactly on it. Exactly on it is where floating
                // point decides, and it decided differently for the same detail at different
                // places: two diagonals meeting at a truss apex measured as a 9743 mm2 planar
                // bearing at three nodes and as a line at two others, from geometry that is an
                // exact translation of itself. Pulling the limit in by the tolerance makes the
                // full-thickness pairing reject everywhere rather than by luck - and it is the
                // pairing least worth keeping, since it spans the whole member and describes
                // where two bodies cross inside one layer rather than where they bear.
                var offset = (rb.Plane.Origin - ra.Plane.Origin) * nA;
                var burial = BurialAllowance(a, ra, boxA, b, rb, boxB) - tolerance;
                if (offset > gap || offset < -burial)
                {
                    continue;
                }

                // Cheap reject before any curve work: two regions that do not overlap in plan
                // cannot share a polygon. The comparison has to be made in plan, so A's box
                // is first slid along the normal onto B's plane - left where it was, the test
                // rejects the pair for being exactly the distance apart that the offset test
                // has just accepted, which is how every chord-to-pad bearing on the bridge
                // came back unmeasured.
                var boxOfA = ra.Box;
                boxOfA.Transform(Transform.Translation(nA * offset));
                boxOfA.Inflate(gap + tolerance);
                var overlap = BoundingBox.Intersection(boxOfA, rb.Box);
                if (!overlap.IsValid)
                {
                    continue;
                }

                result.Pairs++;

                var mean = new Plane(ra.Plane.Origin + nA * (offset * 0.5), nA);
                if (!mean.IsValid)
                {
                    continue;
                }

                if (!TryShared(ra.Outline, rb.Outline, mean, tolerance, out var polygon, out var pieces))
                {
                    continue;
                }

                var area = Math.Abs(PolygonArea(polygon, mean));
                if (!(area > best))
                {
                    continue;
                }

                if (!TryRectangle(polygon, mean, out var frame, out var halfU, out var halfV))
                {
                    continue;
                }

                best = area;
                result.IsValid = true;
                result.Frame = frame;
                result.HalfU = halfU;
                result.HalfV = halfV;
                result.PolygonArea = area;
                result.Offset = offset;
                result.Pieces = pieces;
            }
        }

        if (!result.IsValid)
        {
            TryLineBearing(a, b, solidA, proxyA, solidB, proxyB, gap, tolerance, ref result);
        }

        return result.IsValid;
    }

    /// <summary>
    /// The line two crossing faces share, when no pair of them is parallel enough to bear.
    /// </summary>
    /// <remarks>
    /// Two flat faces meeting at an angle share a line, and that is not a failure to find an
    /// area - it is the geometry. A slab tilted on a wall bears on the wall's top edge; the
    /// area a deep overlap appears to offer exists only because the model was drawn through
    /// itself, and pulling the two apart along the direction they settle leaves the edge
    /// touching last.
    ///
    /// The normal is taken from the face the line runs <em>inside</em> - the surface being
    /// pressed into - and not from the bisector of the two normals. The bisector was tried on
    /// this project by fitting a plane to sampled points: on bridge diagonals landing on flat
    /// pads it reported 45 degrees, a direction neither surface points in, and a one-sided
    /// contact built on it pushed the truss 112 mm off supports with a 61 mm limit. Bisecting
    /// is right only when the line lies on the boundary of both faces, which is edge against
    /// edge, where neither body offers a surface to be pressed into.
    /// </remarks>
    private static void TryLineBearing(
        IReadOnlyList<PlanarRegion> a,
        IReadOnlyList<PlanarRegion> b,
        GeometryBase solidA,
        Mesh proxyA,
        GeometryBase solidB,
        Mesh proxyB,
        double gap,
        double tolerance,
        ref PlanarBearingResult result)
    {
        // The governing pair is the one closest to parallel, and only then the longest. A
        // wall's side face and a tilted slab's underside cross at 65 degrees over the wall's
        // full width, which is a corner being cut rather than a joint bearing; the wall's top
        // face crosses the same slab at 25 over the same width and is the contact. Ranking by
        // length alone picked the corner.
        var bestSkew = double.MaxValue;
        var bestLength = 0.0;
        foreach (var ra in a)
        {
            foreach (var rb in b)
            {
                var nA = ra.Plane.ZAxis;
                var nB = rb.Plane.ZAxis;

                // Anything the parallel pass would have taken is not a crossing.
                var alignment = Math.Abs(nA * nB);
                if (alignment > ParallelCosine)
                {
                    continue;
                }

                var skew = RhinoMath.ToDegrees(Math.Acos(Math.Min(1.0, alignment)));
                if (skew > bestSkew + RhinoMath.ZeroTolerance)
                {
                    continue;
                }

                var near = ra.Box;
                near.Inflate(gap + tolerance);
                if (!BoundingBox.Intersection(near, rb.Box).IsValid)
                {
                    continue;
                }

                if (!Intersection.PlanePlane(ra.Plane, rb.Plane, out var axis))
                {
                    continue;
                }

                result.Pairs++;

                var direction = axis.Direction;
                if (!direction.Unitize())
                {
                    continue;
                }

                if (!TryClip(axis.From, direction, ra, tolerance, out var fromA, out var toA) ||
                    !TryClip(axis.From, direction, rb, tolerance, out var fromB, out var toB))
                {
                    continue;
                }

                var from = Math.Max(fromA, fromB);
                var to = Math.Min(toA, toB);
                var length = to - from;
                if (!(length > tolerance))
                {
                    continue;
                }

                var closer = skew < bestSkew - RhinoMath.ZeroTolerance;
                if (!closer && !(length > bestLength))
                {
                    continue;
                }

                var start = axis.From + direction * from;
                var end = axis.From + direction * to;
                var middle = (start + end) * 0.5;

                var normal = PressedInto(ra, rb, middle, tolerance);
                var frame = new Plane(middle, direction, Vector3d.CrossProduct(normal, direction));
                if (!frame.IsValid)
                {
                    continue;
                }

                var bisector = nA * nB < 0.0 ? nA - nB : nA + nB;
                bisector.Unitize();

                bestSkew = skew;
                bestLength = length;
                result.IsValid = true;
                result.IsLine = true;
                result.Frame = frame;
                result.HalfU = length * 0.5;
                result.HalfV = 0.0;
                result.LineLength = length;
                result.PolygonArea = 0.0;
                var burial = Math.Max(Burial(ra, rb, tolerance), Burial(rb, ra, tolerance));
                result.Offset = -burial;
                result.Pieces = 1;
                result.SkewDegrees = skew;
                result.IsBuried = false;

                // Where the two are drawn through one another there is a real surface inside
                // the volume they share, and it is what an engineer means by the joint when
                // the overlap is deliberate. Reported in place of the line, with the line's
                // length and the burial kept beside it so the weaker reading stays visible.
                if (burial > tolerance &&
                    TryBuriedFace(ra, rb, solidA, proxyA, solidB, proxyB, tolerance, ref result))
                {
                    result.IsLine = false;
                    result.IsBuried = true;
                }

                result.BisectorDegrees = RhinoMath.ToDegrees(Math.Acos(
                    Math.Min(1.0, Math.Abs(frame.ZAxis * bisector))));
            }
        }
    }

    /// <summary>
    /// The stretch of the line lying within a region's outline, as distances along the line
    /// from <paramref name="origin"/>. Taken from the outermost crossings, so a face with a
    /// notch in it is measured across the notch rather than in pieces.
    /// </summary>
    /// <remarks>
    /// Measured from the intersection points rather than from curve parameters. A LineCurve's
    /// domain is not the length its parameters were assumed to be, and reading them as such
    /// gave a slab-on-wall contact 154 metres long.
    /// </remarks>
    /// <summary>
    /// The part of one body's face that lies inside the other body: the contact surface within
    /// the volume they share.
    /// </summary>
    /// <remarks>
    /// Each face is cut by the other solid - section the solid with the face's plane, and keep
    /// the part of the face inside that section. Both bodies offer one, and the smaller is
    /// taken: a socket transmits load through whichever of the two surfaces is smaller, and
    /// taking the larger would credit the joint with area the other side cannot match.
    /// </remarks>
    private static bool TryBuriedFace(
        in PlanarRegion ra, in PlanarRegion rb,
        GeometryBase solidA, Mesh proxyA, GeometryBase solidB, Mesh proxyB,
        double tolerance, ref PlanarBearingResult result)
    {
        var onA = TryFaceInsideSolid(ra, solidB, proxyB, tolerance);
        var onB = TryFaceInsideSolid(rb, solidA, proxyA, tolerance);

        // The smaller of the two, and nothing at all if neither side produced one.
        var chosen = onA;
        var plane = ra.Plane;
        if (onA == null || (onB != null && onB.Area < onA.Area))
        {
            chosen = onB;
            plane = rb.Plane;
        }

        if (chosen == null || !(chosen.Area > 0.0))
        {
            return false;
        }

        var polygon = Discretise(chosen.Outline, tolerance);
        if (polygon.Count < MinPolygonPoints)
        {
            return false;
        }

        var seat = new Plane(chosen.Centre, plane.ZAxis);
        if (!seat.IsValid || !TryRectangle(polygon, seat, out var frame, out var halfU, out var halfV))
        {
            return false;
        }

        result.Frame = frame;
        result.HalfU = halfU;
        result.HalfV = halfV;
        result.PolygonArea = chosen.Area;
        return true;
    }

    private sealed class BuriedFace
    {
        public Curve Outline;
        public double Area;
        public Point3d Centre;
    }

    /// <summary>The part of a region that lies within another solid, as a closed curve on the
    /// region's own plane.</summary>
    private static BuriedFace TryFaceInsideSolid(
        in PlanarRegion region, GeometryBase solid, Mesh proxy, double tolerance)
    {
        var sections = SectionCurves(solid, proxy, region.Plane, tolerance);
        if (sections.Count == 0)
        {
            return null;
        }

        BuriedFace best = null;
        foreach (var section in sections)
        {
            var shared = Curve.CreateBooleanIntersection(region.Outline, section, tolerance);
            if (shared == null)
            {
                continue;
            }

            foreach (var piece in shared.Where(p => p != null && p.IsClosed))
            {
                var properties = AreaMassProperties.Compute(piece, tolerance);
                var area = Math.Abs(properties?.Area ?? 0.0);
                if (!(area > 0.0) || (best != null && area <= best.Area))
                {
                    continue;
                }

                best = new BuriedFace
                {
                    Outline = piece,
                    Area = area,
                    Centre = properties.Centroid
                };
            }
        }

        return best;
    }

    /// <summary>Where a solid crosses a plane, as closed curves on that plane.</summary>
    private static List<Curve> SectionCurves(
        GeometryBase solid, Mesh proxy, Plane plane, double tolerance)
    {
        var curves = new List<Curve>();
        if (TryGetBrep(solid, out var brep) &&
            Intersection.BrepPlane(brep, plane, tolerance, out var brepCurves, out _) &&
            brepCurves != null)
        {
            curves.AddRange(brepCurves.Where(c => c != null && c.IsClosed));
        }

        if (curves.Count == 0 && proxy != null)
        {
            var polylines = Intersection.MeshPlane(proxy, plane);
            if (polylines != null)
            {
                curves.AddRange(polylines
                    .Where(p => p != null && p.Count > 2)
                    .Select(p => (Curve)new PolylineCurve(p))
                    .Where(c => c.IsClosed));
            }
        }

        return curves;
    }

    /// <summary>
    /// How far one face reaches past the other, where they cross.
    /// </summary>
    /// <remarks>
    /// A line contact says the same thing whether the two bodies touch along an edge or one
    /// has been drawn straight through the other, and those are not the same model. This is
    /// the difference: zero for a column leaning on its own base edge, and the burial depth
    /// for the same column rotated about its base centre, which drives it 141 mm into the pad
    /// and produces an apparent bearing area that exists only because of the overlap.
    ///
    /// Only corners that actually lie over the other face are counted. A pad's far corner sits
    /// deep inside the half-space under a leaning column's base plane while being nowhere near
    /// the column, and measuring that would report a burial of metres.
    /// </remarks>
    private static double Burial(in PlanarRegion of, in PlanarRegion into, double tolerance)
    {
        var deepest = 0.0;
        var plane = into.Plane;
        foreach (var point in Discretise(of.Outline, tolerance))
        {
            var depth = (point - plane.Origin) * plane.ZAxis;
            if (depth >= 0.0)
            {
                continue;
            }

            plane.ClosestParameter(point, out var u, out var v);
            if (into.Outline.Contains(plane.PointAt(u, v), plane, tolerance) !=
                PointContainment.Inside)
            {
                continue;
            }

            deepest = Math.Min(deepest, depth);
        }

        return -deepest;
    }

    private static bool TryClip(
        Point3d origin, Vector3d direction, in PlanarRegion region, double tolerance,
        out double from, out double to)
    {
        from = 0.0;
        to = 0.0;

        // The line as returned spans one unit of its own direction, which is nowhere near the
        // size of a building. Stretch it well past the region before asking where it crosses.
        var reach = region.Box.Diagonal.Length + 1.0;
        var stretched = new LineCurve(origin - direction * reach, origin + direction * reach);

        var events = Intersection.CurveCurve(
            region.Outline, stretched, tolerance, tolerance);
        if (events == null || events.Count == 0)
        {
            return false;
        }

        var low = double.MaxValue;
        var high = double.MinValue;
        foreach (var crossing in events)
        {
            // Both ends of every event, not only the first. Where the contact line runs along
            // an edge of the face - a column resting on its own base edge, which is what
            // landing at an angle looks like when nothing is drawn through anything - the
            // intersection is a single overlap event rather than two crossings, and reading
            // one point from it found no length and reported no contact at all.
            foreach (var point in new[] { crossing.PointB, crossing.PointB2 })
            {
                var along = (point - origin) * direction;
                low = Math.Min(low, along);
                high = Math.Max(high, along);
            }
        }

        from = low;
        to = high;
        return high > low;
    }

    /// <summary>
    /// The normal of whichever face the contact line runs inside, which is the face being
    /// pressed into. Falls back to the bisector when the line lies on the boundary of both,
    /// which is edge against edge.
    /// </summary>
    private static Vector3d PressedInto(
        in PlanarRegion ra, in PlanarRegion rb, Point3d middle, double tolerance)
    {
        var insideA = ra.Outline.Contains(middle, ra.Plane, tolerance) ==
            PointContainment.Inside;
        var insideB = rb.Outline.Contains(middle, rb.Plane, tolerance) ==
            PointContainment.Inside;

        if (insideA && !insideB)
        {
            return ra.Plane.ZAxis;
        }

        if (insideB && !insideA)
        {
            return rb.Plane.ZAxis;
        }

        if (insideA)
        {
            // Both: the larger face is the one that can be pressed into over the whole line.
            return ra.Area >= rb.Area ? ra.Plane.ZAxis : rb.Plane.ZAxis;
        }

        var bisector = ra.Plane.ZAxis * rb.Plane.ZAxis < 0.0
            ? ra.Plane.ZAxis - rb.Plane.ZAxis
            : ra.Plane.ZAxis + rb.Plane.ZAxis;
        return bisector.Unitize() ? bisector : ra.Plane.ZAxis;
    }

    /// <summary>
    /// How far one body may be buried in the other and still be read as bearing on it.
    /// </summary>
    /// <remarks>
    /// Stated from the geometry rather than as a constant, because there is no length that is
    /// right for both a 20 mm construction overlap and a truss node where two members
    /// genuinely pass through one another. Each body's own thickness at the face in question
    /// is that length: travel further than that from the face and you have come out the far
    /// side, so the plane is no longer between the two bodies at all.
    ///
    /// Thickness is read from the body's opposite face rather than from its bounding box,
    /// which is not a detail. A bridge brace drawn on a diagonal has a 150 mm section and a
    /// bounding box measuring 2978 mm along that same direction, and the box let two braces
    /// nearly three metres apart pair up as a bearing. The opposite face gives 150 either
    /// way the member is drawn.
    /// </remarks>
    private static double BurialAllowance(
        IReadOnlyList<PlanarRegion> a, in PlanarRegion ra, BoundingBox boxA,
        IReadOnlyList<PlanarRegion> b, in PlanarRegion rb, BoundingBox boxB)
    {
        return Math.Min(Thickness(a, ra, boxA), Thickness(b, rb, boxB));
    }

    /// <summary>How far into the body one may travel from this face before leaving it, taken
    /// from the nearest face pointing the other way. A body offering no opposite face is not
    /// prismatic there, and falls back to its bounding box.</summary>
    private static double Thickness(
        IReadOnlyList<PlanarRegion> regions, in PlanarRegion region, BoundingBox box)
    {
        var normal = region.Plane.ZAxis;
        var best = double.MaxValue;
        foreach (var other in regions)
        {
            if (other.Plane.ZAxis * normal > -ParallelCosine)
            {
                continue;
            }

            var depth = (region.Plane.Origin - other.Plane.Origin) * normal;
            if (depth > RhinoMath.ZeroTolerance && depth < best)
            {
                best = depth;
            }
        }

        return best < double.MaxValue ? best : Depth(box, normal);
    }

    private static double Depth(BoundingBox box, Vector3d normal)
    {
        var d = box.Diagonal;
        return Math.Abs(d.X * normal.X) + Math.Abs(d.Y * normal.Y) + Math.Abs(d.Z * normal.Z);
    }

    /// <summary>The polygon two outlines share, both projected onto the plane between them
    /// first so the boolean is asked a coplanar question rather than a nearly-coplanar one.</summary>
    private static bool TryShared(
        Curve outlineA, Curve outlineB, Plane mean, double tolerance,
        out List<Point3d> polygon, out int pieces)
    {
        polygon = null;
        pieces = 0;

        var projection = Transform.PlanarProjection(mean);
        var ca = outlineA.DuplicateCurve();
        var cb = outlineB.DuplicateCurve();
        ca.Transform(projection);
        cb.Transform(projection);

        var shared = Curve.CreateBooleanIntersection(ca, cb, tolerance);
        if (shared == null || shared.Length == 0)
        {
            return false;
        }

        pieces = shared.Length;

        // Largest piece governs. An L-shaped or split bearing does not fit one rectangle, and
        // merging the pieces would report a bearing across ground the bodies do not touch.
        Curve governing = null;
        var bestArea = 0.0;
        foreach (var piece in shared.Where(p => p != null && p.IsClosed))
        {
            var area = AreaMassProperties.Compute(piece, tolerance)?.Area ?? 0.0;
            if (Math.Abs(area) > bestArea)
            {
                bestArea = Math.Abs(area);
                governing = piece;
            }
        }

        if (governing == null || !(bestArea > 0.0))
        {
            return false;
        }

        polygon = Discretise(governing, tolerance);
        return polygon.Count >= MinPolygonPoints;
    }

    private static List<Point3d> Discretise(Curve curve, double tolerance)
    {
        if (curve.TryGetPolyline(out var polyline))
        {
            return polyline.ToList();
        }

        var points = new List<Point3d>();
        var parameters = curve.DivideByCount(64, true);
        if (parameters == null)
        {
            return points;
        }

        points.AddRange(parameters.Select(curve.PointAt));
        return points;
    }

    /// <summary>
    /// The smallest rectangle on the mean plane that contains the shared polygon.
    /// </summary>
    /// <remarks>
    /// Every candidate direction is a polygon edge, which is what makes a column on a pad read
    /// exactly 400 by 400 whatever angle it is drawn at - the sampled path fitted axes to a
    /// point cloud and gave 453, 536, 543 and 544 on four identical joints. The plane's own
    /// axes are tried as well so a polygon with no straight edges still gets an answer.
    /// </remarks>
    private static bool TryRectangle(
        List<Point3d> polygon, Plane mean, out Plane frame, out double halfU, out double halfV)
    {
        frame = Plane.Unset;
        halfU = 0.0;
        halfV = 0.0;

        var us = new double[polygon.Count];
        var vs = new double[polygon.Count];
        for (var i = 0; i < polygon.Count; i++)
        {
            mean.ClosestParameter(polygon[i], out us[i], out vs[i]);
        }

        var angles = new List<double> { 0.0 };
        for (var i = 0; i < polygon.Count; i++)
        {
            var j = (i + 1) % polygon.Count;
            var du = us[j] - us[i];
            var dv = vs[j] - vs[i];
            if (du * du + dv * dv > RhinoMath.ZeroTolerance)
            {
                angles.Add(Math.Atan2(dv, du));
            }
        }

        var bestArea = double.MaxValue;
        foreach (var angle in angles)
        {
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            double minA = double.MaxValue, maxA = double.MinValue;
            double minB = double.MaxValue, maxB = double.MinValue;
            for (var i = 0; i < polygon.Count; i++)
            {
                var pa = us[i] * cos + vs[i] * sin;
                var pb = -us[i] * sin + vs[i] * cos;
                minA = Math.Min(minA, pa);
                maxA = Math.Max(maxA, pa);
                minB = Math.Min(minB, pb);
                maxB = Math.Max(maxB, pb);
            }

            var area = (maxA - minA) * (maxB - minB);
            if (!(area < bestArea))
            {
                continue;
            }

            var axisU = mean.XAxis * cos + mean.YAxis * sin;
            var axisV = -mean.XAxis * sin + mean.YAxis * cos;
            var centreA = (minA + maxA) * 0.5;
            var centreB = (minB + maxB) * 0.5;
            var origin = mean.Origin + axisU * centreA + axisV * centreB;

            var candidate = new Plane(origin, axisU, axisV);
            if (!candidate.IsValid)
            {
                continue;
            }

            bestArea = area;
            frame = candidate;
            halfU = (maxA - minA) * 0.5;
            halfV = (maxB - minB) * 0.5;
        }

        return frame.IsValid && halfU > 0.0 && halfV > 0.0;
    }
}
