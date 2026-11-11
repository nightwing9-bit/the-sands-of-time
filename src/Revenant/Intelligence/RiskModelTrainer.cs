using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;

namespace Revenant.Intelligence;

public sealed record RiskTrainingResult(RiskModelManifest Manifest, string ManifestPath, string ArtifactPath, RiskModelScorer Scorer);

/// <summary>One processed ETL example. The label is evaluation truth; the raw snapshot is what the feature builder consumes.</summary>
public sealed record RiskExample(RiskSplit Split, long Ordinal, int Step, bool Positive, RiskFeatureRaw Raw);

/// <summary>
/// OFFLINE trainer for the risk classifier (Spec 08 task 8.2). Not a service, not called from a request, and not part of any
/// normal CI test path that trains from scratch more than the tiny fixture. Order of use of labels is fixed by construction:
/// <list type="number">
/// <item>the ETL splits the source (chronological windows, purged groups) and emits split examples;</item>
/// <item>FastTree is fitted on the TRAIN examples only (the only preprocessing, the four-feature builder, has no fitted state);</item>
/// <item>the artifact is saved, then LOADED through <see cref="RiskModelScorer"/>, and all further scores come from the loaded artifact;</item>
/// <item><c>thresholdPpm</c> is selected on VALIDATION scores and labels only;</item>
/// <item>the TEST split is scored and evaluated exactly once, at the already-pinned threshold; nothing is changed afterwards.</item>
/// </list>
/// FastTree's built-in Platt calibrator is fitted by ML.NET on the training data during <c>Fit</c>; no other data reaches it.
/// </summary>
public static class RiskModelTrainer
{
    public const string CohortFixture = "project synthetic fixture";

    /// <summary>Generates the seeded fixture and runs the full offline pipeline into <paramref name="outputDirectory"/>.</summary>
    public static RiskTrainingResult TrainFixture(string outputDirectory, DateTimeOffset trainedAt, long fixtureSeed = RiskFixture.DefaultSeed, RiskTrainerOptions? options = null)
    {
        var csv = RiskFixture.GenerateCsv(fixtureSeed);
        return Train(() => new MemoryStream(csv, writable: false), RiskFixture.Description, outputDirectory, trainedAt, options ?? new RiskTrainerOptions());
    }

