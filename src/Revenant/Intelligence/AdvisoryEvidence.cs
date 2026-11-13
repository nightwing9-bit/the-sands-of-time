using Marrow.Kernel.Canonical;
using Revenant.Observations;
using Revenant.Timeline;

namespace Revenant.Intelligence;

internal enum AdvisoryStatus : byte { Available = 1, InsufficientHistory = 2, ArtifactMissing = 3, ArtifactInvalid = 4, RuntimeUnavailable = 5 }
internal sealed record AdvisoryForecastStep(long BoundaryMs, long CentralMinor, long NativeLowerMinor, long NativeUpperMinor,
    long CalibratedLowerMinor, long CalibratedUpperMinor);
internal sealed record AdvisoryAnomaly(long Count, int HistoricalBuckets, double HistoricalMean, bool NativeAlert,
    double RawScore, double PValue, bool Alert, double Threshold);
internal sealed record AdvisoryForecast(long ReferenceMinor, long ScaleMinor, long FloorMinor, int? FirstCrossingHorizon,
    AdvisoryForecastStep[] Horizons);

internal sealed record AdvisoryEvidence(V4AdvisorySeries Series, Revenant.Contracts.ScenarioRunId Lineage, ObservedModelPin Model, string Policy,
    AdvisoryStatus Status, AdvisoryAnomaly? Anomaly = null, AdvisoryForecast? Forecast = null)
{
    internal string Schema => Series.Schema == V4AdvisorySeries.AccountSchema ? "anomaly-evidence/1" : "liquidity-forecast-evidence/1";

    internal byte[] Encode()
    {
        var seal = Series.Origin ?? throw new InvalidOperationException("Advisory evidence requires a completed origin.");
        var writer = ObservationEncoding.Begin(Schema);
        seal.Write(writer);
        writer.WriteGuid(Lineage.Value).WriteString(Series.SubjectId).WriteString(Series.Unit).WriteString(Series.Schema)
            .WriteBytes(Convert.FromHexString(Series.Digest)).WriteString(Model.ModelId)
            .WriteBytes(Convert.FromHexString(Model.ArtifactSha256)).WriteString(Policy).WriteByte((byte)Status);
        if (Status != AdvisoryStatus.Available)
        {
            if (Anomaly is not null || Forecast is not null) throw new InvalidOperationException("Unavailable evidence has no forecast or score.");
        }
        else if (Anomaly is { } a && Forecast is null && Series.Schema == V4AdvisorySeries.AccountSchema)
        {
            writer.WriteInt64(a.Count).WriteInt32(a.HistoricalBuckets).WriteInt64(BitConverter.DoubleToInt64Bits(a.HistoricalMean))
                .WriteBoolean(a.NativeAlert).WriteInt64(BitConverter.DoubleToInt64Bits(a.RawScore))
                .WriteInt64(BitConverter.DoubleToInt64Bits(a.PValue)).WriteBoolean(a.Alert)
                .WriteInt64(BitConverter.DoubleToInt64Bits(a.Threshold));
        }
        else if (Forecast is { } f && Anomaly is null && Series.Schema == V4AdvisorySeries.BankSchema)
        {
            writer.WriteInt64(f.ReferenceMinor).WriteInt64(f.ScaleMinor).WriteInt64(f.FloorMinor);
            ObservationEncoding.WriteOptional(writer, f.FirstCrossingHorizon);
            writer.WriteCount(f.Horizons.Length);
            foreach (var h in f.Horizons)
                writer.WriteInt64(h.BoundaryMs).WriteInt64(h.CentralMinor).WriteInt64(h.NativeLowerMinor)
                    .WriteInt64(h.NativeUpperMinor).WriteInt64(h.CalibratedLowerMinor).WriteInt64(h.CalibratedUpperMinor);
        }
        else throw new InvalidOperationException("Available evidence requires one matching output.");
        return writer.ToArray();
    }
}
