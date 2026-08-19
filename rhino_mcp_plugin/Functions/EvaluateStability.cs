using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;
using KangarooSolver;
using KangarooSolver.Goals;
using rhinomcp_mod.Serializers;
using Rhino.DocObjects;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public const string GraphKey = "rhinomcp-mod:connectivity-graph";
    public const string EvaluationGraphKey = "rhinomcp-mod:connectivity-graph-eva";
    public const string StabilityKey = "rhinomcp.stability.v1";
    public const string AfterEvaluationKey = "rhinomcp.after_eva.v1";
    public const string EvaluationMode = "single_rigid_assembly";

    public const int DefaultCurrentStep = 50;
    public const double DefaultStabilityThresholdMeters = 0.01;
    public const double DefaultRigidStrength = 10000.0;
    public const double DefaultFloorStrength = 1000.0;
    public const double DefaultFloorZ = 0.0;

    // Share of the stability threshold that an auto-sized floor is allowed to spend on
    // settling. Keeping it at a tenth leaves the rest of the budget for real motion, while
    // staying clear of the ~1 mm residual that rigid-body compliance contributes anyway.
    public const double AutoFloorPenetrationFraction = 10.0;
    public const double DefaultGravity = 9.80665;
    public const double DefaultAssignToleranceMeters = 1e-6;
    public const double DefaultSolverThresholdMeters = 0.001;
    public const int DefaultSolverSubsteps = 1;
    private const int MaxCurrentStep = 10000;
    private const int MaxSolverSubsteps = 1000;
    private const int MaxTotalSolverSteps = 100000;

    public JObject EvaluateStability(JObject parameters)
    {
        try
        {
            if (!global::RhinoMCPModPlugin.KangarooRuntime.EnsureAvailable(out var kangarooError))
            {
                throw new InvalidOperationException($"Kangaroo solver is unavailable. {kangarooError}");
            }

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
            {
                throw new Exception("No active Rhino document.");
            }

            var unitContext = StabilityUnits.Create(doc.ModelUnitSystem);

            var graph = ReadGraph(parameters?["graph"], doc);
            var nodes = graph["n"] as JArray;
            if (nodes == null)
            {
                throw new Exception("Connectivity graph does not contain an 'n' array.");
            }
            if (nodes.Count == 0)
            {
                throw new Exception("Connectivity graph contains no nodes to evaluate.");
            }

            var stabilityNodes = new List<StabilityNode>();
            var nodeErrors = new List<string>();
            var unitWarnings = new JArray();
            for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                var nodeToken = nodes[nodeIndex];
                if (nodeToken is not JObject node)
                {
                    nodeErrors.Add($"node[{nodeIndex}] is not an object");
                    continue;
                }

                if (node["g"]?.ToString() is not string guidString || !Guid.TryParse(guidString, out var guid))
                {
                    nodeErrors.Add($"node[{nodeIndex}] has no valid object GUID");
                    continue;
                }

                var rhinoObject = doc.Objects.FindId(guid);
                if (rhinoObject == null)
                {
                    nodeErrors.Add($"node[{nodeIndex}] object {guidString} was not found");
                    continue;
                }

                var geometry = rhinoObject.Geometry;
                if (geometry == null)
                {
                    nodeErrors.Add($"node[{nodeIndex}] object {guidString} has no geometry");
                    continue;
                }

                JObject massSource = node;
                var userText = rhinoObject.Attributes.GetUserString(StabilityKey);
                if (!string.IsNullOrWhiteSpace(userText))
                {
                    try
                    {
                        var storedMass = JObject.Parse(userText);
                        if (storedMass["mass"] != null)
                        {
                            massSource = storedMass;
                        }
                    }
                    catch (Exception ex)
                    {
                        nodeErrors.Add(
                            $"node[{nodeIndex}] object {guidString} has invalid stored mass data: {ex.Message}");
                        continue;
                    }
                }

                if (!TryReadFiniteDouble(massSource["mass"], out var mass) || mass <= 0.0)
                {
                    nodeErrors.Add($"node[{nodeIndex}] object {guidString} needs a positive finite mass");
                    continue;
                }

                var massUnit = massSource["mass_unit"]?.ToString();
                if (string.IsNullOrWhiteSpace(massUnit))
                {
                    massUnit = StabilityUnits.InferLegacyMassUnit(doc.ModelUnitSystem);
                    unitWarnings.Add(
                        $"Object {guidString} has untagged legacy mass; interpreted as {massUnit}. Reassign mass to store canonical kg metadata.");
                }

                if (!StabilityUnits.TryMassToKilograms(mass, massUnit, out var massKilograms))
                {
                    nodeErrors.Add(
                        $"node[{nodeIndex}] object {guidString} has unsupported mass_unit '{massUnit}' or invalid mass");
                    continue;
                }

                node["mass"] = massKilograms;
                node["mass_unit"] = StabilityUnits.KilogramUnit;
                stabilityNodes.Add(new StabilityNode
                {
                    Node = node,
                    Geometry = geometry,
                    MassKilograms = massKilograms
                });
            }

            if (nodeErrors.Count > 0)
            {
                throw new Exception($"Connectivity graph is not evaluable: {string.Join("; ", nodeErrors)}");
            }

            var currentStep = ReadIntegerParameter(
                parameters, "current_step", DefaultCurrentStep, 1, MaxCurrentStep);
            var stabilityThreshold = ReadFiniteParameter(
                parameters,
                "stability_threshold",
                unitContext.FromMeters(DefaultStabilityThresholdMeters),
                0.0,
                inclusiveMinimum: true);
            var rigidStrength = ReadFiniteParameter(
                parameters, "rigid_strength", DefaultRigidStrength, 0.0, inclusiveMinimum: false);
            var floorZ = ReadFiniteParameter(parameters, "floor_z", DefaultFloorZ);
            var gravity = ReadFiniteParameter(
                parameters, "gravity", DefaultGravity, 0.0, inclusiveMinimum: true);

            // Floor2 is a linear contact spring, so a fixed strength lets a heavy assembly
            // sink far enough to exhaust the stability threshold on settling alone. When the
            // caller does not pin the strength down, size it from the assembly's own weight
            // so that settling stays within a small fraction of the threshold.
            var totalMassKilograms = stabilityNodes.Sum(node => node.MassKilograms);
            var stabilityThresholdMeters = stabilityThreshold * unitContext.LengthToMeters;
            var floorStrengthIsAuto = parameters?["floor_strength"] == null;
            var floorStrength = floorStrengthIsAuto
                ? StabilityUnits.AutoFloorStrength(
                    totalMassKilograms,
                    gravity,
                    stabilityThresholdMeters / AutoFloorPenetrationFraction,
                    DefaultFloorStrength)
                : ReadFiniteParameter(
                    parameters, "floor_strength", DefaultFloorStrength, 0.0, inclusiveMinimum: false);
            var assignTol = ReadFiniteParameter(
                parameters,
                "assign_tol",
                unitContext.FromMeters(DefaultAssignToleranceMeters),
                0.0,
                inclusiveMinimum: false);
            var threshold = ReadFiniteParameter(
                parameters,
                "threshold",
                unitContext.FromMeters(DefaultSolverThresholdMeters),
                0.0,
                inclusiveMinimum: false);
            var solverSubsteps = ReadIntegerParameter(
                parameters, "solver_substeps", DefaultSolverSubsteps, 1, MaxSolverSubsteps);
            if ((long)currentStep * solverSubsteps > MaxTotalSolverSteps)
            {
                throw new ArgumentOutOfRangeException(
                    "solver_substeps",
                    $"current_step * solver_substeps must not exceed {MaxTotalSolverSteps}.");
            }

            var stable = SolveFromGraph(
                graph,
                stabilityNodes,
                currentStep,
                stabilityThreshold,
                rigidStrength,
                floorStrength,
                unitContext.ToMeters(floorZ),
                gravity,
                unitContext.ToMeters(assignTol),
                unitContext.ToMeters(threshold),
                solverSubsteps,
                unitContext.LengthToMeters,
                out var finalXform);

            graph["stable"] = stable;
            graph["evaluation_mode"] = EvaluationMode;
            graph["document_length_unit"] = doc.ModelUnitSystem.ToString();
            graph["displacement_unit"] = doc.ModelUnitSystem.ToString();
            graph["length_to_meters"] = unitContext.LengthToMeters;
            graph["mass_unit"] = StabilityUnits.KilogramUnit;
            graph["gravity_m_s2"] = gravity;
            // Report the solver inputs that the caller did not necessarily supply, so a
            // result can be explained without re-running the evaluation to discover them.
            graph["total_mass_kg"] = totalMassKilograms;
            graph["floor_strength"] = floorStrength;
            graph["floor_strength_auto"] = floorStrengthIsAuto;
            graph["floor_z_m"] = unitContext.ToMeters(floorZ);
            graph["unit_warnings"] = unitWarnings.DeepClone();
            var evaluationGraph = SerializableGraph(graph);
            doc.Strings.SetString(EvaluationGraphKey, evaluationGraph.ToString());

            // Always rewrite the evaluated geometry cache with the latest simulation result.
            var displayRequested = parameters?["display"]?.ToString();
            var displayOn = string.Equals(displayRequested, "On", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(displayRequested, "on", StringComparison.OrdinalIgnoreCase) ||
                (parameters?["display"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && parameters["display"].Value<bool>() == true);
            var displayOff = string.Equals(displayRequested, "Off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(displayRequested, "off", StringComparison.OrdinalIgnoreCase) ||
                (parameters?["display"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && parameters["display"].Value<bool>() == false);

            ClearAfterEvaluationCache(doc);
            {
                try
                {
                    foreach (var sNode in stabilityNodes)
                    {
                        var guidStr = sNode.Node["g"]?.ToString();
                        if (string.IsNullOrWhiteSpace(guidStr) || !Guid.TryParse(guidStr, out var gid))
                            continue;

                        var obj = doc.Objects.FindId(gid);
                        if (obj == null || obj.Geometry == null)
                            continue;

                        // use the finalXform returned by the solver

                        // Duplicate geometry and apply transform
                        GeometryBase dup = null;
                        if (obj.Geometry is Brep br)
                        {
                            dup = br.DuplicateBrep();
                        }
                        else if (obj.Geometry is Mesh ms)
                        {
                            dup = ms.DuplicateMesh();
                        }
                        else if (obj.Geometry is Curve crv)
                        {
                            dup = crv.DuplicateCurve();
                        }
                        else if (obj.Geometry is Extrusion ex)
                        {
                            dup = ex.Duplicate();
                        }

                        if (dup == null)
                            continue;

                        try
                        {
                            dup.Transform(finalXform);
                        }
                        catch
                        {
                            // ignore transform failures
                        }

                        // Add temporary object to doc so Serializer can build outlines/obb
                        Guid tempId = Guid.Empty;
                        try
                        {
                            if (dup is Brep b)
                            {
                                tempId = doc.Objects.AddBrep(b);
                            }
                            else if (dup is Mesh m)
                            {
                                tempId = doc.Objects.AddMesh(m);
                            }
                            else if (dup is Curve c)
                            {
                                tempId = doc.Objects.AddCurve(c);
                            }
                            else if (dup is Extrusion ex)
                            {
                                tempId = doc.Objects.AddExtrusion(ex);
                            }
                            else
                            {
                                // fallback: try adding as generic geometry
                                tempId = doc.Objects.Add(dup);
                            }

                            if (tempId == Guid.Empty)
                                continue;

                            var tempObj = doc.Objects.FindId(tempId);
                            if (tempObj == null)
                                continue;

                                // Ensure a stored pose user-string exists so the serializer uses it
                                try
                                {
                                    JObject pose = GetOrBootstrapPose(tempObj);
                                    WriteStoredPose(tempObj, pose, invalidateObbCache: false);
                                }
                                catch
                                {
                                    // ignore pose caching failures
                                }

                            // Serialize geometry summary
                            JObject serial = Serializer.RhinoObject(tempObj, includeGeometrySummary: true, outlineMaxPoints: 64);
                            if (serial != null && serial["geometry"] is JObject geometry)
                            {
                                // build full mesh from transformed geometry and store alongside summary
                                var meshFull = AsMesh(dup);
                                if (meshFull != null)
                                {
                                    var verts = new JArray();
                                    foreach (var v in meshFull.Vertices)
                                    {
                                        verts.Add(new JArray { v.X, v.Y, v.Z });
                                    }

                                    var faces = new JArray();
                                    foreach (var f in meshFull.Faces)
                                    {
                                        if (f.IsTriangle)
                                        {
                                            faces.Add(new JArray { f.A, f.B, f.C });
                                        }
                                        else
                                        {
                                            faces.Add(new JArray { f.A, f.B, f.C, f.D });
                                        }
                                    }

                                    var fullMesh = new JObject
                                    {
                                        ["type"] = "MESH",
                                        ["vertices"] = verts,
                                        ["faces"] = faces
                                    };

                                    WriteAfterEvaluationFullGeometry(obj, geometry, fullMesh);
                                }
                                else
                                {
                                    // fallback: write only the summary
                                    WriteAfterEvaluationObb(obj, geometry);
                                }
                            }
                        }
                        catch
                        {
                            // ignore per-object failures
                        }
                        finally
                        {
                            if (tempId != Guid.Empty)
                            {
                                try { doc.Objects.Delete(tempId, true); } catch { }
                            }
                        }
                    }

                    if (displayOn)
                    {
                        global::RhinoMCPModPlugin.MCPStabilityController.SetEnabled(true);
                    }
                    else if (displayOff)
                    {
                        global::RhinoMCPModPlugin.MCPStabilityController.SetEnabled(false);
                    }

                    doc.Views.Redraw();
                }
                catch
                {
                    // swallow any caching/display errors
                }
            }

            var result = new JObject
            {
                ["success"] = true,
                ["stable"] = stable,
                ["evaluation_mode"] = EvaluationMode,
                ["node_count"] = stabilityNodes.Count,
                ["solver_iterations"] = currentStep * solverSubsteps,
                ["stability_threshold"] = stabilityThreshold,
                ["stability_threshold_m"] = unitContext.ToMeters(stabilityThreshold),
                ["document_length_unit"] = doc.ModelUnitSystem.ToString(),
                ["length_to_meters"] = unitContext.LengthToMeters,
                ["mass_unit"] = StabilityUnits.KilogramUnit,
                ["gravity_m_s2"] = gravity,
                ["total_mass_kg"] = totalMassKilograms,
                ["floor_strength"] = floorStrength,
                ["floor_strength_auto"] = floorStrengthIsAuto,
                ["floor_z_m"] = unitContext.ToMeters(floorZ),
                ["unit_warnings"] = unitWarnings,
                ["evaluation_graph_key"] = EvaluationGraphKey
            };

            if (graph["max_displacement"] != null)
            {
                result["max_displacement"] = graph["max_displacement"].Value<double?>();
            }
            if (graph["max_displacement_m"] != null)
            {
                result["max_displacement_m"] = graph["max_displacement_m"].Value<double?>();
            }

            return result;
        }
        catch (Exception ex)
        {
            return new JObject
            {
                ["success"] = false,
                ["message"] = ex.Message
            };
        }
    }

    private static bool TryReadFiniteDouble(JToken token, out double value)
    {
        value = 0.0;
        if (token == null)
        {
            return false;
        }

        try
        {
            value = token.Value<double>();
            return double.IsFinite(value);
        }
        catch
        {
            return false;
        }
    }

    private static int ReadIntegerParameter(
        JObject parameters,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        var token = parameters?[name];
        if (token == null)
        {
            return fallback;
        }

        int value;
        try
        {
            value = token.Value<int>();
        }
        catch
        {
            throw new ArgumentException($"{name} must be an integer.", name);
        }

        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                $"{name} must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static double ReadFiniteParameter(
        JObject parameters,
        string name,
        double fallback,
        double? minimum = null,
        bool inclusiveMinimum = true)
    {
        var token = parameters?[name];
        if (token == null)
        {
            return fallback;
        }

        if (!TryReadFiniteDouble(token, out var value))
        {
            throw new ArgumentException($"{name} must be a finite number.", name);
        }

        if (minimum.HasValue &&
            (inclusiveMinimum ? value < minimum.Value : value <= minimum.Value))
        {
            var comparison = inclusiveMinimum ? "greater than or equal to" : "greater than";
            throw new ArgumentOutOfRangeException(
                name,
                $"{name} must be {comparison} {minimum.Value}.");
        }

        return value;
    }

    private static JObject ReadGraph(JToken graphToken, RhinoDoc doc)
    {
        if (graphToken is JObject graphObject)
        {
            return graphObject;
        }

        var graphText = graphToken?.Type == JTokenType.String
            ? graphToken.Value<string>()
            : graphToken?.ToString();

        if (string.IsNullOrWhiteSpace(graphText))
        {
            graphText = doc.Strings.GetValue(GraphKey);
        }

        if (string.IsNullOrWhiteSpace(graphText))
        {
            throw new Exception($"Connectivity graph not found in Rhino document: {GraphKey}");
        }

        var parsed = JToken.Parse(graphText);
        if (parsed is JValue value && value.Type == JTokenType.String)
        {
            parsed = JToken.Parse(value.Value<string>() ?? string.Empty);
        }

        if (parsed is not JObject graph)
        {
            throw new Exception("Connectivity graph JSON must be an object.");
        }

        return graph;
    }
    private static void ClearAfterEvaluationCache(RhinoDoc doc)
    {
        if (doc == null)
        {
            return;
        }

        foreach (var obj in doc.Objects)
        {
            if (obj == null || obj.IsDeleted)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(AfterEvaluationKey)))
            {
                obj.Attributes.DeleteUserString(AfterEvaluationKey);
                obj.CommitChanges();
            }
        }
    }
    private static bool SolveFromGraph(
        JObject graph,
        List<StabilityNode> nodes,
        int currentStep,
        double stabilityThreshold,
        double rigidStrength,
        double floorStrength,
        double floorZMeters,
        double gravity,
        double assignToleranceMeters,
        double solverThresholdMeters,
        int solverSubsteps,
        double lengthToMeters,
        out Transform finalXform)
    {
        if (nodes.Count == 0)
        {
            throw new InvalidOperationException("No valid stability nodes were provided to the solver.");
        }

        graph["stable"] = false;
        graph["stability_threshold"] = stabilityThreshold;
        graph["stability_threshold_m"] = stabilityThreshold * lengthToMeters;
        graph["max_displacement"] = null;
        graph["max_displacement_m"] = null;

        var rigidMesh = new Mesh();
        var vertexPoints = new List<Point3d>();
        var gravityLoads = new List<(Point3d Point, double MassPerPoint)>();
        var collisionPoints = new List<Point3d>();

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
            if (mesh == null)
            {
                throw new InvalidOperationException(
                    $"Object {node.Node["g"]} could not be meshed in solver meter space.");
            }

            var points = MeshVerticesAsPoints(mesh);
            if (points.Count < 3)
            {
                throw new InvalidOperationException(
                    $"Object {node.Node["g"]} could not be converted to a solver mesh with at least three vertices.");
            }

            rigidMesh.Append(mesh);
            var massPerPoint = node.MassKilograms / points.Count;
            foreach (var point in points)
            {
                vertexPoints.Add(point);
                gravityLoads.Add((point, massPerPoint));
                collisionPoints.Add(point);
            }
        }

        if (vertexPoints.Count < 3 || rigidMesh.Vertices.Count == 0)
        {
            throw new InvalidOperationException("The assembly did not produce a valid solver mesh.");
        }

        rigidMesh.Normals.ComputeNormals();
        if (rigidMesh.Vertices.Count != vertexPoints.Count)
        {
            throw new Exception("Rigid mesh vertices and source points are not one-to-one.");
        }

        var frameIndices = FrameIndices(vertexPoints);
        if (frameIndices == null)
        {
            throw new InvalidOperationException("The assembly does not contain three non-collinear solver points.");
        }

        var seedP0 = vertexPoints[frameIndices.Value.Item1];
        var seedP1 = vertexPoints[frameIndices.Value.Item2];
        var seedP2 = vertexPoints[frameIndices.Value.Item3];
        var seedPlane = new Plane(seedP0, seedP1, seedP2);
        if (!seedPlane.IsValid)
        {
            throw new InvalidOperationException("The solver could not construct an initial assembly frame.");
        }

        // RigidBody2 adds solverPlane.Origin as PPos[0]. Keep this origin away
        // from the mesh reference vertex so it does not collapse onto PPos[1].
        var combinedBoundingBox = rigidMesh.GetBoundingBox(true);
        if (!combinedBoundingBox.IsValid)
        {
            throw new InvalidOperationException("The assembly solver mesh has no valid bounding box.");
        }

        var solverPlane = new Plane(
            combinedBoundingBox.Center,
            seedPlane.XAxis,
            seedPlane.YAxis);

        var bodyBrep = Brep.CreateFromMesh(rigidMesh, true);
        if (bodyBrep == null)
        {
            throw new InvalidOperationException("Kangaroo could not create a rigid body from the assembly mesh.");
        }

        // All source vertices intentionally form one welded rigid body.
        var rigidGoalPoints = new List<Point3d>(vertexPoints);
        var rbGoal = new RigidBody2(bodyBrep, solverPlane, rigidGoalPoints, rigidStrength);
        var goals = new List<IGoal> { rbGoal };

        foreach (var (point, massPerPoint) in gravityLoads)
        {
            goals.Add(new Unary(point, new Vector3d(0.0, 0.0, -gravity * massPerPoint)));
        }

        if (collisionPoints.Count > 0)
        {
            goals.Add(new Floor2(collisionPoints, floorStrength, floorZMeters));
        }

        var physicalSystem = new PhysicalSystem();
        foreach (var goal in goals)
        {
            physicalSystem.AssignPIndex(goal, assignToleranceMeters);
        }

        var initialRigidPositions = rbGoal.PPos;
        var initialRigidIndices = rbGoal.PIndex;
        if (initialRigidPositions == null || initialRigidIndices == null ||
            initialRigidPositions.Length != initialRigidIndices.Length ||
            initialRigidIndices.Length < vertexPoints.Count + 1)
        {
            throw new InvalidOperationException("Kangaroo returned an invalid rigid-body particle mapping.");
        }

        // PPos[0] is the orientation particle; PPos[1 + vertexIndex] is the
        // corresponding mesh vertex. Ignore duplicate global particles, then
        // select three distinct, non-collinear particles for transform recovery.
        var uniqueVertexRecords = new List<(int VertexIndex, int GlobalIndex, Point3d Point)>();
        var seenGlobalIndices = new HashSet<int>();
        for (var vertexIndex = 0; vertexIndex < vertexPoints.Count; vertexIndex++)
        {
            var globalIndex = initialRigidIndices[vertexIndex + 1];
            if (!seenGlobalIndices.Add(globalIndex))
            {
                continue;
            }

            uniqueVertexRecords.Add((vertexIndex, globalIndex, vertexPoints[vertexIndex]));
        }

        if (uniqueVertexRecords.Count < 3)
        {
            throw new InvalidOperationException("Kangaroo assigned fewer than three unique rigid-body particles.");
        }

        var tracking0 = uniqueVertexRecords[0];
        var tracking1 = uniqueVertexRecords[1];
        var farthestDistanceSquared = tracking1.Point.DistanceToSquared(tracking0.Point);
        for (var i = 2; i < uniqueVertexRecords.Count; i++)
        {
            var distanceSquared = uniqueVertexRecords[i].Point.DistanceToSquared(tracking0.Point);
            if (distanceSquared > farthestDistanceSquared)
            {
                farthestDistanceSquared = distanceSquared;
                tracking1 = uniqueVertexRecords[i];
            }
        }

        var trackingAxis = tracking1.Point - tracking0.Point;
        var tracking2Index = -1;
        var bestTrackingCrossSquared = -1.0;
        for (var i = 0; i < uniqueVertexRecords.Count; i++)
        {
            var candidate = uniqueVertexRecords[i];
            if (candidate.GlobalIndex == tracking0.GlobalIndex ||
                candidate.GlobalIndex == tracking1.GlobalIndex)
            {
                continue;
            }

            var cross = Vector3d.CrossProduct(trackingAxis, candidate.Point - tracking0.Point);
            if (cross.SquareLength > bestTrackingCrossSquared)
            {
                bestTrackingCrossSquared = cross.SquareLength;
                tracking2Index = i;
            }
        }

        if (tracking2Index < 0 ||
            IsDegenerateCross(bestTrackingCrossSquared, trackingAxis.SquareLength))
        {
            throw new InvalidOperationException("The solver could not select a non-collinear tracking frame.");
        }

        var tracking2 = uniqueVertexRecords[tracking2Index];
        var initialTrackingPlane = new Plane(tracking0.Point, tracking1.Point, tracking2.Point);
        if (!initialTrackingPlane.IsValid)
        {
            throw new InvalidOperationException("The solver's initial tracking frame is invalid.");
        }

        var globalP0 = tracking0.GlobalIndex;
        var globalP1 = tracking1.GlobalIndex;
        var globalP2 = tracking2.GlobalIndex;
        for (var step = 0; step < currentStep; step++)
        {
            for (var subStep = 0; subStep < solverSubsteps; subStep++)
            {
                physicalSystem.Step(goals, true, solverThresholdMeters);
            }
        }

        var positions = physicalSystem.GetPositionArray();
        if (globalP0 < 0 || globalP1 < 0 || globalP2 < 0 ||
            globalP0 >= positions.Length || globalP1 >= positions.Length || globalP2 >= positions.Length)
        {
            throw new InvalidOperationException("Kangaroo returned an incomplete final particle array.");
        }

        var nowP0 = positions[globalP0];
        var nowP1 = positions[globalP1];
        var nowP2 = positions[globalP2];
        var finalCross = Vector3d.CrossProduct(nowP1 - nowP0, nowP2 - nowP0);
        var initialTrackingCross = Vector3d.CrossProduct(
            tracking1.Point - tracking0.Point,
            tracking2.Point - tracking0.Point);
        if (!double.IsFinite(finalCross.SquareLength) ||
            finalCross.SquareLength <= Math.Max(1e-48, initialTrackingCross.SquareLength * 1e-20))
        {
            throw new InvalidOperationException("The final solver tracking frame collapsed.");
        }

        var nowPlane = new Plane(nowP0, nowP1, nowP2);
        if (!nowPlane.IsValid)
        {
            throw new InvalidOperationException("The solver's final tracking frame is invalid.");
        }

        var solverTransform = Transform.PlaneToPlane(initialTrackingPlane, nowPlane);
        finalXform = StabilityUnits.SolverTransformToDocument(solverTransform, lengthToMeters);
        return RecordNodeTransforms(
            nodes,
            finalXform,
            stabilityThreshold,
            lengthToMeters,
            graph);
    }

    private static bool RecordNodeTransforms(
        List<StabilityNode> nodes,
        Transform xform,
        double stabilityThreshold,
        double lengthToMeters,
        JObject graph)
    {
        var maxDisplacement = 0.0;
        var rotation = RotationFromTransform(xform);
        var matrix = TransformMatrix(xform);

        foreach (var node in nodes)
        {
            if (!TryGeometryCenter(node.Geometry, out var center))
            {
                var displacement = Vector3d.Zero;
                node.Node["displacement"] = new JObject
                {
                    ["x"] = displacement.X,
                    ["y"] = displacement.Y,
                    ["z"] = displacement.Z,
                    ["length"] = 0.0,
                    ["length_m"] = 0.0
                };
                node.Node.Remove("rotation_degrees");
                node.Node["rotation"] = rotation;
                node.Node["transform"] = matrix;
                continue;
            }

            var movedCenter = new Point3d(center);
            movedCenter.Transform(xform);
            var movedDisplacement = movedCenter - center;
            var displacementLength = movedDisplacement.Length;
            maxDisplacement = Math.Max(maxDisplacement, displacementLength);

            node.Node["displacement"] = new JObject
            {
                ["x"] = movedDisplacement.X,
                ["y"] = movedDisplacement.Y,
                ["z"] = movedDisplacement.Z,
                ["length"] = displacementLength,
                ["length_m"] = displacementLength * lengthToMeters
            };
            node.Node.Remove("rotation_degrees");
            node.Node["rotation"] = rotation;
            node.Node["transform"] = matrix;
        }

        var maxDisplacementMeters = maxDisplacement * lengthToMeters;
        var stabilityThresholdMeters = stabilityThreshold * lengthToMeters;
        graph["stable"] = maxDisplacementMeters <= stabilityThresholdMeters;
        graph["stability_threshold"] = stabilityThreshold;
        graph["stability_threshold_m"] = stabilityThresholdMeters;
        graph["max_displacement"] = maxDisplacement;
        graph["max_displacement_m"] = maxDisplacementMeters;
        return graph["stable"].Value<bool>();
    }

    private static JObject RotationFromTransform(Transform xform)
    {
        var cosAngle = (xform.M00 + xform.M11 + xform.M22 - 1.0) * 0.5;
        cosAngle = Math.Max(-1.0, Math.Min(1.0, cosAngle));
        var angle = Math.Acos(cosAngle);

        var axis = Vector3d.Zero;
        if (angle <= 1e-10)
        {
            axis = Vector3d.Zero;
        }
        else if (Math.Abs(Math.PI - angle) <= 1e-6)
        {
            var x = Math.Sqrt(Math.Max(0.0, (xform.M00 + 1.0) * 0.5));
            var y = Math.Sqrt(Math.Max(0.0, (xform.M11 + 1.0) * 0.5));
            var z = Math.Sqrt(Math.Max(0.0, (xform.M22 + 1.0) * 0.5));
            if (x >= y && x >= z && x > 1e-10)
            {
                y = (xform.M01 + xform.M10) / (4.0 * x);
                z = (xform.M02 + xform.M20) / (4.0 * x);
            }
            else if (y >= z && y > 1e-10)
            {
                x = (xform.M01 + xform.M10) / (4.0 * y);
                z = (xform.M12 + xform.M21) / (4.0 * y);
            }
            else if (z > 1e-10)
            {
                x = (xform.M02 + xform.M20) / (4.0 * z);
                y = (xform.M12 + xform.M21) / (4.0 * z);
            }

            axis = new Vector3d(x, y, z);
            axis.Unitize();
        }
        else
        {
            var scale = 2.0 * Math.Sin(angle);
            axis = new Vector3d(
                (xform.M21 - xform.M12) / scale,
                (xform.M02 - xform.M20) / scale,
                (xform.M10 - xform.M01) / scale);
            axis.Unitize();
        }

        return new JObject
        {
            ["angle_degrees"] = angle * 180.0 / Math.PI,
            ["axis"] = new JObject
            {
                ["x"] = axis.X,
                ["y"] = axis.Y,
                ["z"] = axis.Z
            }
        };
    }

    private static JArray TransformMatrix(Transform xform)
    {
        var matrix = new JArray();
        matrix.Add(new JArray(xform.M00, xform.M01, xform.M02, xform.M03));
        matrix.Add(new JArray(xform.M10, xform.M11, xform.M12, xform.M13));
        matrix.Add(new JArray(xform.M20, xform.M21, xform.M22, xform.M23));
        matrix.Add(new JArray(xform.M30, xform.M31, xform.M32, xform.M33));
        return matrix;
    }

    private static JObject SerializableGraph(JObject graph)
    {
        var result = (JObject)graph.DeepClone();
        var nodes = result["n"] as JArray;
        if (nodes != null)
        {
            var serializableNodes = new JArray();
            foreach (var nodeToken in nodes)
            {
                if (nodeToken is not JObject node)
                {
                    continue;
                }

                var storedNode = (JObject)node.DeepClone();
                storedNode.Remove("geo");
                serializableNodes.Add(storedNode);
            }

            result["n"] = serializableNodes;
        }

        return result;
    }

    private static List<Point3d> MeshVerticesAsPoints(Mesh mesh)
    {
        var points = new List<Point3d>();
        if (mesh == null)
        {
            return points;
        }

        foreach (var vertex in mesh.Vertices)
        {
            points.Add(new Point3d(vertex.X, vertex.Y, vertex.Z));
        }

        return points;
    }

    private static bool TryGeometryCenter(GeometryBase geometry, out Point3d center)
    {
        center = Point3d.Unset;
        if (geometry == null)
        {
            return false;
        }

        try
        {
            var bbox = geometry.GetBoundingBox(true);
            if (bbox.IsValid)
            {
                center = bbox.Center;
                return true;
            }
        }
        catch
        {
            // ignore and fall back
        }

        return false;
    }

    private static Mesh AsMesh(GeometryBase geometry)
    {
        if (geometry == null)
        {
            return null;
        }

        if (geometry is Mesh mesh)
        {
            return mesh.DuplicateMesh();
        }

        var brep = AsBrep(geometry);
        if (brep == null)
        {
            return null;
        }

        var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.Default);
        if (meshes == null || meshes.Length == 0)
        {
            return null;
        }

        var result = new Mesh();
        foreach (var part in meshes)
        {
            result.Append(part);
        }

        if (result.Vertices.Count == 0)
        {
            return null;
        }

        result.Normals.ComputeNormals();
        result.Compact();
        return result;
    }

    private static Brep AsBrep(GeometryBase geometry)
    {
        if (geometry == null)
        {
            return null;
        }

        switch (geometry)
        {
            case Brep brep:
                return brep.DuplicateBrep();
            case Extrusion extrusion:
                return extrusion.ToBrep();
            case Surface surface:
                return surface.ToBrep();
            case Mesh mesh:
                return Brep.CreateFromMesh(mesh, true);
            case Curve curve:
                var planarBreps = Brep.CreatePlanarBreps(curve, 0.001);
                return planarBreps != null && planarBreps.Length > 0 ? planarBreps[0] : null;
            default:
                return null;
        }
    }

    private static (int Item1, int Item2, int Item3)? FrameIndices(List<Point3d> points)
    {
        if (points.Count < 3)
        {
            return null;
        }

        // Match the Python reference-frame selection exactly.
        var i0 = 0;
        var p0 = points[i0];
        var i1 = 0;
        var maxDistanceSquared = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var distanceSquared = points[i].DistanceToSquared(p0);
            if (distanceSquared > maxDistanceSquared)
            {
                maxDistanceSquared = distanceSquared;
                i1 = i;
            }
        }

        var xAxis = points[i1] - p0;
        if (!double.IsFinite(maxDistanceSquared) || maxDistanceSquared <= 1e-30)
        {
            return null;
        }

        var i2 = -1;
        var maxCross = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            if (i == i0 || i == i1)
            {
                continue;
            }

            var cross = Vector3d.CrossProduct(xAxis, points[i] - p0);
            if (cross.SquareLength > maxCross)
            {
                maxCross = cross.SquareLength;
                i2 = i;
            }
        }

        if (i2 < 0 || IsDegenerateCross(maxCross, maxDistanceSquared))
        {
            return null;
        }

        return (i0, i1, i2);
    }

    private static bool IsDegenerateCross(double crossSquareLength, double referenceLengthSquared)
    {
        if (!double.IsFinite(crossSquareLength) ||
            !double.IsFinite(referenceLengthSquared) ||
            referenceLengthSquared <= 0.0)
        {
            return true;
        }

        // Cross-product squared has units L^4. Use a relative test so meter
        // normalization does not reject valid small models that were authored
        // in millimeters or inches.
        return crossSquareLength <= Math.Max(
            1e-48,
            referenceLengthSquared * referenceLengthSquared * 1e-24);
    }

    private static void WriteAfterEvaluationFullGeometry(RhinoObject obj, JObject geometry, JObject fullMesh)
    {
        if (obj == null || geometry == null || fullMesh == null)
        {
            return;
        }

        var payload = new JObject
        {
            ["geometry"] = geometry,
            ["full_mesh"] = fullMesh
        };

        obj.Attributes.SetUserString(AfterEvaluationKey, payload.ToString(Newtonsoft.Json.Formatting.None));
        obj.CommitChanges();
    }

    private static void WriteAfterEvaluationObb(RhinoObject obj, JObject geometry)
    {
        if (obj == null || geometry == null)
        {
            return;
        }

        var payload = new JObject
        {
            ["geometry"] = geometry
        };

        obj.Attributes.SetUserString(AfterEvaluationKey, payload.ToString(Newtonsoft.Json.Formatting.None));
        obj.CommitChanges();
    }

    private sealed class StabilityNode
    {
        public JObject Node { get; set; }
        public GeometryBase Geometry { get; set; }
        public double MassKilograms { get; set; }
    }
}