    public static RiskTrainingResult Train(
        Func<Stream> openSource, RiskSourceDescription source, string outputDirectory, DateTimeOffset trainedAt, RiskTrainerOptions options)
    {
        ArgumentNullException.ThrowIfNull(openSource);
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(outputDirectory);

        var clock = Stopwatch.StartNew();
        using var examplesText = new StringWriter(CultureInfo.InvariantCulture);
        var etl = RiskDatasetEtl.Run(openSource, source, examplesText);
        var examples = ParseExamples(examplesText.ToString(), etl.Manifest.SplitExamplesSha256);
        var etlSeconds = clock.Elapsed.TotalSeconds;

        var train = examples.Where(e => e.Split == RiskSplit.Train).ToArray();
        var validation = examples.Where(e => e.Split == RiskSplit.Validation).ToArray();
        var test = examples.Where(e => e.Split == RiskSplit.Test).ToArray();

        // 1. Fit on TRAIN only.
        clock.Restart();
        var ml = new MLContext(seed: options.Seed);
        var trainView = ml.Data.LoadFromEnumerable(train.Select(ToInput).ToList());
        var trainer = ml.BinaryClassification.Trainers.FastTree(new FastTreeBinaryTrainer.Options
        {
            LabelColumnName = "Label",
            FeatureColumnName = "Features",
            Seed = options.Seed,
            NumberOfThreads = options.NumberOfThreads,
            NumberOfLeaves = options.NumberOfLeaves,
            NumberOfTrees = options.NumberOfTrees,
            MinimumExampleCountPerLeaf = options.MinimumExampleCountPerLeaf,
            LearningRate = options.LearningRate,
        });
        var fitted = trainer.Fit(trainView);
        var trainSeconds = clock.Elapsed.TotalSeconds;

        // 2. Save, then LOAD through the production path; all later scores come from the loaded artifact.
        clock.Restart();
        var artifactPath = Path.Combine(outputDirectory, RiskModelScorer.ArtifactFileName);
        using (var file = File.Create(artifactPath))
        {
            ml.Model.Save(fitted, trainView.Schema, file);
        }
        var artifactBytes = File.ReadAllBytes(artifactPath);
        var artifactSha = RiskModelManifestCodec.Sha256Hex(artifactBytes);

        // The threshold is unknown until validation is scored, so the manifest cannot be sealed yet. Score through the same verified-bytes
        // loader the production scorer uses (RiskModelCore); the sealed manifest and RiskModelScorer.Load follow at the end.
        var provisional = RiskModelCore.TryCreate(artifactBytes, options.Seed, out var coreFailure)
            ?? throw new InvalidOperationException($"The freshly saved artifact does not load: {coreFailure?.Code}: {coreFailure?.Detail}");
        var saveLoadSeconds = clock.Elapsed.TotalSeconds;

        // Pre/post-process reproducibility: the in-memory fitted model and the model re-loaded from the saved bytes must agree on every row.
        var allVectors = examples.Select(e => RiskFeaturesV1.Vector(e.Raw)).ToArray();
        var fittedProbabilities = fitted.Transform(ml.Data.LoadFromEnumerable(allVectors.Select(v => new RiskModelInput { Features = v.ToArray() }).ToList()))
            .GetColumn<float>(RiskModelScorer.ProbabilityColumn).ToArray();
        var loadedProbabilities = provisional.Probabilities(allVectors);
        var equal = fittedProbabilities.SequenceEqual(loadedProbabilities);
        if (!equal)
        {
            throw new InvalidOperationException("The reloaded artifact does not reproduce the fitted model's probabilities.");
        }

        // 3. Threshold from VALIDATION only.
        clock.Restart();
        var validationScored = Score(provisional, validation);
        var threshold = RiskEvaluation.SelectThreshold(validationScored);
        if (threshold.IsFailure)
        {
            throw new RiskEtlException($"No valid threshold could be selected on validation: {threshold.Error}.");
        }
        var thresholdPpm = threshold.Value.ThresholdPpm;

        // 4. TEST: scored and evaluated once, at the pinned threshold.
        var trainScored = Score(provisional, train);
        var testScored = Score(provisional, test);
        var report = new RiskEvaluationReport(
            CohortFixture,
            RiskEvaluation.Compute("train (in-sample, not a quality claim)", trainScored, thresholdPpm),
            RiskEvaluation.Compute("validation (threshold selected here)", validationScored, thresholdPpm),
            RiskEvaluation.Compute("held-out test (evaluated once)", testScored, thresholdPpm),
            Baselines(test),
            "Baselines are parameter-free rules computed on the same held-out test rows. The rules-only v7 screening policy is NOT evaluated: its inputs "
            + "(new-beneficiary flag, sanctions flag, 15-minute velocity) do not exist in the PaySim-shaped fixture, so no aligned v7 baseline is available.",
            [
                "Project synthetic fixture only: not PaySim, not independent validation, not a Friday Ring evaluation, not a real-world fraud rate or bank accuracy.",
                "Labels come from the generator's scenario types. By generator design fraud-like transfers draw a larger fraction of the source balance AND come from accounts "
                + "with larger balances, so separation on transferFraction and logSourcePostedBefore is partly a generator artifact, not a property of real payments.",
                "Boosted trees do not extrapolate sensibly: inputs outside the fixture's balance and amount range can score high or low for no business reason, and an exact "
                + "balance drain is not guaranteed to hold (see ReferencePredictions). The model must not be read as a rule.",
                "Single seed, single split, no confidence intervals; the held-out cohort is small (see row and positive counts), so small metric differences are noise.",
                "USD transfers only; four features; no velocity, beneficiary or time-of-day signal.",
            ]);

        var timer = Stopwatch.StartNew();
        const int timingRepeats = 20;
        var timingVectors = allVectors.Take(200).ToArray();
        for (var i = 0; i < timingRepeats; i++)
        {
            provisional.Probabilities(timingVectors);
        }
        var microsPerExample = timer.Elapsed.TotalMilliseconds * 1000d / (timingRepeats * Math.Max(1, timingVectors.Length));
        var evalSeconds = clock.Elapsed.TotalSeconds;

        var references = ReferenceInputs.Select(r => Reference(provisional, r.Amount, r.Posted, thresholdPpm)).ToArray();

        var manifest = RiskModelManifestCodec.Seal(new RiskModelManifest(
            RiskModelManifest.Schema,
            RiskModelManifest.ModelIdFixture,
            "ML.NET FastTree binary classifier; calibrated Probability converted once to integer PPM (probability-ppm/1)",
            CohortFixture,
            RiskModelScorer.ArtifactFileName,
            artifactSha,
            artifactBytes.LongLength,
            RiskFeaturesV1.SchemaId,
            [.. RiskFeaturesV1.FeatureNames],
            RiskScoreNormalization.NormalizationId,
            etl.Manifest,
            options,
            new RiskModelEnvironment(
                MlNetVersions.MlNet, MlNetVersions.FastTree, RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.RuntimeIdentifier),
            threshold.Value,
            thresholdPpm,
            report,
            allVectors.Length,
            equal,
            references,
            new RiskModelTimings(Round(etlSeconds), Round(trainSeconds), Round(saveLoadSeconds), Round(evalSeconds), Math.Round(microsPerExample, 1)),
            trainedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            [
                "Immutable: a retrain produces a new artifact, manifest and model id; this file is never edited. Byte-identical retraining on a different platform is not promised, only that this loaded artifact scores consistently.",
                "Offline only. Nothing here changes a ledger decision; the policy that would consume the threshold is a later task.",
                "The probability is a model-specific calibrated score, not a real-world fraud probability.",
            ],
            string.Empty));

        var manifestPath = Path.Combine(outputDirectory, RiskModelScorer.ManifestFileName);
        File.WriteAllText(manifestPath, RiskModelManifestCodec.ToJson(manifest) + "\n");
        File.WriteAllText(Path.Combine(outputDirectory, "model.zip.sha256"), artifactSha + "  " + RiskModelScorer.ArtifactFileName + "\n");

        var loaded = RiskModelScorer.Load(manifestPath, artifactPath);
        return loaded.IsSuccess
            ? new RiskTrainingResult(manifest, manifestPath, artifactPath, loaded.Value)
            : throw new InvalidOperationException($"The freshly written artifact does not load: {loaded.Error.Code}: {loaded.Error.Detail}");
    }

