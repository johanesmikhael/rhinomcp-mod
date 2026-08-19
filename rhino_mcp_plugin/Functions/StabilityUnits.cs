using System;
using Rhino;
using Rhino.Geometry;

namespace RhinoMCPModPlugin.Functions;

internal sealed class StabilityUnitContext
{
    public StabilityUnitContext(UnitSystem documentUnitSystem, double lengthToMeters)
    {
        DocumentUnitSystem = documentUnitSystem;
        LengthToMeters = lengthToMeters;
    }

    public UnitSystem DocumentUnitSystem { get; }
    public double LengthToMeters { get; }
    public bool UsesImperialInput => StabilityUnits.IsImperialInput(DocumentUnitSystem);
    public string MassInputUnit => UsesImperialInput ? StabilityUnits.PoundMassUnit : StabilityUnits.KilogramUnit;
    public string DensityInputUnit => UsesImperialInput
        ? StabilityUnits.PoundMassPerCubicFootUnit
        : StabilityUnits.KilogramPerCubicMeterUnit;

    public double ToMeters(double documentLength) =>
        StabilityUnitMath.ToMeters(documentLength, LengthToMeters);
    public double FromMeters(double meters) =>
        StabilityUnitMath.FromMeters(meters, LengthToMeters);
}

internal static class StabilityUnits
{
    public const string KilogramUnit = StabilityUnitMath.KilogramUnit;
    public const string PoundMassUnit = StabilityUnitMath.PoundMassUnit;
    public const string KilogramPerCubicMeterUnit = StabilityUnitMath.KilogramPerCubicMeterUnit;
    public const string PoundMassPerCubicFootUnit = StabilityUnitMath.PoundMassPerCubicFootUnit;
    public const double KilogramsPerPoundMass = StabilityUnitMath.KilogramsPerPoundMass;
    public const double FeetToMeters = StabilityUnitMath.FeetToMeters;
    public const double CubicFeetToCubicMeters = StabilityUnitMath.CubicFeetToCubicMeters;

    public static bool TryCreate(UnitSystem unitSystem, out StabilityUnitContext context, out string error)
    {
        context = null;
        error = null;

        if (unitSystem is UnitSystem.None or UnitSystem.Unset or UnitSystem.CustomUnits)
        {
            error =
                $"Rhino document unit system '{unitSystem}' cannot be normalized to meters. " +
                "Choose a standard Rhino model unit before evaluating stability or deriving mass from density.";
            return false;
        }

        var lengthToMeters = RhinoMath.UnitScale(unitSystem, UnitSystem.Meters);
        if (!double.IsFinite(lengthToMeters) || lengthToMeters <= 0.0)
        {
            error = $"Rhino document unit system '{unitSystem}' has no valid conversion to meters.";
            return false;
        }

        context = new StabilityUnitContext(unitSystem, lengthToMeters);
        return true;
    }

    public static StabilityUnitContext Create(UnitSystem unitSystem)
    {
        if (!TryCreate(unitSystem, out var context, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return context;
    }

    public static bool IsImperialInput(UnitSystem unitSystem)
    {
        return unitSystem is
            UnitSystem.Microinches or
            UnitSystem.Mils or
            UnitSystem.Inches or
            UnitSystem.Feet or
            UnitSystem.Yards or
            UnitSystem.Miles or
            UnitSystem.PrinterPoints or
            UnitSystem.PrinterPicas;
    }

    public static string PreferredMassInputUnit(UnitSystem unitSystem)
    {
        return IsImperialInput(unitSystem) ? PoundMassUnit : KilogramUnit;
    }

    public static string PreferredDensityInputUnit(UnitSystem unitSystem)
    {
        return IsImperialInput(unitSystem)
            ? PoundMassPerCubicFootUnit
            : KilogramPerCubicMeterUnit;
    }

    public static string InferLegacyMassUnit(UnitSystem unitSystem)
    {
        return unitSystem == UnitSystem.Feet ? PoundMassUnit : KilogramUnit;
    }

    public static string InferLegacyDensityUnit(UnitSystem unitSystem)
    {
        return unitSystem == UnitSystem.Feet
            ? PoundMassPerCubicFootUnit
            : KilogramPerCubicMeterUnit;
    }

    public static bool TryMassToKilograms(double value, string unit, out double kilograms)
    {
        return StabilityUnitMath.TryMassToKilograms(value, unit, out kilograms);
    }

    public static double KilogramsToInputMass(double kilograms, string unit)
    {
        return StabilityUnitMath.KilogramsToInputMass(kilograms, unit);
    }

    public static double AutoFloorStrength(
        double totalMassKilograms,
        double gravity,
        double targetPenetrationMeters,
        double fallback)
    {
        return StabilityUnitMath.AutoFloorStrength(
            totalMassKilograms, gravity, targetPenetrationMeters, fallback);
    }

    public static bool TryDensityToKilogramsPerCubicMeter(
        double value,
        string unit,
        out double kilogramsPerCubicMeter)
    {
        return StabilityUnitMath.TryDensityToKilogramsPerCubicMeter(
            value,
            unit,
            out kilogramsPerCubicMeter);
    }

    public static double KilogramsPerCubicMeterToInputDensity(
        double kilogramsPerCubicMeter,
        string unit)
    {
        return StabilityUnitMath.KilogramsPerCubicMeterToInputDensity(
            kilogramsPerCubicMeter,
            unit);
    }

    public static Transform SolverTransformToDocument(Transform solverTransform, double lengthToMeters)
    {
        // S^-1 * T_solver * S. PlaneToPlane is rigid, so the rotation is
        // unchanged and only translation needs conversion back to model units.
        var documentTransform = solverTransform;
        documentTransform.M03 = StabilityUnitMath.SolverTranslationToDocument(
            solverTransform.M03, lengthToMeters);
        documentTransform.M13 = StabilityUnitMath.SolverTranslationToDocument(
            solverTransform.M13, lengthToMeters);
        documentTransform.M23 = StabilityUnitMath.SolverTranslationToDocument(
            solverTransform.M23, lengthToMeters);
        return documentTransform;
    }
}
