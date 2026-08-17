using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Rhino;

namespace RhinoMCPModPlugin;

internal static class KangarooRuntime
{
    private const string AssemblySimpleName = "KangarooSolver";
    private const string OverrideEnvironmentVariable = "RHINOMCP_KANGAROO_PATH";
    private static readonly object SyncRoot = new();
    private static bool _resolverInstalled;
    private static bool _initialized;
    private static bool _available;
    private static string _error;

    public static void InstallResolver()
    {
        lock (SyncRoot)
        {
            if (_resolverInstalled)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += ResolveKangarooAssembly;
            _resolverInstalled = true;
        }
    }

    public static bool EnsureAvailable(out string error)
    {
        lock (SyncRoot)
        {
            if (!_initialized)
            {
                _available = TryLoadKangaroo(out _error);
                _initialized = true;
            }

            error = _error;
            return _available;
        }
    }

    private static Assembly ResolveKangarooAssembly(object sender, ResolveEventArgs args)
    {
        AssemblyName requested;
        try
        {
            requested = new AssemblyName(args.Name);
        }
        catch
        {
            return null;
        }

        if (!string.Equals(requested.Name, AssemblySimpleName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        lock (SyncRoot)
        {
            var loaded = FindLoadedAssembly();
            if (loaded != null)
            {
                return loaded;
            }

            foreach (var path in CandidatePaths())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    return Assembly.LoadFrom(path);
                }
                catch
                {
                    // Try the next known Rhino/Grasshopper installation path.
                }
            }
        }

        return null;
    }

    private static bool TryLoadKangaroo(out string error)
    {
        if (FindLoadedAssembly() != null)
        {
            error = null;
            return true;
        }

        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var assembly = Assembly.LoadFrom(path);
                if (string.Equals(
                        assembly.GetName().Name,
                        AssemblySimpleName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = null;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = $"Failed to load Kangaroo from '{path}': {ex.Message}";
                return false;
            }
        }

        error =
            "KangarooSolver.dll was not found in the Rhino 8 Grasshopper installation. " +
            $"Set {OverrideEnvironmentVariable} to its absolute path and restart Rhino.";
        return false;
    }

    private static Assembly FindLoadedAssembly()
    {
        return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
            assembly => string.Equals(
                assembly.GetName().Name,
                AssemblySimpleName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        AddCandidate(candidates, seen, Environment.GetEnvironmentVariable(OverrideEnvironmentVariable));

        var rhinoAssemblyDirectory = Path.GetDirectoryName(typeof(RhinoApp).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(rhinoAssemblyDirectory))
        {
            AddCandidate(
                candidates,
                seen,
                Path.Combine(
                    rhinoAssemblyDirectory,
                    "ManagedPlugIns",
                    "GrasshopperPlugin.rhp",
                    "Components",
                    "KangarooSolver.dll"));
            AddCandidate(
                candidates,
                seen,
                Path.GetFullPath(Path.Combine(
                    rhinoAssemblyDirectory,
                    "..",
                    "Plug-ins",
                    "Grasshopper",
                    "Components",
                    "KangarooSolver.dll")));
        }

        AddCandidate(
            candidates,
            seen,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Rhino 8",
                "Plug-ins",
                "Grasshopper",
                "Components",
                "KangarooSolver.dll"));
        AddCandidate(
            candidates,
            seen,
            "/Applications/Rhino 8.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources/ManagedPlugIns/GrasshopperPlugin.rhp/Components/KangarooSolver.dll");

        return candidates;
    }

    private static void AddCandidate(List<string> candidates, HashSet<string> seen, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath))
            {
                candidates.Add(fullPath);
            }
        }
        catch
        {
            // Ignore malformed override paths and continue to installed locations.
        }
    }
}
