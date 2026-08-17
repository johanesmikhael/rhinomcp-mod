using System;

namespace RhinoMCPModPlugin.Functions;

internal static class StabilityUnitMath
{
    public const string KilogramUnit = "kg";
    public const string PoundMassUnit = "lbm";
    public const string KilogramPerCubicMeterUnit = "kg/m³";
    public const string PoundMassPerCubicFootUnit = "lbm/ft³";
    public const double KilogramsPerPoundMass = 0.45359237;
    public const double FeetToMeters = 0.3048;
    public const double CubicFeetToCubicMeters = FeetToMeters * FeetToMeters * FeetToMeters;

    public static double ToMeters(double documentLength, double lengthToMeters)
    {
        ValidateLengthScale(lengthToMeters);
        return documentLength * lengthToMeters;
    }

    public static double FromMeters(double meters, double lengthToMeters)
    {
        ValidateLengthScale(lengthToMeters);
        return meters / lengthToMeters;
    }

    public static double SolverTranslationToDocument(double solverTranslation, double lengthToMeters)
    {
        ValidateLengthScale(lengthToMeters);
        return solverTranslation / lengthToMeters;
    }

    public static bool TryMassToKilograms(double value, string unit, out double kilograms)
    {
        kilograms = 0.0;
        if (!double.IsFinite(value) || value <= 0.0 || string.IsNullOrWhiteSpace(unit))
        {
            return false;
        }

        if (string.Equals(unit.Trim(), KilogramUnit, StringComparison.OrdinalIgnoreCase))
        {
            kilograms = value;
            return true;
        }

        if (string.Equals(unit.Trim(), PoundMassUnit, StringComparison.OrdinalIgnoreCase))
        {
            kilograms = value * KilogramsPerPoundMass;
            return double.IsFinite(kilograms) && kilograms > 0.0;
        }

        return false;
    }

    public static double KilogramsToInputMass(double kilograms, string unit)
    {
        if (string.Equals(unit, PoundMassUnit, StringComparison.OrdinalIgnoreCase))
        {
            return kilograms / KilogramsPerPoundMass;
        }

        if (string.Equals(unit, KilogramUnit, StringComparison.OrdinalIgnoreCase))
        {
            return kilograms;
        }

        throw new ArgumentException($"Unsupported mass unit '{unit}'.", nameof(unit));
    }

    public static bool TryDensityToKilogramsPerCubicMeter(
        double value,
        string unit,
        out double kilogramsPerCubicMeter)
    {
        kilogramsPerCubicMeter = 0.0;
        if (!double.IsFinite(value) || value <= 0.0 || string.IsNullOrWhiteSpace(unit))
        {
            return false;
        }

        var normalized = unit.Trim().Replace("3", "³");
        if (string.Equals(normalized, KilogramPerCubicMeterUnit, StringComparison.OrdinalIgnoreCase))
        {
            kilogramsPerCubicMeter = value;
            return true;
        }

        if (string.Equals(normalized, PoundMassPerCubicFootUnit, StringComparison.OrdinalIgnoreCase))
        {
            kilogramsPerCubicMeter = value * KilogramsPerPoundMass / CubicFeetToCubicMeters;
            return double.IsFinite(kilogramsPerCubicMeter) && kilogramsPerCubicMeter > 0.0;
        }

        return false;
    }

    public static double KilogramsPerCubicMeterToInputDensity(
        double kilogramsPerCubicMeter,
        string unit)
    {
        var normalized = unit?.Trim().Replace("3", "³");
        if (string.Equals(normalized, PoundMassPerCubicFootUnit, StringComparison.OrdinalIgnoreCase))
        {
            return kilogramsPerCubicMeter * CubicFeetToCubicMeters / KilogramsPerPoundMass;
        }

        if (string.Equals(normalized, KilogramPerCubicMeterUnit, StringComparison.OrdinalIgnoreCase))
        {
            return kilogramsPerCubicMeter;
        }

        throw new ArgumentException($"Unsupported density unit '{unit}'.", nameof(unit));
    }

    private static void ValidateLengthScale(double lengthToMeters)
    {
        if (!double.IsFinite(lengthToMeters) || lengthToMeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthToMeters));
        }
    }
}
