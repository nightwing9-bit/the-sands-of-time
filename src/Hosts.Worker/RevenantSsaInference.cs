using Microsoft.ML;
using Microsoft.ML.Transforms.TimeSeries;
using Revenant.Intelligence;

namespace Hosts.Worker;

internal readonly record struct RevenantSpike(bool Alert, double RawScore, double PValue);

/// <summary>
/// The single ML.NET implementation of R7/2 SSA fitting and inference. The published Worker, pilot research, qualification,
/// cold reconstruction and source/child comparisons call these methods; tests orchestrate and measure but do not re-implement them.
/// Each call creates one engine from the immutable artifact bytes and consumes one subject's verified causal series in order, one
/// completed seal per <c>Predict(observation)</c>. Output at index i depends only on observations 0…i. No call ever refits.
/// </summary>
internal static class RevenantSsaInference
{
    internal static TimeSeriesPredictionEngine<SeriesPoint, T> Load<T>(MLContext ml, Stream artifact) where T : class, new()
    {
        RequireSupportedPlatform();
        return ml.Model.Load(artifact, out _).CreateTimeSeriesEngine<SeriesPoint, T>(ml);
    }

    /// <summary>Fits DetectSpikeBySsa on exactly <c>spec.TrainingWindow</c> contiguous counts starting at <paramref name="start"/>.</summary>
    internal static byte[] FitAnomaly(MLContext ml, SsaAnomalySpec spec, IReadOnlyList<long> counts, int start)
    {
        RequireSupportedPlatform();
        if (!spec.IsValid || start < 0 || start + spec.TrainingWindow > counts.Count)
            throw new InvalidDataException("Anomaly training requires one valid specification and a contiguous training slice.");
        var data = ml.Data.LoadFromEnumerable(Enumerable.Range(start, spec.TrainingWindow)
            .Select(i => new SeriesPoint { Value = SsaAdvisory.AnomalyInput(spec.Transform, counts[i]) }).ToArray());
        var estimator = ml.Transforms.DetectSpikeBySsa(nameof(SpikeOutput.Prediction), nameof(SeriesPoint.Value),
            confidence: 95d, pvalueHistoryLength: spec.PValueHistory, trainingWindowSize: spec.TrainingWindow,
            seasonalityWindowSize: spec.SeasonalWindow, side: AnomalySide.Positive);
        using var stream = new MemoryStream();
        ml.Model.Save(estimator.Fit(data), data.Schema, stream);
        return stream.ToArray();
    }

