using System;
using RhinoMCPModPlugin.Functions;

namespace RhinoMCPModPlugin.Tests;

internal static class Program
{
    private static int _assertions;

    public static int Main()
    {
        try
        {
            EquivalentMetricAndImperialLengthsProduceTheSameSolverValue();
            PoundMassConvertsToCanonicalKilograms();
            ImperialDensityConvertsToCanonicalSiDensity();
            UnsupportedOrMalformedUnitsAreRejected();
            SolverTranslationReturnsToDocumentUnits();
            AutoFloorStrengthScalesWithAssemblyWeight();
            AutoFloorStrengthFallsBackOnDegenerateInputs();
            Console.WriteLine($"Passed {_assertions} stability unit assertions.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void EquivalentMetricAndImperialLengthsProduceTheSameSolverValue()
    {
        AssertClose(1.0, StabilityUnitMath.ToMeters(1.0, 1.0), 1e-12);
        AssertClose(1.0, StabilityUnitMath.ToMeters(1000.0, 0.001), 1e-12);
        AssertClose(1.0, StabilityUnitMath.ToMeters(1.0 / 0.0254, 0.0254), 1e-12);
        AssertClose(1.0, StabilityUnitMath.ToMeters(1.0 / 0.3048, 0.3048), 1e-12);
        AssertClose(
            StabilityUnitMath.ToMeters(StabilityUnitMath.FromMeters(0.001, 0.001), 0.001),
            StabilityUnitMath.ToMeters(StabilityUnitMath.FromMeters(0.001, 0.0254), 0.0254),
            1e-12);
    }

    private static void PoundMassConvertsToCanonicalKilograms()
    {
        Assert(StabilityUnitMath.TryMassToKilograms(10.0, "kg", out var unchangedKilograms));
        AssertClose(10.0, unchangedKilograms, 1e-12);
        Assert(StabilityUnitMath.TryMassToKilograms(10.0, "lbm", out var kilograms));
        AssertClose(4.5359237, kilograms, 1e-10);
        AssertClose(
            10.0,
            StabilityUnitMath.KilogramsToInputMass(kilograms, StabilityUnitMath.PoundMassUnit),
            1e-10);
    }

    private static void ImperialDensityConvertsToCanonicalSiDensity()
    {
        Assert(
            StabilityUnitMath.TryDensityToKilogramsPerCubicMeter(
                1000.0,
                StabilityUnitMath.KilogramPerCubicMeterUnit,
                out var unchangedDensity));
        AssertClose(1000.0, unchangedDensity, 1e-12);
        Assert(
            StabilityUnitMath.TryDensityToKilogramsPerCubicMeter(
                62.4,
                StabilityUnitMath.PoundMassPerCubicFootUnit,
                out var kilogramsPerCubicMeter));
        AssertClose(999.552114535112, kilogramsPerCubicMeter, 1e-9);
        AssertClose(
            62.4,
            StabilityUnitMath.KilogramsPerCubicMeterToInputDensity(
                kilogramsPerCubicMeter,
                StabilityUnitMath.PoundMassPerCubicFootUnit),
            1e-10);
        Assert(StabilityUnitMath.TryDensityToKilogramsPerCubicMeter(62.4, "lbm/ft3", out _));
    }

    private static void UnsupportedOrMalformedUnitsAreRejected()
    {
        Assert(!StabilityUnitMath.TryMassToKilograms(1.0, "lbf", out _));
        Assert(!StabilityUnitMath.TryDensityToKilogramsPerCubicMeter(1.0, "lb/ft³", out _));
        AssertThrows(() => StabilityUnitMath.ToMeters(1.0, 0.0));
        AssertThrows(() => StabilityUnitMath.FromMeters(1.0, double.NaN));
    }

    private static void SolverTranslationReturnsToDocumentUnits()
    {
        AssertClose(1500.0, StabilityUnitMath.SolverTranslationToDocument(1.5, 0.001), 1e-10);
        AssertClose(-250.0, StabilityUnitMath.SolverTranslationToDocument(-0.25, 0.001), 1e-10);
        AssertClose(3.0, StabilityUnitMath.SolverTranslationToDocument(0.003, 0.001), 1e-10);
    }

    private static void AutoFloorStrengthScalesWithAssemblyWeight()
    {
        const double gravity = 9.80665;
        const double fallback = 1000.0;
        // Default stability threshold 0.01 m, a tenth of which is the settling budget.
        const double targetPenetration = 0.01 / 10.0;

        // One 10 kg block and a ten-block stack, the two cases calibrated in Rhino.
        AssertClose(
            5580.0,
            StabilityUnitMath.AutoFloorStrength(10.0, gravity, targetPenetration, fallback),
            1.0);
        AssertClose(
            55800.0,
            StabilityUnitMath.AutoFloorStrength(100.0, gravity, targetPenetration, fallback),
            10.0);

        // Strength tracks weight linearly, so penetration stays put as an assembly grows.
        var single = StabilityUnitMath.AutoFloorStrength(10.0, gravity, targetPenetration, fallback);
        var quadruple = StabilityUnitMath.AutoFloorStrength(40.0, gravity, targetPenetration, fallback);
        AssertClose(4.0, quadruple / single, 1e-9);

        // Halving the tolerated penetration doubles the required stiffness.
        AssertClose(
            2.0 * single,
            StabilityUnitMath.AutoFloorStrength(10.0, gravity, targetPenetration / 2.0, fallback),
            1e-6);
    }

    private static void AutoFloorStrengthFallsBackOnDegenerateInputs()
    {
        const double fallback = 1000.0;

        // A zero stability threshold and a zero gravity are both accepted by the solver's
        // parameter reader, so neither may divide.
        AssertClose(fallback, StabilityUnitMath.AutoFloorStrength(10.0, 9.80665, 0.0, fallback), 1e-12);
        AssertClose(fallback, StabilityUnitMath.AutoFloorStrength(10.0, 0.0, 0.001, fallback), 1e-12);
        AssertClose(fallback, StabilityUnitMath.AutoFloorStrength(0.0, 9.80665, 0.001, fallback), 1e-12);
        AssertClose(
            fallback,
            StabilityUnitMath.AutoFloorStrength(double.NaN, 9.80665, 0.001, fallback),
            1e-12);
        AssertClose(
            fallback,
            StabilityUnitMath.AutoFloorStrength(double.PositiveInfinity, 9.80665, 0.001, fallback),
            1e-12);

        // A featherweight assembly must not end up with a floor softer than the old fixed one.
        AssertClose(
            fallback,
            StabilityUnitMath.AutoFloorStrength(1e-9, 9.80665, 0.001, fallback),
            1e-12);

        // A pathological mass is clamped rather than handed to Kangaroo verbatim.
        AssertClose(
            StabilityUnitMath.MaxAutoFloorStrength,
            StabilityUnitMath.AutoFloorStrength(1e30, 9.80665, 0.001, fallback),
            1e-12);
    }

    private static void Assert(bool condition)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion {_assertions} failed.");
        }
    }

    private static void AssertClose(double expected, double actual, double tolerance)
    {
        _assertions++;
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                $"Assertion {_assertions} failed: expected {expected:R}, got {actual:R}.");
        }
    }

    private static void AssertThrows(Action action)
    {
        _assertions++;
        try
        {
            action();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new InvalidOperationException($"Assertion {_assertions} failed: expected range error.");
    }
}
