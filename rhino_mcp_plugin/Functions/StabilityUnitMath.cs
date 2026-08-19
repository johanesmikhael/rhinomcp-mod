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

    // Kangaroo's Floor2 goal is a linear contact spring, so an assembly sinks into the
    // floor in proportion to its own weight: penetration = C * weight / floor_strength.
    //
    // C was calibrated in Rhino against 1x1x1 m blocks resting on a flat floor at
    // floor_strength 1000, sweeping mass 10 -> 40 -> 160 kg. Penetration came out
    // 0.005577 -> 0.022315 -> 0.089385 m (exactly 4x per 4x mass), giving
    // k*penetration/weight = 0.05687, 0.05688, 0.05696.
    //
    // The fit assumes unit cubes on a flat floor. Contact-area effects account for roughly
    // 12% (it predicts 0.0558 m against 0.0493 m measured for a ten-block stack), and
    // non-planar bases and mixed block sizes are untested, so an explicit floor_strength
    // always overrides the value derived from this constant.
    public const double FloorPenetrationCoefficient = 0.0569;

    // Guard against handing Kangaroo an absurd stiffness if an assembly carries a
    // pathological mass or a caller drives the stability threshold to near zero.
    public const double MaxAutoFloorStrength = 1e9;

    /// <summary>
    /// Derives a floor-collision strength that keeps an assembly's floor penetration at
    /// roughly <paramref name="targetPenetrationMeters"/>, so that a sound structure's
    /// settling does not consume the stability threshold on its own.
    /// </summary>
    /// <remarks>
    /// Returns <paramref name="fallback"/> whenever the inputs cannot produce a meaningful
    /// stiffness. A zero gravity or a zero stability threshold are both accepted by the
    /// solver's parameter reader, so neither can be assumed positive here.
    /// </remarks>
    public static double AutoFloorStrength(
        double totalMassKilograms,
        double gravity,
        double targetPenetrationMeters,
        double fallback)
    {
        if (!double.IsFinite(totalMassKilograms) || totalMassKilograms <= 0.0 ||
            !double.IsFinite(gravity) || gravity <= 0.0 ||
            !double.IsFinite(targetPenetrationMeters) || targetPenetrationMeters <= 0.0)
        {
            return fallback;
        }

        var strength =
            FloorPenetrationCoefficient * totalMassKilograms * gravity / targetPenetrationMeters;

        if (!double.IsFinite(strength) || strength <= 0.0)
        {
            return fallback;
        }

        // Never weaken the floor below the fixed default; a very light assembly should not
        // end up with a floor softer than the one it would have had before auto-scaling.
        return Math.Min(Math.Max(strength, fallback), MaxAutoFloorStrength);
    }

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
