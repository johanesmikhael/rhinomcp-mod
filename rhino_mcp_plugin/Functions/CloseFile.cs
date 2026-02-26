using System;
using System.Collections.Generic;
using System.Threading;
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

        if (!TryCloseDocument(docSerialBeforeClose, pathBeforeClose))
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

    private static bool TryCloseDocument(uint docSerialBeforeClose, string pathBeforeClose)
    {
        var commands = new List<string>();

        if (!string.IsNullOrWhiteSpace(pathBeforeClose))
        {
            // On macOS, explicitly targeting the path avoids interactive close-path prompts.
            string escapedPath = pathBeforeClose.Replace("\"", "\"\"");
            commands.Add($"_-Close \"{escapedPath}\" _Enter");
            commands.Add($"-_Close \"{escapedPath}\" _Enter");
        }

        // Prefer explicit option assignment first to avoid interactive prompts.
        // If saveChanges is true, data is already saved above before we get here.
        commands.Add("_-Close _Save=_No _Enter");
        commands.Add("-_Close _Save=_No _Enter");
        commands.Add("_Close _Save=_No _Enter");
        // Legacy variants kept as fallback for command parser differences.
        commands.Add("_-Close _No _Enter");
        commands.Add("-_Close _No _Enter");
        commands.Add("_Close _No _Enter");
        commands.Add("_-Close _Enter");
        commands.Add("-_Close _Enter");
        commands.Add("_Close");

        foreach (string command in commands)
        {
            bool runSucceeded = RhinoApp.RunScript(command, false);
            if (!runSucceeded)
            {
                continue;
            }

            if (WaitForDocumentToClose(docSerialBeforeClose, 500))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WaitForDocumentToClose(uint docSerialBeforeClose, int timeoutMs)
    {
        if (IsDocumentClosed(docSerialBeforeClose))
        {
            return true;
        }

        DateTime timeoutAt = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < timeoutAt)
        {
            RhinoApp.Wait();
            if (IsDocumentClosed(docSerialBeforeClose))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return IsDocumentClosed(docSerialBeforeClose);
    }

    private static bool IsDocumentClosed(uint docSerialBeforeClose)
    {
        var activeDoc = RhinoDoc.ActiveDoc;
        return activeDoc == null || activeDoc.RuntimeSerialNumber != docSerialBeforeClose;
    }
}
