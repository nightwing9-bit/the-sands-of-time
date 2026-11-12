using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using Microsoft.ML.Trainers.FastTree;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Scenario;
using Revenant.Synthetic;

namespace Revenant.Tests.Intelligence;

internal sealed record R5Configuration(string Id, string Family, string Features, float L2, int Leaves, int Trees, int MinLeaf);
internal sealed record R5Selection(string DatasetSha256, string FeatureSha256, string CandidateSha256, R5Configuration Config,
    int TrainingSeed, string Preprocessing, string Runtime, string ExperimentRevision);
internal sealed record R5Freeze(R5Selection Selection, string CalibrationMethod, double Slope, double Intercept, int ThresholdPpm,
    string ReviewPolicy, IReadOnlyDictionary<string, string> SplitSha256);

internal static partial class R5Experiment
{
    private const int Seed = 17081129;
    private const string CalibrationMethod = "calibration-root-monotonic-sigmoid-on-raw-score/1";
    private const string ReviewPolicy = "v7-mandatory-plus-equal-capacity/1:5%;sensitivity=2%,10%;probability-ppm-away-from-zero";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly R5Configuration Logistic = new("logistic-l2-0.1", "logistic", "full", .1f, 0, 0, 0);
    private static readonly R5Configuration[] Candidates =
    [
        Logistic, Logistic with { Id = "logistic-l2-2", L2 = 2 },
        new("fasttree-4x35", "fasttree", "full", 0, 4, 35, 25),
        new("fasttree-8x60", "fasttree", "full", 0, 8, 60, 18),
    ];

    internal static void Select(V4RiskDatasetReader reader, string output, string revision)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("The authoritative R5 experiment runs on Linux x64.");
        if (reader.Manifest.Configuration != V4EpisodeConfig.RiskQualification)
            throw new InvalidOperationException("Training requires the pinned 12/4/4/4 design.");
        var train = reader.ReadSplit(V4DatasetSplit.Train);
        var validation = reader.ReadSplit(V4DatasetSplit.Validation);
        var time = FitAndMeasure(Logistic with { Id = "time-coverage", Features = "time" }, train, validation);
        var flags = FitAndMeasure(Logistic with { Id = "amount-flags", Features = "flags" }, train, validation);
        var trainEvidence = Baselines(train);
        var validationEvidence = Baselines(validation);
        var prevalence = (double)validation.Count(IsMalicious) / validation.Count;
        // Fail before fitting production candidates if either metadata-only control separates nearly perfectly.
        var qualified = time.Result.Ranking.RocAuc < .85 && time.Result.Ranking.AveragePrecision < .75
            && flags.Result.Ranking.RocAuc < .85 && flags.Result.Ranking.AveragePrecision < .75;
        Write(output, "shortcut-diagnostics.json", new
        {
            Dataset = reader.Manifest.Sha256, Prevalence = prevalence, Qualified = qualified,
            Guard = "Validation ROC-AUC < 0.85 and average precision < 0.75 for both controls.",
            Time = time.Result, AmountFlags = flags.Result, TrainBaselines = trainEvidence, ValidationBaselines = validationEvidence,
            Banks = validation.GroupBy(x => x.Bank).ToDictionary(x => x.Key, x => new { Rows = x.Count(), Malicious = x.Count(IsMalicious) }),
            Activity = new[] { ActivityMix("Train", train), ActivityMix("Validation", validation) },
        });
        if (!qualified) return;

