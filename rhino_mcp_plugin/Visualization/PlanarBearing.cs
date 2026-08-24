using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Geometry;

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
            var area = AreaMassProperties.Compute(outline, tolerance)?.Area ?? 0.0;
            if (!(Math.Abs(area) > 0.0))
            {
                continue;
            }

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

                var offset = (rb.Plane.Origin - ra.Plane.Origin) * nA;
                if (offset > gap || offset < -BurialAllowance(a, ra, boxA, b, rb, boxB))
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

        return result.IsValid;
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
