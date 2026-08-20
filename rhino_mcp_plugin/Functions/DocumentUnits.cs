using Rhino;

namespace RhinoMCPModPlugin.Functions;

/// <summary>
/// Physical lengths expressed in whatever units the active document happens to use.
/// </summary>
/// <remarks>
/// A literal like 1.0 in a length comparison silently means one millimetre in a
/// millimetre document and one metre in a metre one. Several tolerances in this plugin
/// were written that way, which made them a thousand times looser or tighter depending on
/// a setting that has nothing to do with the question being asked - a pose comparison
/// meant to allow a tenth of a millimetre allowed a tenth of a metre instead.
///
/// Every fixed length should therefore be stated in real units and converted here.
/// Tolerances derived from ModelAbsoluteTolerance are already unit-relative and need no
/// conversion; this is only for constants that describe a physical size.
/// </remarks>
internal static class DocumentUnits
{
    /// <summary>The given length in millimetres, expressed in <paramref name="doc"/>'s units.</summary>
    public static double Millimetres(double millimetres, RhinoDoc doc = null)
    {
        doc ??= RhinoDoc.ActiveDoc;
        if (doc == null)
        {
            return millimetres;
        }

        return millimetres * RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
    }

    /// <summary>The given length in metres, expressed in <paramref name="doc"/>'s units.</summary>
    public static double Metres(double metres, RhinoDoc doc = null)
    {
        doc ??= RhinoDoc.ActiveDoc;
        if (doc == null)
        {
            return metres * 1000.0;
        }

        return metres * RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
    }

    /// <summary>
    /// The document's absolute tolerance, falling back to a millimetre when there is no
    /// document rather than to a bare literal whose meaning depends on the units.
    /// </summary>
    public static double AbsoluteTolerance(RhinoDoc doc = null)
    {
        doc ??= RhinoDoc.ActiveDoc;
        return doc?.ModelAbsoluteTolerance ?? Millimetres(1.0, doc);
    }
}