        var permutedLabels = train.Select(IsMalicious).ToArray();
        foreach (var group in train.Select((row, index) => (row.Root, index)).GroupBy(x => x.Root))
        {
            var indices = group.Select(x => x.index).ToArray();
            var labels = indices.Select(i => permutedLabels[i]).ToArray();
            SeededRandom.ForStream(Seed, (ulong)indices[0]).Shuffle(labels);
            for (var i = 0; i < indices.Length; i++) permutedLabels[indices[i]] = labels[i];
        }
        var negative = FitAndMeasure(Logistic with { Id = "root-stratified-permutation" }, train, validation, permutedLabels);
        var ablations = new[] { "money", "current", "current-history", "history-no-novelty", "full-time" }
            .Select(f => FitAndMeasure(Logistic with { Id = "ablation-" + f, Features = f }, train, validation).Result).ToArray();
        var experiments = Candidates.Select(c => FitAndMeasure(c, train, validation)).ToArray();
        var best = experiments.OrderByDescending(c => c.Result.Combined.Recall).ThenByDescending(c => c.Result.Combined.Precision)
            .ThenByDescending(c => c.Result.Rings).ThenByDescending(c => c.Result.Families)
            .ThenBy(c => c.Result.Combined.HardNegativesReviewed).ThenByDescending(c => c.Result.RootsWithMaliciousReview)
            .ThenByDescending(c => c.Result.Ranking.AveragePrecision)
            .ThenBy(c => c.Result.Config.Family == "logistic" ? 0 : c.Result.Config.Leaves).First();
        var behavioralAdvantage = best.Result.Combined.MaliciousReviewed > time.Result.Combined.MaliciousReviewed
            && best.Result.Ranking.AveragePrecision > time.Result.Ranking.AveragePrecision + .05;
        var controlPassed = negative.Result.Ranking.RocAuc < .65 && negative.Result.Ranking.AveragePrecision < prevalence + .15;
        Write(output, "validation.json", new
        {
            CandidateResults = experiments.Select(c => c.Result).ToArray(), Ablations = ablations, NegativeControl = negative.Result,
            Selected = best.Result, BehavioralAdvantage = behavioralAdvantage, NegativeControlPassed = controlPassed,
            Guard = "Selected behavioral model must exceed the time control in combined 5% detections and AP by >0.05; permuted labels must not yield ROC-AUC >=0.65 or AP >= prevalence+0.15.",
        });
        if (!behavioralAdvantage || !controlPassed) return;

        var modelPath = Path.Combine(output, "candidate.zip");
        using (var file = File.Open(modelPath, FileMode.CreateNew))
            best.Model.Context.Model.Save(best.Model.Fitted, best.Model.Schema, file);
        var zip = File.ReadAllBytes(modelPath);
        var after = Load(zip);
        var validationVectors = validation.Select(r => r.Vector.ToModelInput()).ToArray();
        if (!best.Model.Score(validationVectors).SequenceEqual(after.Score(validationVectors)))
            throw new InvalidOperationException("Candidate save/load changed raw scores.");
        Write(output, "selection.json", new R5Selection(reader.Manifest.Sha256, RiskFeatureVectorV2.SchemaHash,
            ObservationEncoding.Hash(zip), best.Result.Config, Seed,
            best.Result.Config.Family == "logistic" ? "train-fitted-normalize-minmax/fix-zero" : "canonical-exact-single",
            "ML.NET 5.0.1; .NET 10; Linux x64", revision));

