using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;

namespace RhinoMCPModPlugin.Commands
{
    public class MCPGraphExportCommand : Command
    {
        public MCPGraphExportCommand()
        {
            Instance = this;
        }

        public static MCPGraphExportCommand Instance { get; private set; }

        public override string EnglishName => "mcpmodgraphexport";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null)
            {
                RhinoApp.WriteLine("No active Rhino document.");
                return Result.Failure;
            }

            var graph = MCPConnectivityGraphController.GetOrComputeGraph(doc);
            if (graph.Nodes.Count == 0)
            {
                RhinoApp.WriteLine("Connectivity graph is empty; nothing to export.");
                return Result.Nothing;
            }

            if (!TryGetTargetPath(doc, mode, out var path))
            {
                return Result.Cancel;
            }

            try
            {
                var json = BuildGraphJson(doc, graph).ToString(Formatting.Indented);
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Failed to export connectivity graph: {ex.Message}");
                return Result.Failure;
            }

            RhinoApp.WriteLine(
                $"Connectivity graph exported to {path} (nodes: {graph.Nodes.Count}, edges: {graph.Edges.Count}).");
            return Result.Success;
        }

        private static bool TryGetTargetPath(RhinoDoc doc, RunMode mode, out string path)
        {
            var defaultName = DefaultFileName(doc);

            if (mode == RunMode.Scripted)
            {
                var fallback = Path.Combine(DefaultDirectory(doc), defaultName);
                var getString = new Rhino.Input.Custom.GetString();
                getString.SetCommandPrompt("Graph JSON output path");
                getString.SetDefaultString(fallback);
                getString.AcceptNothing(true);

                var result = getString.Get();
                if (result == GetResult.Nothing)
                {
                    path = fallback;
                }
                else if (result == GetResult.String)
                {
                    path = getString.StringResult()?.Trim();
                }
                else
                {
                    path = null;
                    return false;
                }
            }
            else
            {
                var dialog = new Rhino.UI.SaveFileDialog
                {
                    Title = "Export MCP connectivity graph",
                    Filter = "JSON files (*.json)|*.json",
                    DefaultExt = "json",
                    FileName = defaultName,
                    InitialDirectory = DefaultDirectory(doc)
                };

                if (!dialog.ShowSaveDialog())
                {
                    path = null;
                    return false;
                }

                path = dialog.FileName;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                RhinoApp.WriteLine("No output path given.");
                path = null;
                return false;
            }

            if (!Path.HasExtension(path))
            {
                path += ".json";
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                RhinoApp.WriteLine($"Directory does not exist: {directory}");
                path = null;
                return false;
            }

            path = Path.GetFullPath(path);
            return true;
        }

        private static string DefaultFileName(RhinoDoc doc)
        {
            var name = string.IsNullOrWhiteSpace(doc.Name)
                ? "untitled"
                : Path.GetFileNameWithoutExtension(doc.Name);
            return $"{name}_connectivity_graph.json";
        }

        private static string DefaultDirectory(RhinoDoc doc)
        {
            if (!string.IsNullOrWhiteSpace(doc.Path))
            {
                var directory = Path.GetDirectoryName(doc.Path);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    return directory;
                }
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private static JObject BuildGraphJson(RhinoDoc doc, MCPConnectivityGraph graph)
        {
            var nodes = new JArray();
            for (var i = 0; i < graph.Nodes.Count; i++)
            {
                var node = graph.Nodes[i];
                var obj = doc.Objects.FindId(node.ObjectId);
                nodes.Add(new JObject
                {
                    ["index"] = i,
                    ["guid"] = node.ObjectId.ToString(),
                    ["name"] = node.Name ?? string.Empty,
                    ["layer"] = obj == null ? string.Empty : doc.Layers[obj.Attributes.LayerIndex].FullPath,
                    ["object_type"] = obj?.Geometry?.GetType().Name ?? string.Empty,
                    ["center"] = Point(node.Center),
                    ["bbox_min"] = Point(node.BoundingBox.Min),
                    ["bbox_max"] = Point(node.BoundingBox.Max)
                });
            }

            var edges = new JArray();
            foreach (var edge in graph.Edges)
            {
                edges.Add(new JObject
                {
                    ["a"] = edge.A,
                    ["b"] = edge.B,
                    ["a_guid"] = graph.Nodes[edge.A].ObjectId.ToString(),
                    ["b_guid"] = graph.Nodes[edge.B].ObjectId.ToString(),
                    ["contact_point"] = Point(edge.ContactPoint)
                });
            }

            return new JObject
            {
                ["schema"] = "rhinomcp-mod/connectivity-graph/1",
                ["document"] = string.IsNullOrWhiteSpace(doc.Name) ? "untitled" : doc.Name,
                ["unit_system"] = doc.ModelUnitSystem.ToString(),
                ["tolerance"] = graph.Tolerance,
                ["node_count"] = graph.Nodes.Count,
                ["edge_count"] = graph.Edges.Count,
                ["nodes"] = nodes,
                ["edges"] = edges
            };
        }

        private static JArray Point(Point3d point)
        {
            if (!point.IsValid)
            {
                return new JArray();
            }

            return new JArray(
                Math.Round(point.X, 4),
                Math.Round(point.Y, 4),
                Math.Round(point.Z, 4));
        }
    }
}
