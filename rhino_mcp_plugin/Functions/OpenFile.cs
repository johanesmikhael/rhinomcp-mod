using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject OpenFile(JObject parameters)
    {
        bool hasPath = parameters.ContainsKey("path");
        string path = hasPath ? castToString(parameters.SelectToken("path")) : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new Exception("path is required");
        }

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new Exception($"File does not exist: {fullPath}");
        }

        bool closeCurrent = parameters["close_current"]?.ToObject<bool>() ?? false;
        bool saveCurrent = parameters["save_current"]?.ToObject<bool>() ?? false;

        string previousPath = RhinoDoc.ActiveDoc?.Path ?? string.Empty;

        if (closeCurrent)
        {
            CloseActiveDocument(saveCurrent, null);
        }

        bool wasAlreadyOpen;
        var openedDoc = RhinoDoc.Open(fullPath, out wasAlreadyOpen);
        if (openedDoc == null)
        {
            throw new Exception($"Failed to open file: {fullPath}");
        }

        string openedPath = string.IsNullOrWhiteSpace(openedDoc.Path) ? fullPath : openedDoc.Path;
        string openedName = string.IsNullOrWhiteSpace(openedDoc.Name) ? Path.GetFileName(fullPath) : openedDoc.Name;
        return new JObject
        {
            ["opened"] = true,
            ["path"] = openedPath,
            ["name"] = openedName,
            ["was_already_open"] = wasAlreadyOpen,
            ["closed_previous"] = closeCurrent,
            ["saved_previous"] = closeCurrent && saveCurrent,
            ["previous_path"] = previousPath
        };
    }
}