        static object ActivityMix(string split, IReadOnlyList<V4RiskResearchRow> rows) => new
        {
            Split = split,
            Banks = rows.GroupBy(x => x.Bank).ToDictionary(x => x.Key, x => new { Rows = x.Count(), Malicious = x.Count(IsMalicious) }),
            // Observable proxies, not hidden planner family IDs.
            Groups = rows.GroupBy(r => r.AsOf.Windows[1].SubmittedCount >= 2 ? "recent outgoing burst"
                    : r.AsOf.BeneficiarySeenBefore ? "familiar beneficiary"
                    : r.AsOf.AmountMinor >= 100_000 ? "large novel payment"
                    : r.AsOf.Windows[^1].SubmittedCount == 0 ? "infrequent sender" : "other novel payment")
                .Select(g => new { Activity = g.Key, Malicious = g.Count(IsMalicious), Legitimate = g.Count(r => !IsMalicious(r)),
                    HardLegitimate = g.Count(r => r.Truth.HardNegative) }).ToArray(),
        };
    }

    internal static void Calibrate(V4RiskDatasetReader reader, string input, string output)
    {
        var selection = Read<R5Selection>(input, "selection.json");
        var model = VerifyCandidate(reader, input, selection);
        var rows = reader.ReadSplit(V4DatasetSplit.Calibration);
        var scores = Index(rows, model.Score(rows.Select(r => r.Vector.ToModelInput()).ToArray()));
        var (slope, intercept) = FitSigmoid(rows.Select(r => (scores[r], IsMalicious(r))).ToArray());
        double Probability(V4RiskResearchRow r) => Sigmoid(slope * scores[r] + intercept);
        var available = V4RiskReviewBudget.Slots(rows.Count, 5) - rows.Count(IsHeld);
        var ranked = rows.Where(IsCleared).Select(r => Ppm(Probability(r))).OrderDescending().ToArray();
        if (available < 0) throw new InvalidOperationException("Mandatory Calibration holds exceed budget.");
        var threshold = available >= ranked.Length ? 1 : Math.Max(1, checked(ranked[available] + 1));
        if (threshold > 1_000_000) throw new InvalidOperationException("Calibration ties prevent a feasible threshold.");
        var report = Threshold(rows, Probability, threshold);
        if (report.Review.BudgetExceeded) throw new InvalidOperationException("Calibrated threshold exceeds development capacity.");
        Write(output, "calibration.json", new
        {
            Calibrated = report, Uncalibrated = Threshold(rows, r => Sigmoid(scores[r]), threshold),
            Limitation = "Probabilities describe the pinned synthetic calibration distribution only.",
        });
        var freeze = new R5Freeze(selection, CalibrationMethod, slope, intercept, threshold, ReviewPolicy,
            reader.Manifest.Splits.ToDictionary(s => s.Split.ToString(), s => V4DatasetExporter.SplitDigest("r5-experiment-split/1", s.Tables)));
        Write(output, "freeze.json", freeze);
    }

    internal static void Evaluate(V4RiskDatasetReader reader, V4RiskDatasetReader challenge, string input, string output, string freezePin)
    {
        var freezeBytes = File.ReadAllBytes(Path.Combine(input, "freeze.json"));
        ObservationEncoding.RequireDigest(freezeBytes, freezePin);
        var freeze = JsonSerializer.Deserialize<R5Freeze>(freezeBytes) ?? throw new InvalidOperationException("Missing freeze.");
        var model = VerifyCandidate(reader, input, freeze.Selection);
        if (freeze.CalibrationMethod != CalibrationMethod || freeze.ReviewPolicy != ReviewPolicy || freeze.ThresholdPpm is < 1 or > 1_000_000
            || !double.IsFinite(freeze.Slope) || freeze.Slope <= 0 || !double.IsFinite(freeze.Intercept)
            || freeze.SplitSha256.Count != reader.Manifest.Splits.Count
            || reader.Manifest.Splits.Any(s => freeze.SplitSha256[s.Split.ToString()] != V4DatasetExporter.SplitDigest("r5-experiment-split/1", s.Tables)))
            throw new InvalidOperationException("Freeze pins disagree with the experiment.");
        if (challenge.Manifest.Configuration != V4EpisodeConfig.RiskRingChallenge
            || challenge.Manifest.Roots.Select(r => r.Key).Intersect(reader.Manifest.Roots.Select(r => r.Key)).Any())
            throw new InvalidOperationException("Invalid independent challenge identity.");

        var final = Assess(reader.ReadSplit(V4DatasetSplit.FinalTest));
        var ring = Assess(challenge.ReadSplit(V4DatasetSplit.FinalTest));
        var primary = final.Combined.Single(r => r.Percent == 5).Review;
        var baseline = final.Baselines.Single(r => r.Percent == 5).V7Heuristic;
        var ringPrimary = ring.Combined.Single(r => r.Percent == 5).Review;
        var extraRings = ringPrimary.Families["Ring"].MaliciousReviewed - ring.Baselines.Single(r => r.Percent == 5).V7.Families["Ring"].MaliciousReviewed;
        var qualifies = primary.AdditionalMalicious > baseline.AdditionalMalicious
            && primary.Families["Ring"].MaliciousReviewed > 0 && extraRings > 0
            && primary.Families.Count(f => f.Key != "Legitimate" && f.Value.MaliciousReviewed > 0) >= 2
            && primary.HardNegativesReviewed <= baseline.HardNegativesReviewed && !primary.BudgetExceeded && !ringPrimary.BudgetExceeded;
        Write(output, "independent-evaluation.json", new
        {
            FreezeSha256 = freezePin, ChallengeDatasetSha256 = challenge.Manifest.Sha256,
            FinalTest = final, Challenge = ring, ChallengeIncrementalRings = extraRings,
            CandidateQualifiesForArtifactVerification = qualifies,
            Guard = "Strict additional-malicious uplift over v7+heuristic, ring review in FinalTest and incremental ring review in challenge, at least two families, no extra hard-negative burden, no 5% top-K budget overload.",
        });

        R5Evaluation Assess(IReadOnlyList<V4RiskResearchRow> rows)
        {
            var scores = Index(rows, model.Score(rows.Select(r => r.Vector.ToModelInput()).ToArray()));
            double Raw(V4RiskResearchRow r) => scores[r];
            double Probability(V4RiskResearchRow r) => Sigmoid(freeze.Slope * Raw(r) + freeze.Intercept);
            return new(Ranking(rows, Raw), Baselines(rows),
                Budgets.Select(p => new R5BudgetEvaluation(p, Review(rows, V4RiskReviewBudget.Evaluate(rows, p, false, Raw), false))).ToArray(),
                Budgets.Select(p => new R5BudgetEvaluation(p, Review(rows, V4RiskReviewBudget.Evaluate(rows, p, true, Raw), true))).ToArray(),
                Threshold(rows, Probability, freeze.ThresholdPpm), Bootstrap(rows, Raw));
        }
    }

    private sealed record R5BudgetEvaluation(int Percent, R5Review Review);
    private sealed record R5Evaluation(R5Ranking Ranking, R5Baseline[] Baselines, R5BudgetEvaluation[] Intrinsic,
        R5BudgetEvaluation[] Combined, R5ThresholdReport Threshold, object RecallDeltaRootBootstrap);

    private static R5Fitted VerifyCandidate(V4RiskDatasetReader reader, string input, R5Selection selection)
    {
        if (selection.DatasetSha256 != reader.Manifest.Sha256 || selection.FeatureSha256 != RiskFeatureVectorV2.SchemaHash
            || selection.TrainingSeed != Seed || !Candidates.Contains(selection.Config))
            throw new InvalidOperationException("Candidate selection does not match the pinned experiment.");
        var zip = File.ReadAllBytes(Path.Combine(input, "candidate.zip"));
        ObservationEncoding.RequireDigest(zip, selection.CandidateSha256);
        return Load(zip);
    }

    private static (R5CandidateResult Result, R5Fitted Model) FitAndMeasure(R5Configuration c,
        IReadOnlyList<V4RiskResearchRow> train, IReadOnlyList<V4RiskResearchRow> validation, bool[]? trainingLabels = null)
    {
        var ml = new MLContext(Seed);
        var training = train.Select((r, i) => new Input { Features = Features(r, c.Features), Label = trainingLabels?[i] ?? IsMalicious(r) }).ToArray();
        var data = View(ml, training);
        ITransformer fitted = c.Family switch
        {
            "logistic" => ml.Transforms.NormalizeMinMax("Normalized", "Features", fixZero: true).Append(
                ml.BinaryClassification.Trainers.LbfgsLogisticRegression(new LbfgsLogisticRegressionBinaryTrainer.Options
                {
                    LabelColumnName = "Label", FeatureColumnName = "Normalized", L2Regularization = c.L2,
                    L1Regularization = 0, NumberOfThreads = 1, MaximumNumberOfIterations = 120,
                })).Fit(data),
            "fasttree" => ml.BinaryClassification.Trainers.FastTree(new FastTreeBinaryTrainer.Options
            {
                LabelColumnName = "Label", FeatureColumnName = "Features", NumberOfThreads = 1, Seed = Seed,
                NumberOfTrees = c.Trees, NumberOfLeaves = c.Leaves, MinimumExampleCountPerLeaf = c.MinLeaf, LearningRate = .08,
            }).Fit(data),
            _ => throw new InvalidOperationException("Unsupported candidate family."),
        };
        var model = new R5Fitted(ml, fitted, data.Schema);
        var scored = Index(validation, model.Score(validation.Select(r => Features(r, c.Features)).ToArray()));
        double Score(V4RiskResearchRow r) => scored[r];
        var combined = Review(validation, V4RiskReviewBudget.Evaluate(validation, 5, true, Score), true);
        return (new(c, Ranking(validation, Score), Review(validation, V4RiskReviewBudget.Evaluate(validation, 5, false, Score), false),
            combined, combined.Families["Ring"].MaliciousReviewed,
            combined.Families.Count(f => f.Key != "Legitimate" && f.Value.MaliciousReviewed > 0),
            combined.Roots.Count(r => r.Value.MaliciousReviewed > 0)), model);
    }

    private static float[] Features(V4RiskResearchRow row, string group)
    {
        var vector = row.Vector.ToModelInput();
        switch (group)
        {
            case "full": break;
            case "money":
                vector[6] = 0;
                Array.Clear(vector, RiskFeatureVectorV2.CurrentCount, vector.Length - RiskFeatureVectorV2.CurrentCount);
                break;
            case "current":
                Array.Clear(vector, RiskFeatureVectorV2.CurrentCount, vector.Length - RiskFeatureVectorV2.CurrentCount);
                break;
            case "history-no-novelty":
                vector[6] = vector[8] = vector[9] = 0;
                goto case "current-history";
            case "current-history":
                Array.Clear(vector, RiskFeatureVectorV2.AccountCount, vector.Length - RiskFeatureVectorV2.AccountCount);
                break;
            case "flags":
                Array.Clear(vector);
                vector[0] = row.Vector.Values[0];
                vector[6] = row.Vector.Values[6];
                vector[7] = row.AsOf.SanctionsHit ? 1 : 0;
                break;
            case "time":
                Array.Clear(vector);
                vector[row.AsOf.UtcMinuteOfDay / 60] = 1;
                vector[24 + row.AsOf.UtcDayOfWeek] = 1;
                vector[31 + (row.At - row.OriginMs) / V4EpisodeConfig.WarmupMs] = 1;
                vector[35] = (row.At - row.OriginMs) / (float)V4EpisodeConfig.HourMs;
                for (var i = 0; i < 4; i++) vector[36 + i] = row.AsOf.Windows[i].Coverage == ObservationCoverage.Complete ? 1 : 0;
                break;
            case "full-time":
                // Diagnostic only: append independent temporal columns without overwriting production features.
                vector = [.. vector, .. Features(row, "time")[..40]];
                break;
            default: throw new InvalidOperationException("Unknown feature group.");
        }
        return vector;
    }

    private sealed class Input
    {
        public bool Label { get; set; }
        [VectorType(70)]
        public float[] Features { get; set; } = [];
    }

    private sealed record R5Fitted(MLContext Context, ITransformer Fitted, DataViewSchema Schema)
    {
        internal double[] Score(IReadOnlyList<float[]> vectors)
        {
            // Labels are deliberately absent from the input values. The default Label value is unused for Score.
            if (vectors.Count == 0 || vectors.Any(v => v.Any(x => !float.IsFinite(x))))
                throw new InvalidOperationException("Missing or nonfinite score input.");
            var data = View(Context, vectors.Select(v => new Input { Features = v }).ToArray());
            var scores = Fitted.Transform(data).GetColumn<float>("Score").Select(f => (double)f).ToArray();
            if (scores.Length != vectors.Count || scores.Any(s => !double.IsFinite(s)))
                throw new InvalidOperationException("Wrong count or nonfinite raw score.");
            return scores;
        }
    }

    private static IDataView View(MLContext ml, Input[] rows)
    {
        var schema = SchemaDefinition.Create(typeof(Input));
        schema["Features"].ColumnType = new VectorDataViewType(NumberDataViewType.Single, rows[0].Features.Length);
        return ml.Data.LoadFromEnumerable(rows, schema);
    }

    private static R5Fitted Load(byte[] zip)
    {
        var ml = new MLContext(Seed);
        using var file = new MemoryStream(zip, writable: false);
        var loaded = ml.Model.Load(file, out var schema);
        if (schema.GetColumnOrNull("Features")?.Type is not VectorDataViewType { Size: 70 } features ||
            features.ItemType != NumberDataViewType.Single)
            throw new InvalidOperationException("Candidate model has the wrong input vector schema.");
        return new(ml, loaded, schema);
    }

    private static Dictionary<V4RiskResearchRow, double> Index(IReadOnlyList<V4RiskResearchRow> rows, double[] scores) =>
        rows.Select((r, i) => (r, s: scores[i])).ToDictionary(x => x.r, x => x.s);

    internal static void Write<T>(string directory, string name, T content)
    {
        using var file = File.Open(Path.Combine(directory, name), FileMode.CreateNew);
        JsonSerializer.Serialize(file, content, Json);
    }

    private static T Read<T>(string directory, string name) =>
        JsonSerializer.Deserialize<T>(File.ReadAllBytes(Path.Combine(directory, name)))
            ?? throw new InvalidOperationException($"Missing {name}.");
}