    /// <summary>Parses the ETL's split file (header + tab-separated lines) after checking it is the file the manifest hashed.</summary>
    public static IReadOnlyList<RiskExample> ParseExamples(string text, string expectedSha256)
    {
        if (!string.Equals(RiskModelManifestCodec.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(text)), expectedSha256, StringComparison.Ordinal))
        {
            throw new RiskEtlException("The split examples do not match the dataset manifest hash.");
        }
        var result = new List<RiskExample>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length != 7 || !Enum.TryParse<RiskSplit>(f[0], out var split)
                || !long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
                || !int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var step)
                || !long.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
                || !long.TryParse(f[6], NumberStyles.None, CultureInfo.InvariantCulture, out var posted)
                || f[3] is not ("0" or "1"))
            {
                throw new RiskEtlException($"Malformed split example line: '{line}'.");
            }
            var built = RiskFeaturesV1.Build(f[4], amount, posted);
            if (built.IsFailure)
            {
                throw new RiskEtlException($"Split example {ordinal} no longer builds: {built.Error}.");
            }
            result.Add(new RiskExample(split, ordinal, step, f[3] == "1", built.Value.Raw));
        }
        return result;
    }

    /// <summary>Fixed inputs whose loaded-artifact scores are recorded in the manifest as deterministic reference predictions.</summary>
    public static readonly IReadOnlyList<(long Amount, long Posted)> ReferenceInputs =
    [
        (150_000, 300_000),   // the 8.1 feature-hash pin: half the balance
        (10_000, 5_000_000),  // small payment from a large balance
        (500_000, 500_000),   // exact drain
        (50_000, 0),          // empty source account
        (9_900_000, 10_000_000), // 99 percent of the balance
        (100, 100_000_000),   // one major unit from a very large balance
    ];

    private static RiskReferencePrediction Reference(RiskModelCore scorer, long amount, long posted, int thresholdPpm)
    {
        var snapshot = RiskFeaturesV1.Build(RiskFeaturesV1.RequiredCurrency, amount, posted).Value;
        var score = scorer.Score(snapshot.Vector).Value;
        return new RiskReferencePrediction(snapshot.Raw.Currency, amount, posted, snapshot.FeatureHash, score, RiskScoreNormalization.AtOrAboveThreshold(score, thresholdPpm));
    }

    private static RiskMetrics[] Baselines(IReadOnlyList<RiskExample> test) =>
    [
        RiskEvaluation.ComputeBinary("baseline: never hold (test)", test.Select(e => (false, e.Positive)).ToArray()),
        RiskEvaluation.ComputeBinary(
            "baseline: hold iff wouldDrainPosted == 1 (test)",
            test.Select(e => (RiskFeaturesV1.Vector(e.Raw).WouldDrainPosted == 1f, e.Positive)).ToArray()),
    ];

    private static ScoredExample[] Score(RiskModelCore scorer, IReadOnlyList<RiskExample> examples)
    {
        var ppm = scorer.ScoreBatch(examples.Select(e => RiskFeaturesV1.Vector(e.Raw)).ToArray());
        return ppm.IsSuccess
            ? examples.Select((e, i) => new ScoredExample(ppm.Value[i], e.Positive)).ToArray()
            : throw new InvalidOperationException($"Scoring failed: {ppm.Error.Code}: {ppm.Error.Detail}");
    }

    private static RiskModelInput ToInput(RiskExample e) => new() { Label = e.Positive, Features = RiskFeaturesV1.Vector(e.Raw).ToArray() };

    private static double Round(double seconds) => Math.Round(seconds, 3);
}
