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
