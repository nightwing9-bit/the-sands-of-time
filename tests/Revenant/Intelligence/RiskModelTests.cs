using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;
using Revenant.Contracts;
using Revenant.Intelligence;
using Xunit;

namespace Revenant.Tests.Intelligence;

public sealed class ThreeFeatureRow
{
    public bool Label { get; set; }

    [VectorType(3)]
    public float[] Features { get; set; } = [];
}

/// <summary>
/// Spec 08 task 8.2 against the COMMITTED artifact (copied next to the test assembly) and short real trainings on the seeded
/// fixture. Every number asserted here is recomputed from the artifact or the regenerated fixture, never copied from the manifest
/// as an oracle (the manifest is compared with the recomputation).
/// </summary>
public sealed class RiskModelTests
{
    private static readonly string ArtifactDirectory = Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts", "risk-fixture-fasttree-v1");

    private static readonly Lazy<IReadOnlyList<RiskExample>> FixtureExamples = new(() =>
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        var etl = RiskDatasetEtl.Run(() => new MemoryStream(RiskFixture.GenerateCsv()), RiskFixture.Description, text);
        return RiskModelTrainer.ParseExamples(text.ToString(), etl.Manifest.SplitExamplesSha256);
    });

    private static RiskModelScorer Load() => RiskModelScorer.LoadDirectory(ArtifactDirectory) is { IsSuccess: true } r ? r.Value : throw new Xunit.Sdk.XunitException("artifact does not load");

    private static DirectoryInfo CopyArtifact()
    {
        var dir = Directory.CreateTempSubdirectory("risk-model-");
        foreach (var file in Directory.GetFiles(ArtifactDirectory))
        {
            File.Copy(file, Path.Combine(dir.FullName, Path.GetFileName(file)));
        }
        return dir;
    }

    private static RiskModelManifest ReadManifest(string dir) =>
        JsonSerializer.Deserialize<RiskModelManifest>(File.ReadAllText(Path.Combine(dir, RiskModelScorer.ManifestFileName)))!;

    private static void WriteManifest(string dir, RiskModelManifest manifest) =>
        File.WriteAllText(Path.Combine(dir, RiskModelScorer.ManifestFileName), RiskModelManifestCodec.ToJson(manifest));

    private static RiskModelFailure LoadFailure(string dir) => RiskModelScorer.LoadDirectory(dir).Error;

    // ---- load, identity, manifest ----

    [Fact]
    public void Committed_artifact_loads_and_matches_its_checksum_manifest_and_sidecar()
    {
        var scorer = Load();
        var manifest = scorer.Manifest;
        var zip = File.ReadAllBytes(Path.Combine(ArtifactDirectory, RiskModelScorer.ArtifactFileName));

        Assert.Equal(RiskModelManifestCodec.Sha256Hex(zip), manifest.ArtifactSha256);
        Assert.Equal(zip.LongLength, manifest.ArtifactBytes);
        Assert.StartsWith(manifest.ArtifactSha256, File.ReadAllText(Path.Combine(ArtifactDirectory, "model.zip.sha256")), StringComparison.Ordinal);
        Assert.True(RiskModelManifestCodec.HashMatches(manifest));
        Assert.Equal(new RiskModelIdentity(
            "revenant-risk-fasttree-fixture/1", manifest.ArtifactSha256, manifest.ManifestSha256, "5.0.1", "risk-features-v1", manifest.ThresholdPpm), scorer.Identity);
        Assert.InRange(scorer.ThresholdPpm, 1, RiskScoreNormalization.PpmScale);
        Assert.InRange(manifest.ArtifactBytes, 1, 200_000);
    }

    [Fact]
    public void Manifest_records_provenance_options_split_and_honest_labels()
    {
        var m = Load().Manifest;
        var csv = RiskFixture.GenerateCsv();

        Assert.Equal("project synthetic fixture", m.CohortLabel);
        Assert.Equal("project synthetic fixture", m.Evaluation.CohortLabel);
        Assert.Equal("project-synthetic-fixture", m.Dataset.SourceKind);
        Assert.Equal(RiskModelManifestCodec.Sha256Hex(csv), m.Dataset.SourceSha256);
        Assert.True(RiskDatasetEtl.ManifestHashMatches(m.Dataset));
        Assert.True(m.Dataset.MetricsEligible);
        Assert.Equal(RiskFeaturesV1.FeatureNames, m.FeatureNames);
        Assert.Equal(new RiskTrainerOptions(), m.Trainer);
        Assert.Equal(8201, m.Trainer.Seed);
        Assert.Equal("5.0.1", m.Environment.MlNetPackageVersion);
        Assert.Equal("5.0.1", m.Environment.FastTreePackageVersion);
        Assert.Equal(DateTimeOffset.Parse(m.TrainedAtUtc, CultureInfo.InvariantCulture).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), m.TrainedAtUtc);
        Assert.True(m.LoadedEqualsFittedOnAllRows);
        Assert.Equal(FixtureExamples.Value.Count, m.ReproducibilityRowsChecked);
        Assert.Contains(m.Limitations, l => l.Contains("never edited", StringComparison.Ordinal) || l.Contains("this file is never edited", StringComparison.Ordinal));
        Assert.Contains(m.Evaluation.Limitations, l => l.Contains("not PaySim", StringComparison.Ordinal));
        Assert.Contains("v7", m.Evaluation.BaselineNote, StringComparison.Ordinal);
        Assert.Equal(2, m.Evaluation.Baselines.Length);
    }

    [Fact]
    public void Split_counts_in_the_manifest_equal_the_regenerated_fixture_and_every_cohort_has_both_classes()
    {
        var m = Load().Manifest;
        foreach (var split in new[] { RiskSplit.Train, RiskSplit.Validation, RiskSplit.Test })
        {
            var rows = FixtureExamples.Value.Where(e => e.Split == split).ToArray();
            var summary = m.Dataset.Splits.Single(s => s.Split == split.ToString());
            Assert.Equal(rows.LongLength, summary.Rows);
            Assert.Equal(rows.LongCount(r => r.Positive), summary.Positives);
            Assert.True(summary.Positives > 0 && summary.Negatives > 0, $"{split} lacks a class");
        }
    }

    // ---- scoring, calibration, PPM, reproducibility ----

    [Fact]
    public void Reference_predictions_in_the_manifest_reproduce_from_the_loaded_artifact()
    {
        var scorer = Load();
        Assert.Equal(RiskModelTrainer.ReferenceInputs.Count, scorer.Manifest.ReferencePredictions.Length);
        foreach (var reference in scorer.Manifest.ReferencePredictions)
        {
            var snapshot = RiskFeaturesV1.Build(reference.Currency, reference.AmountMinor, reference.SourcePostedBeforeMinor).Value;
            Assert.Equal(reference.FeatureHash, snapshot.FeatureHash);
            var score = scorer.Score(snapshot.Vector).Value;
            Assert.Equal(reference.ScorePpm, score);
            Assert.Equal(reference.HoldAtThreshold, RiskScoreNormalization.AtOrAboveThreshold(score, scorer.ThresholdPpm));
        }
    }

    [Fact]
    public void Probability_is_finite_in_unit_range_and_ppm_is_the_exact_half_away_from_zero_rounding()
    {
        var scorer = Load();
        var vectors = FixtureExamples.Value.Select(e => RiskFeaturesV1.Vector(e.Raw)).ToArray();
        var probabilities = scorer.Probabilities(vectors);
        var ppm = scorer.ScoreBatch(vectors).Value;

        Assert.Equal(vectors.Length, probabilities.Length);
        for (var i = 0; i < probabilities.Length; i++)
        {
            Assert.True(float.IsFinite(probabilities[i]) && probabilities[i] >= 0f && probabilities[i] <= 1f, $"row {i}: {probabilities[i]}");
            // Independent formula: floor(p * 1e6 + 0.5) is round-half-up, which is round-half-away-from-zero for p >= 0.
            Assert.Equal((int)Math.Floor((double)probabilities[i] * 1_000_000d + 0.5d), ppm[i]);
        }
        Assert.True(ppm.Distinct().Count() > 50, "the calibrated score should be graded, not a few constants");
    }

    [Fact]
    public void Scores_are_reproducible_across_repeated_calls_fresh_loads_and_concurrent_callers()
    {
        var first = Load();
        var second = Load();
        var vectors = FixtureExamples.Value.Take(400).Select(e => RiskFeaturesV1.Vector(e.Raw)).ToArray();
        var expected = first.ScoreBatch(vectors).Value;

        Assert.Equal(expected, first.ScoreBatch(vectors).Value);
        Assert.Equal(expected, second.ScoreBatch(vectors).Value);

        var results = new int[8][];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => results[i] = first.ScoreBatch(vectors).Value);
        Assert.All(results, r => Assert.Equal(expected, r));
        Assert.Equal(expected[0], first.Score(vectors[0]).Value);
    }

    [Fact]
    public void Held_out_metrics_in_the_manifest_equal_a_recomputation_from_the_loaded_artifact_and_regenerated_fixture()
    {
        var scorer = Load();
        var recorded = scorer.Manifest.Evaluation;
        foreach (var (split, metrics) in new[] { (RiskSplit.Validation, recorded.Validation), (RiskSplit.Test, recorded.HeldOutTest), (RiskSplit.Train, recorded.TrainInSample) })
        {
            var rows = FixtureExamples.Value.Where(e => e.Split == split).ToArray();
            var scores = scorer.ScoreBatch(rows.Select(r => RiskFeaturesV1.Vector(r.Raw)).ToArray()).Value;
            var recomputed = RiskEvaluation.Compute("x", rows.Select((r, i) => new ScoredExample(scores[i], r.Positive)).ToArray(), scorer.ThresholdPpm);

            Assert.Equal(
                (metrics.TruePositives, metrics.FalsePositives, metrics.TrueNegatives, metrics.FalseNegatives, metrics.Rows, metrics.Positives),
                (recomputed.TruePositives, recomputed.FalsePositives, recomputed.TrueNegatives, recomputed.FalseNegatives, recomputed.Rows, recomputed.Positives));
            Assert.Equal(metrics.PrAuc, recomputed.PrAuc);
            Assert.Equal(metrics.RocAuc, recomputed.RocAuc);
            Assert.Equal(metrics.F1, recomputed.F1);
        }
        Assert.Empty(recorded.HeldOutTest.Undefined);
        Assert.True(recorded.HeldOutTest.Positives > 0 && recorded.HeldOutTest.Negatives > 0);
    }

    [Fact]
    public void The_pinned_threshold_is_what_the_validation_rule_selects_from_the_loaded_artifact()
    {
        var scorer = Load();
        var rows = FixtureExamples.Value.Where(e => e.Split == RiskSplit.Validation).ToArray();
        var scores = scorer.ScoreBatch(rows.Select(r => RiskFeaturesV1.Vector(r.Raw)).ToArray()).Value;
        var selected = RiskEvaluation.SelectThreshold(rows.Select((r, i) => new ScoredExample(scores[i], r.Positive)).ToArray()).Value;

        Assert.Equal(selected, scorer.Manifest.ThresholdSelection);
        Assert.Equal(selected.ThresholdPpm, scorer.ThresholdPpm);
    }

    [Fact]
    public void Roc_auc_agrees_with_the_ml_net_evaluator_on_the_held_out_rows()
    {
        // Independent oracle: ML.NET's own BinaryClassification evaluator over the saved ZIP, using raw float probabilities.
        var ml = new MLContext(seed: 1);
        var model = ml.Model.Load(Path.Combine(ArtifactDirectory, RiskModelScorer.ArtifactFileName), out _);
        var test = FixtureExamples.Value.Where(e => e.Split == RiskSplit.Test).ToArray();
        var scored = model.Transform(ml.Data.LoadFromEnumerable(test.Select(e => new RiskModelInput { Label = e.Positive, Features = RiskFeaturesV1.Vector(e.Raw).ToArray() }).ToList()));
        var oracle = ml.BinaryClassification.Evaluate(scored, "Label");

        var mine = Load().Manifest.Evaluation.HeldOutTest;
        Assert.Equal(oracle.AreaUnderRocCurve, mine.RocAuc!.Value, 3);
        // ML.NET's AUPRC uses a different interpolation than step-wise average precision; require the same ballpark, not equality.
        Assert.InRange(Math.Abs(oracle.AreaUnderPrecisionRecallCurve - mine.PrAuc!.Value), 0, 0.05);
    }

    [Fact]
    public void The_model_beats_the_never_hold_and_drain_flag_baselines_on_the_held_out_fixture_rows()
    {
        var evaluation = Load().Manifest.Evaluation;
        var never = evaluation.Baselines[0];
        var drain = evaluation.Baselines[1];

        Assert.Equal(0, never.TruePositives);
        Assert.Null(never.Precision);
        Assert.True(evaluation.HeldOutTest.RocAuc > drain.RocAuc && evaluation.HeldOutTest.RocAuc > never.RocAuc);
        Assert.True(evaluation.HeldOutTest.F1 > drain.F1);
    }

    [Fact]
    public void Scorer_identity_can_back_an_inference_evidence_record_without_touching_the_ledger()
    {
        var scorer = Load();
        var reference = scorer.Manifest.ReferencePredictions[0];
        var features = RiskFeaturesV1.Build(reference.Currency, reference.AmountMinor, reference.SourcePostedBeforeMinor).Value;
        var score = scorer.Score(features.Vector).Value;
        var id = scorer.Identity;

        var evidence = new InferenceEvidenceV1(
            new ScenarioRunId(Guid.Parse("11111111-1111-1111-1111-111111111111")), "intent-1", 10, 11, features.Raw,
            id.ModelId, id.ArtifactSha256, id.ManifestSha256, id.MlNetPackageVersion, "risk-policy-v1-offline-proof", id.ThresholdPpm, score, ScreeningDecisionKind.Clear);

        Assert.Equal(reference.HoldAtThreshold, evidence.LearnedHold);
        Assert.True(InferenceEvidenceV1.Decode(evidence.ToCanonicalBytes()).IsSuccess);
    }

    // ---- failure behaviour ----

    [Fact]
    public void A_tampered_or_truncated_artifact_fails_the_checksum_before_ml_net_sees_it()
    {
        var dir = CopyArtifact();
        try
        {
            var path = Path.Combine(dir.FullName, RiskModelScorer.ArtifactFileName);
            var bytes = File.ReadAllBytes(path);
            bytes[bytes.Length / 2] ^= 0x01;
            File.WriteAllBytes(path, bytes);
            Assert.Equal(RiskModelError.ArtifactChecksumMismatch, LoadFailure(dir.FullName).Code);

            File.WriteAllBytes(path, bytes[..^10]);
            Assert.Equal(RiskModelError.ArtifactChecksumMismatch, LoadFailure(dir.FullName).Code);

            File.Delete(path);
            Assert.Equal(RiskModelError.ArtifactMissing, LoadFailure(dir.FullName).Code);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void A_tampered_malformed_or_missing_manifest_fails_typed()
    {
        var dir = CopyArtifact();
        try
        {
            var path = Path.Combine(dir.FullName, RiskModelScorer.ManifestFileName);
            var original = File.ReadAllText(path);
            var m = ReadManifest(dir.FullName);

            File.WriteAllText(path, original.Replace($"\"ThresholdPpm\": {m.ThresholdPpm},", $"\"ThresholdPpm\": {m.ThresholdPpm - 1},", StringComparison.Ordinal));
            Assert.Equal(RiskModelError.ManifestHashMismatch, LoadFailure(dir.FullName).Code);

            File.WriteAllText(path, "{ not json");
            Assert.Equal(RiskModelError.ManifestMalformed, LoadFailure(dir.FullName).Code);

            WriteManifest(dir.FullName, RiskModelManifestCodec.Seal(m with { ManifestSchema = "risk-model-manifest/0" }));
            Assert.Equal(RiskModelError.ManifestMalformed, LoadFailure(dir.FullName).Code);

            File.Delete(path);
            Assert.Equal(RiskModelError.ManifestMissing, LoadFailure(dir.FullName).Code);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void An_incompatible_feature_schema_declaration_fails_even_with_a_valid_seal()
    {
        var dir = CopyArtifact();
        try
        {
            var m = ReadManifest(dir.FullName);
            string[][] bad =
            [
                [m.FeatureNames[1], m.FeatureNames[0], m.FeatureNames[2], m.FeatureNames[3]],      // reordered
                [.. m.FeatureNames.Take(3)],                                                         // missing one
                [m.FeatureNames[0], m.FeatureNames[1], m.FeatureNames[2], "isFraud"],                // forbidden predictor
                [.. m.FeatureNames, "newbalanceOrig"],                                               // extra leak
            ];
            foreach (var names in bad)
            {
                WriteManifest(dir.FullName, RiskModelManifestCodec.Seal(m with { FeatureNames = names }));
                Assert.Equal(RiskModelError.FeatureSchemaIncompatible, LoadFailure(dir.FullName).Code);
            }

            WriteManifest(dir.FullName, RiskModelManifestCodec.Seal(m with { FeatureSchemaId = "risk-features-v2" }));
            Assert.Equal(RiskModelError.FeatureSchemaIncompatible, LoadFailure(dir.FullName).Code);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void A_correctly_checksummed_model_with_a_different_input_width_fails_the_ml_net_schema_check()
    {
        var dir = CopyArtifact();
        try
        {
            var ml = new MLContext(seed: 3);
            var rows = Enumerable.Range(0, 200).Select(i => new ThreeFeatureRow { Label = i % 5 == 0, Features = [i % 7, i % 5, i % 3] }).ToList();
            var data = ml.Data.LoadFromEnumerable(rows);
            var model = ml.BinaryClassification.Trainers.FastTree(new FastTreeBinaryTrainer.Options { NumberOfLeaves = 4, NumberOfTrees = 5, Seed = 3, NumberOfThreads = 1 }).Fit(data);
            var zipPath = Path.Combine(dir.FullName, RiskModelScorer.ArtifactFileName);
            using (var file = File.Create(zipPath))
            {
                ml.Model.Save(model, data.Schema, file);
            }
            var bytes = File.ReadAllBytes(zipPath);
            var m = ReadManifest(dir.FullName);
            WriteManifest(dir.FullName, RiskModelManifestCodec.Seal(m with { ArtifactSha256 = RiskModelManifestCodec.Sha256Hex(bytes), ArtifactBytes = bytes.LongLength }));

            Assert.Equal(RiskModelError.ModelSchemaIncompatible, LoadFailure(dir.FullName).Code);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Garbage_with_a_matching_checksum_and_a_different_package_version_fail_typed()
    {
        var dir = CopyArtifact();
        try
        {
            var m = ReadManifest(dir.FullName);
            var garbage = Encoding.UTF8.GetBytes("this is not a zip");
            File.WriteAllBytes(Path.Combine(dir.FullName, RiskModelScorer.ArtifactFileName), garbage);
            WriteManifest(dir.FullName, RiskModelManifestCodec.Seal(m with { ArtifactSha256 = RiskModelManifestCodec.Sha256Hex(garbage), ArtifactBytes = garbage.LongLength }));
            Assert.Equal(RiskModelError.ModelLoadFailed, LoadFailure(dir.FullName).Code);

            File.Copy(Path.Combine(ArtifactDirectory, RiskModelScorer.ArtifactFileName), Path.Combine(dir.FullName, RiskModelScorer.ArtifactFileName), overwrite: true);
            WriteManifest(dir.FullName, RiskModelManifestCodec.Seal(m with { Environment = m.Environment with { MlNetPackageVersion = "4.0.0" } }));
            Assert.Equal(RiskModelError.PackageVersionMismatch, LoadFailure(dir.FullName).Code);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    // ---- real training: determinism, no test-label influence, cost ----

    [Fact]
    public void Retraining_is_deterministic_in_process_and_never_depends_on_held_out_test_labels()
    {
        var csv = RiskFixture.GenerateCsv();
        var flipped = FlipTestLabels(csv, firstTestStep: 192);
        Assert.NotEqual(RiskModelManifestCodec.Sha256Hex(csv), RiskModelManifestCodec.Sha256Hex(flipped));

        var a = Directory.CreateTempSubdirectory("risk-train-a-");
        var b = Directory.CreateTempSubdirectory("risk-train-b-");
        try
        {
            var clock = Stopwatch.StartNew();
            var original = RiskModelTrainer.Train(() => new MemoryStream(csv), RiskFixture.Description, a.FullName, DateTimeOffset.UnixEpoch, new RiskTrainerOptions());
            var wall = clock.Elapsed;
            var changed = RiskModelTrainer.Train(() => new MemoryStream(flipped), RiskFixture.Description, b.FullName, DateTimeOffset.UnixEpoch, new RiskTrainerOptions());

            // Same model on every row, same threshold, same train/validation numbers: only the test evaluation differs.
            var vectors = FixtureExamples.Value.Select(e => RiskFeaturesV1.Vector(e.Raw)).ToArray();
            Assert.Equal(original.Scorer.Probabilities(vectors), changed.Scorer.Probabilities(vectors));
            Assert.Equal(original.Manifest.ThresholdPpm, changed.Manifest.ThresholdPpm);
            Assert.Equal(original.Manifest.ThresholdSelection, changed.Manifest.ThresholdSelection);
            Assert.Equal(JsonSerializer.Serialize(original.Manifest.Evaluation.Validation), JsonSerializer.Serialize(changed.Manifest.Evaluation.Validation));
            Assert.Equal(JsonSerializer.Serialize(original.Manifest.Evaluation.TrainInSample), JsonSerializer.Serialize(changed.Manifest.Evaluation.TrainInSample));
            Assert.NotEqual(original.Manifest.Evaluation.HeldOutTest.TruePositives, changed.Manifest.Evaluation.HeldOutTest.TruePositives);

            Assert.True(original.Manifest.LoadedEqualsFittedOnAllRows);
            Assert.True(wall < TimeSpan.FromSeconds(60), $"fixture training took {wall}");
            Assert.True(new FileInfo(original.ArtifactPath).Length < 200_000);
        }
        finally
        {
            a.Delete(true);
            b.Delete(true);
        }
    }

    [Fact]
    public void Training_refuses_a_cohort_that_lacks_a_class_in_any_split()
    {
        var csv = Encoding.UTF8.GetString(RiskFixture.GenerateCsv()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Remove every positive from the late steps so validation/test cannot be evaluated.
        var kept = csv.Where((l, i) => i == 0 || !(l.Split(',') is { Length: 12 } f && f[9] == "1" && int.Parse(f[0], CultureInfo.InvariantCulture) >= 144)).ToArray();
        var dir = Directory.CreateTempSubdirectory("risk-train-bad-");
        try
        {
            Assert.Throws<RiskEtlException>(() => RiskModelTrainer.Train(
                () => new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', kept) + "\n")), RiskFixture.Description, dir.FullName, DateTimeOffset.UnixEpoch, new RiskTrainerOptions()));
            Assert.False(File.Exists(Path.Combine(dir.FullName, RiskModelScorer.ManifestFileName)), "no manifest may be written for an unevaluable cohort");
        }
        finally
        {
            dir.Delete(true);
        }
    }

    // ---- boundaries ----

    [Fact]
    public void Marrow_and_the_shared_contracts_do_not_reference_ml_net()
    {
        foreach (var assembly in new[] { typeof(Marrow.Kernel.Results.Result<,>).Assembly, typeof(PaymentIntent).Assembly, Assembly.Load("Marrow.Timeline") })
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), r => r.Name!.StartsWith("Microsoft.ML", StringComparison.Ordinal));
        }
    }

    private static byte[] FlipTestLabels(byte[] csv, int firstTestStep)
    {
        var lines = Encoding.UTF8.GetString(csv).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var output = new List<string> { lines[0] };
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(',');
            if (int.Parse(f[0], CultureInfo.InvariantCulture) >= firstTestStep)
            {
                f[9] = f[9] == "1" ? "0" : "1";
            }
            output.Add(string.Join(',', f));
        }
        return Encoding.UTF8.GetBytes(string.Join('\n', output) + "\n");
    }
}
