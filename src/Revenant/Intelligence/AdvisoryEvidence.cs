using Marrow.Kernel.Canonical;
using Revenant.Observations;
using Revenant.Timeline;

namespace Revenant.Intelligence;

internal enum AdvisoryStatus : byte { Available = 1, InsufficientHistory = 2, ArtifactMissing = 3, ArtifactInvalid = 4, RuntimeUnavailable = 5 }

/// <summary>
/// One forecast horizon in USD minor units. Under the completed-interval-change representation the native bounds are the
/// cumulative sums of ML.NET's per-change bounds: a labelled diagnostic, not a level interval. The calibrated bounds are the
/// split-conformal level band frozen before FinalTest.
/// </summary>
internal sealed record AdvisoryForecastStep(long BoundaryMs, long CentralMinor, long NativeLowerMinor, long NativeUpperMinor,
    long CalibratedLowerMinor, long CalibratedUpperMinor);

/// <summary>R7/1 anomaly output (schema anomaly-evidence/1). Retained so stored schema-1 bytes stay decodable by definition.</summary>
internal sealed record AdvisoryAnomaly(long Count, int HistoricalBuckets, double HistoricalMean, bool NativeAlert,
    double RawScore, double PValue, bool Alert, double Threshold);

/// <summary>R7/1 forecast output (schema liquidity-forecast-evidence/1).</summary>
internal sealed record AdvisoryForecast(long ReferenceMinor, long ScaleMinor, long FloorMinor, int? FirstCrossingHorizon,
    AdvisoryForecastStep[] Horizons);

/// <summary>
/// R7/2 anomaly output (anomaly-evidence/2): observed count versus the fitted SSA's expected count, the causal residual, the
/// calibrated alert statistic and threshold, and the native p-value only as a diagnostic (null when ML.NET returns a nonfinite one).
/// </summary>
internal sealed record AdvisoryAnomalyV2(long Count, double ExpectedCount, double Residual, SsaAlertStatistic Statistic,
    double StatisticValue, double Threshold, bool Alert, bool NativeAlert, double? NativePValue, int SeasonalWindow, int Window);

/// <summary>R7/2 forecast output (liquidity-forecast-evidence/2).</summary>
internal sealed record AdvisoryForecastV2(SsaForecastRepresentation Representation, long ReferenceMinor, long ScaleMinor,
    long SigmaMinor, long FloorMinor, int? FirstCrossingHorizon, AdvisoryForecastStep[] Horizons);

internal sealed record AdvisoryEvidence(V4AdvisorySeries Series, Revenant.Contracts.ScenarioRunId Lineage, ObservedModelPin Model, string Policy,
    AdvisoryStatus Status, AdvisoryAnomaly? Anomaly = null, AdvisoryForecast? Forecast = null, AdvisoryAnomalyV2? AnomalyV2 = null,
    AdvisoryForecastV2? ForecastV2 = null)
{
    private bool IsV2 => Policy == SsaAdvisory.Policy;

    internal string Schema => (Series.Schema == V4AdvisorySeries.AccountSchema, IsV2) switch
    {
        (true, false) => "anomaly-evidence/1",
        (false, false) => "liquidity-forecast-evidence/1",
        (true, true) => "anomaly-evidence/2",
        (false, true) => "liquidity-forecast-evidence/2",
    };

    internal byte[] Encode()
    {
        var seal = Series.Origin ?? throw new InvalidOperationException("Advisory evidence requires a completed origin.");
        var writer = ObservationEncoding.Begin(Schema);
        seal.Write(writer);
        writer.WriteGuid(Lineage.Value).WriteString(Series.SubjectId).WriteString(Series.Unit).WriteString(Series.Schema)
            .WriteBytes(Convert.FromHexString(Series.Digest)).WriteString(Model.ModelId)
            .WriteBytes(Convert.FromHexString(Model.ArtifactSha256)).WriteString(Policy).WriteByte((byte)Status);
        var outputs = (Anomaly is null ? 0 : 1) + (Forecast is null ? 0 : 1) + (AnomalyV2 is null ? 0 : 1) + (ForecastV2 is null ? 0 : 1);
        var account = Series.Schema == V4AdvisorySeries.AccountSchema;
        if (Status != AdvisoryStatus.Available)
        {
            if (outputs != 0) throw new InvalidOperationException("Unavailable evidence has no forecast or score.");
        }
        else if (outputs != 1) throw new InvalidOperationException("Available evidence requires one matching output.");
        else if (!IsV2 && Anomaly is { } a && account)
        {
            writer.WriteInt64(a.Count).WriteInt32(a.HistoricalBuckets).WriteInt64(Bits(a.HistoricalMean))
                .WriteBoolean(a.NativeAlert).WriteInt64(Bits(a.RawScore)).WriteInt64(Bits(a.PValue)).WriteBoolean(a.Alert)
                .WriteInt64(Bits(a.Threshold));
        }
        else if (!IsV2 && Forecast is { } f && !account)
        {
            writer.WriteInt64(f.ReferenceMinor).WriteInt64(f.ScaleMinor).WriteInt64(f.FloorMinor);
            ObservationEncoding.WriteOptional(writer, f.FirstCrossingHorizon);
            WriteSteps(writer, f.Horizons);
        }
        else if (IsV2 && AnomalyV2 is { } v && account)
        {
            if (!double.IsFinite(v.ExpectedCount) || !double.IsFinite(v.Residual) || !double.IsFinite(v.StatisticValue)
                || !double.IsFinite(v.Threshold) || v.NativePValue is { } p && !double.IsFinite(p))
                throw new InvalidOperationException("Anomaly evidence values must be finite.");
            writer.WriteInt64(v.Count).WriteInt64(Bits(v.ExpectedCount)).WriteInt64(Bits(v.Residual)).WriteByte((byte)v.Statistic)
                .WriteInt64(Bits(v.StatisticValue)).WriteInt64(Bits(v.Threshold)).WriteBoolean(v.Alert).WriteBoolean(v.NativeAlert)
                .WriteBoolean(v.NativePValue.HasValue).WriteInt64(Bits(v.NativePValue ?? 0)).WriteInt32(v.SeasonalWindow).WriteInt32(v.Window);
        }
        else if (IsV2 && ForecastV2 is { } g && !account)
        {
            writer.WriteByte((byte)g.Representation).WriteInt64(g.ReferenceMinor).WriteInt64(g.ScaleMinor).WriteInt64(g.SigmaMinor)
                .WriteInt64(g.FloorMinor);
            ObservationEncoding.WriteOptional(writer, g.FirstCrossingHorizon);
            WriteSteps(writer, g.Horizons);
        }
        else throw new InvalidOperationException("Available evidence requires one matching output.");
        return writer.ToArray();
    }

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    private static void WriteSteps(CanonicalWriter writer, AdvisoryForecastStep[] steps)
    {
        writer.WriteCount(steps.Length);
        foreach (var h in steps)
            writer.WriteInt64(h.BoundaryMs).WriteInt64(h.CentralMinor).WriteInt64(h.NativeLowerMinor)
                .WriteInt64(h.NativeUpperMinor).WriteInt64(h.CalibratedLowerMinor).WriteInt64(h.CalibratedUpperMinor);
    }
}
