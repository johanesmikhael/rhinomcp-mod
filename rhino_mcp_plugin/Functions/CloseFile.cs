using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject CloseFile(JObject parameters)
    {
        bool saveChanges = parameters["save_changes"]?.ToObject<bool>() ?? false;
        string savePath = parameters["save_path"]?.ToObject<string>();

        JObject result = CloseActiveDocument(saveChanges, savePath);
        result["closed"] = true;
        return result;
    }

    private JObject CloseActiveDocument(bool saveChanges, string savePath)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
        {
            throw new Exception("No active Rhino document to close");
        }

        string pathBeforeClose = doc.Path ?? string.Empty;
        string nameBeforeClose = doc.Name ?? string.Empty;
        bool wasModified = doc.Modified;
        uint docSerialBeforeClose = doc.RuntimeSerialNumber;
        bool saved = false;
        string resolvedSavePath = null;

        if (saveChanges)
        {
            if (!string.IsNullOrWhiteSpace(savePath))
            {
                resolvedSavePath = System.IO.Path.GetFullPath(savePath);
                bool saveAsSucceeded = doc.SaveAs(resolvedSavePath);
                if (!saveAsSucceeded)
                {
                    throw new Exception($"Failed to save file as: {resolvedSavePath}");
                }
                saved = true;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(doc.Path))
                {
                    throw new Exception("Cannot save unnamed document without save_path");
                }

                bool saveSucceeded = doc.Save();
                if (!saveSucceeded)
                {
                    throw new Exception("Failed to save active document");
                }
                saved = true;
            }
        }
        else
        {
            // Avoid UI save prompts for non-interactive MCP calls.
            doc.Modified = false;
        }

        if (!TryCloseDocument(docSerialBeforeClose, saveChanges))
        {
            throw new Exception("Failed to close active document");
        }

        return new JObject
        {
            ["path"] = pathBeforeClose,
            ["name"] = nameBeforeClose,
            ["was_modified"] = wasModified,
            ["save_requested"] = saveChanges,
            ["saved"] = saved,
            ["save_path"] = resolvedSavePath
        };
    }

    private static bool TryCloseDocument(uint docSerialBeforeClose, bool saveChanges)
    {
        var commands = new List<string>();

        if (!saveChanges)
        {
            // Modified flag should already be cleared above; close without prompt options.
            commands.Add("_-Close _Enter");
            commands.Add("-_Close _Enter");
            commands.Add("_Close");
        }
        else
        {
            commands.Add("_-Close _Enter");
            commands.Add("-_Close _Enter");
            commands.Add("_Close");
        }

        foreach (string command in commands)
        {
            bool runSucceeded = RhinoApp.RunScript(command, false);
            if (!runSucceeded)
            {
                continue;
            }

            var activeDoc = RhinoDoc.ActiveDoc;
            if (activeDoc == null || activeDoc.RuntimeSerialNumber != docSerialBeforeClose)
            {
                return true;
            }
        }

        return false;
    }
}