    /// <summary>Fits ForecastBySsa on <c>spec.TrainSize</c> contiguous transformed observations starting at <paramref name="start"/>.</summary>
    internal static byte[] FitForecast(MLContext ml, SsaForecastSpec spec, long opening, IReadOnlyList<long> available, int start)
    {
        RequireSupportedPlatform();
        if (!spec.IsValid || start < 0 || start + spec.TrainSize > available.Count)
            throw new InvalidDataException("Forecast training requires one valid specification and a contiguous training slice.");
        var scale = SsaAdvisory.Scale(opening, spec.ScaleDivisor);
        var data = ml.Data.LoadFromEnumerable(Enumerable.Range(start, spec.TrainSize).Select(i => new SeriesPoint
        {
            Value = SsaAdvisory.ForecastInput(spec.Representation, opening, scale, i == 0 ? opening : available[i - 1], available[i]),
        }).ToArray());
        var estimator = ml.Forecasting.ForecastBySsa(nameof(ForecastOutput.Forecast), nameof(SeriesPoint.Value),
            windowSize: spec.Window, seriesLength: spec.SeriesLength, trainSize: spec.TrainSize, horizon: SsaAdvisory.Horizon,
            isAdaptive: false, confidenceLevel: .95f,
            rankSelectionMethod: spec.Rank.HasValue ? RankSelectionMethod.Fixed : RankSelectionMethod.Exact, rank: spec.Rank,
            confidenceLowerBoundColumn: nameof(ForecastOutput.LowerBound), confidenceUpperBoundColumn: nameof(ForecastOutput.UpperBound));
        using var stream = new MemoryStream();
        ml.Model.Save(estimator.Fit(data), data.Schema, stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Scores one account through its whole causal prefix. Steps are returned for buckets at or after the warm-up. An ineligible
    /// account returns no steps (the Worker reports InsufficientHistory). A nonfinite native residual is an invalid artifact or
    /// runtime, never a silent zero. The native p-value is kept as a diagnostic and may be unavailable.
    /// </summary>
    internal static SsaAnomalyStep[] ScoreAccount(MLContext ml, ReadOnlySpan<byte> artifact, SsaAnomalySpec spec, SsaAlertPolicy policy,
        IReadOnlyList<long> counts)
    {
        if (!spec.IsValid || !policy.IsValid) throw new InvalidDataException("Invalid anomaly specification.");
        if (!SsaAdvisory.EligibleAccount(spec, counts)) return [];
        using var stream = new MemoryStream(artifact.ToArray(), writable: false);
        using var engine = Load<SpikeOutput>(ml, stream);
        var steps = new SsaAnomalyStep[counts.Count - spec.Warmup];
        var alert = new SsaAlertState(policy);
        double expectedInput = 0;
        for (var i = 0; i < counts.Count; i++)
        {
            var input = SsaAdvisory.AnomalyInput(spec.Transform, counts[i]);
            var raw = engine.Predict(new SeriesPoint { Value = input }).Prediction;
            if (raw.Length != 3 || raw[0] is not (0d or 1d) || !double.IsFinite(raw[1]))
                throw new InvalidDataException(FormattableString.Invariant(
                    $"Invalid ML.NET anomaly output at bucket {i}: [{string.Join(", ", raw)}]."));
            if (i >= spec.Warmup)
            {
                var residual = input - expectedInput;
                var expected = SsaAdvisory.ExpectedCount(spec.Transform, expectedInput);
                var statistic = alert.Next(counts[i], expected, residual);
                steps[i - spec.Warmup] = new(i, counts[i], residual, expected, double.IsFinite(raw[2]) ? raw[2] : null, raw[0] == 1,
                    statistic, counts[i] > 0 && statistic >= policy.Threshold);
            }
            // The prediction engine consumes bucket i before its getter runs. Its signed raw score therefore
            // subtracts the prediction from that updated state, which is the expectation for bucket i+1.
            expectedInput = input - raw[1];
        }
        return steps;
    }

    /// <summary>
    /// Forecasts one bank through its whole causal prefix with one stateful engine: the observation at seal i is consumed and the
    /// eight-step forecast from that origin is decoded. No re-centering, no replay and no future observation.
    /// </summary>
    internal static SsaForecastStep[] ForecastBank(MLContext ml, ReadOnlySpan<byte> artifact, SsaForecastSpec spec,
        SsaIntervalPolicy? interval, long opening, IReadOnlyList<long> available)
    {
        if (!spec.IsValid || interval is { IsValid: false }) throw new InvalidDataException("Invalid forecast specification.");
        if (available.Count <= spec.Warmup) return [];
        var scale = SsaAdvisory.Scale(opening, spec.ScaleDivisor);
        using var stream = new MemoryStream(artifact.ToArray(), writable: false);
        using var engine = Load<ForecastOutput>(ml, stream);
        var steps = new SsaForecastStep[available.Count - spec.Warmup];
        for (var i = 0; i < available.Count; i++)
        {
            var input = SsaAdvisory.ForecastInput(spec.Representation, opening, scale, i == 0 ? opening : available[i - 1], available[i]);
            var result = engine.Predict(new SeriesPoint { Value = input });
            if (i < spec.Warmup) continue;
            var (central, lower, upper) = SsaAdvisory.DecodeLevels(spec.Representation, opening, scale, available[i],
                result.Forecast, result.LowerBound, result.UpperBound);
            var sigma = SsaAdvisory.Sigma(available, opening, scale, i, interval?.SigmaWindow ?? 12);
            var calibratedLower = new long[SsaAdvisory.Horizon];
            var calibratedUpper = new long[SsaAdvisory.Horizon];
            for (var h = 0; h < SsaAdvisory.Horizon; h++)
                (calibratedLower[h], calibratedUpper[h]) = interval is null
                    ? (lower[h], upper[h]) : SsaAdvisory.Band(central[h], sigma, interval.Multipliers[h]);
            steps[i - spec.Warmup] = new(i, available[i], sigma, central, lower, upper, calibratedLower, calibratedUpper);
        }
        return steps;
    }

    // Historical R7/1 entry points retained for the frozen transfer fixture and the archived pilot reproduction.
    internal static RevenantSpike ObserveAccount(TimeSeriesPredictionEngine<SeriesPoint, SpikeOutput> engine, long count,
        bool allowUnavailable = false)
    {
        if (count is < 0 or > 16_777_216)
            throw new InvalidDataException("Account count cannot be represented exactly as an ML.NET Single.");
        var raw = engine.Predict(new SeriesPoint { Value = count }).Prediction;
        if (raw.Length != 3 || raw[0] is not (0d or 1d) || raw[2] is < 0 or > 1
            || (!allowUnavailable && raw.Any(x => !double.IsFinite(x))))
            throw new InvalidDataException(FormattableString.Invariant(
                $"Invalid ML.NET anomaly output for count {count}: [{string.Join(", ", raw)}]."));
        return new(raw[0] == 1, raw[1], raw[2]);
    }

    internal static ForecastOutput ObserveBank(TimeSeriesPredictionEngine<SeriesPoint, ForecastOutput> engine, float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("Invalid normalized bank input.");
        return engine.Predict(new SeriesPoint { Value = value });
    }

    internal static void VerifyForecast(ForecastOutput forecast, int horizons)
    {
        if (forecast.Forecast.Length != horizons || forecast.LowerBound.Length != horizons || forecast.UpperBound.Length != horizons)
            throw new InvalidDataException("Invalid ML.NET forecast dimensions.");
        for (var h = 0; h < horizons; h++)
        {
            var (point, lower, upper) = (forecast.Forecast[h], forecast.LowerBound[h], forecast.UpperBound[h]);
            if (!float.IsFinite(point) || !float.IsFinite(lower) || !float.IsFinite(upper) || lower > point || upper < point)
                throw new InvalidDataException("Invalid ML.NET forecast interval.");
        }
    }

    private static void RequireSupportedPlatform()
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("Revenant SSA inference requires Linux AMD64.");
    }
}
