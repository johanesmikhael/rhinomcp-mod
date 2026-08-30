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

        // Hold the outgoing document by reference, serial and path before anything moves.
        // Closing it later goes through those rather than through "the active document",
        // because by then the active document is meant to be the new one.
        var previousDoc = RhinoDoc.ActiveDoc;
        string previousPath = previousDoc?.Path ?? string.Empty;
        uint previousSerial = previousDoc?.RuntimeSerialNumber ?? 0;

        // Open first, close second.
        //
        // The other order closed the document and then opened the file, and between those
        // two steps Rhino holds no document at all. If the open does not leave one active,
        // every later call fails on a null ActiveDoc with nothing to say why, and the
        // session cannot be recovered from the socket. Opening first means there is never a
        // moment with nothing open.
        bool wasAlreadyOpen;
        var openedDoc = RhinoDoc.Open(fullPath, out wasAlreadyOpen);
        if (openedDoc == null)
        {
            throw new Exception($"Failed to open file: {fullPath}");
        }

        // RhinoDoc.Open hands back the document it opened, which is not the same claim as
        // that document being active. Rhino for Mac holds several at once, and every other
        // tool here resolves its scope through RhinoDoc.ActiveDoc - so an open that did not
        // switch leaves the caller reading, and writing, a model it did not ask for. Saying
        // so is the whole point: a wrong document that announces itself is a bug, one that
        // does not is a hazard.
        var activeDoc = RhinoDoc.ActiveDoc;
        if (activeDoc == null)
        {
            throw new Exception(
                $"Opened {fullPath} but Rhino has no active document. Open a file in Rhino " +
                "to continue; the session cannot be recovered from here.");
        }

        if (activeDoc.RuntimeSerialNumber != openedDoc.RuntimeSerialNumber)
        {
            throw new Exception(
                $"Opened {openedDoc.Name ?? fullPath} but the active document is still " +
                $"{activeDoc.Name ?? activeDoc.Path}. Every other tool works on the active " +
                "document, so continuing would read and write the wrong model. Retry with " +
                "close_current=true, which switches reliably because it leaves Rhino one " +
                "document to make active - but save first if the current document matters, " +
                "since save_current=false discards its changes.");
        }

        var closedPrevious = false;
        var savedPrevious = false;
        string closeSkippedReason = null;
        if (closeCurrent && previousDoc != null &&
            previousSerial != openedDoc.RuntimeSerialNumber)
        {
            if (string.IsNullOrWhiteSpace(previousPath))
            {
                // An unsaved document has no path to close by name, and the fallbacks close
                // whichever document is active - which is now the one just opened. Leaving
                // it open is the lesser harm, and the caller is told rather than left to
                // wonder why it is still there.
                closeSkippedReason =
                    "Previous document was never saved and has no path; closing by name is " +
                    "not possible and closing by position would close the file just opened.";
            }
            else
            {
                if (saveCurrent)
                {
                    if (!previousDoc.Save())
                    {
                        throw new Exception($"Failed to save previous document: {previousPath}");
                    }

                    savedPrevious = true;
                }
                else
                {
                    // Keeps Rhino from raising a save prompt no MCP caller can answer.
                    previousDoc.Modified = false;
                }

                closedPrevious = TryCloseDocument(previousSerial, previousPath);
                if (!closedPrevious)
                {
                    closeSkippedReason = $"Failed to close previous document: {previousPath}";
                }
            }
        }

        string openedPath = string.IsNullOrWhiteSpace(openedDoc.Path) ? fullPath : openedDoc.Path;
        string openedName = string.IsNullOrWhiteSpace(openedDoc.Name) ? Path.GetFileName(fullPath) : openedDoc.Name;
        var result = new JObject
        {
            ["opened"] = true,
            ["path"] = openedPath,
            ["name"] = openedName,
            // The document every subsequent call will act on, stated outright so a caller
            // never has to infer it from "opened".
            ["active_path"] = RhinoDoc.ActiveDoc?.Path ?? string.Empty,
            ["active_name"] = RhinoDoc.ActiveDoc?.Name ?? string.Empty,
            ["was_already_open"] = wasAlreadyOpen,
            ["closed_previous"] = closedPrevious,
            ["saved_previous"] = savedPrevious,
            ["previous_path"] = previousPath
        };
        if (closeSkippedReason != null)
        {
            result["close_previous_skipped"] = closeSkippedReason;
        }

        return result;
    }
}
