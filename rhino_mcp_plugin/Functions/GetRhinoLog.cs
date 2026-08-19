using Newtonsoft.Json.Linq;
using Rhino;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoMCPModPlugin.Functions
{
    public partial class RhinoMCPModFunctions
    {
        public JObject GetRhinoLog(JObject parameters)
        {
            int lines = 20;
            if (parameters["lines"] != null)
            {
                int.TryParse(parameters["lines"].ToString(), out lines);
                lines = Math.Max(1, Math.Min(100, lines));
            }

            var entries = new List<string>();
            var truncated = false;
            var totalLines = 0;

            try
            {
                // Read the command line pane directly rather than a history file: Rhino for
                // Mac never writes CommandHistory.txt, and the pane also carries what
                // commands printed, which a history file does not.
                var history = RhinoApp.CommandHistoryWindowText ?? string.Empty;
                var allLines = history
                    .Replace("\r\n", "\n")
                    .Replace('\r', '\n')
                    .Split('\n')
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();

                totalLines = allLines.Count;
                truncated = totalLines > lines;
                entries = allLines.Skip(Math.Max(0, totalLines - lines)).ToList();

                if (entries.Count == 0)
                {
                    entries.Add("Rhino's command history is empty.");
                }
            }
            catch (Exception e)
            {
                entries.Add($"Error reading command history: {e.Message}");
            }

            return new JObject
            {
                ["entries"] = new JArray(entries.ToArray()),
                ["count"] = entries.Count,
                ["total_lines"] = totalLines,
                ["truncated"] = truncated
            };
        }
    }
}
