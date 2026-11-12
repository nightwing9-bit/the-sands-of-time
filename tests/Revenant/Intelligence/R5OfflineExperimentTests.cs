using Revenant.Synthetic;
using Xunit;

namespace Revenant.Tests.Intelligence;

public sealed class R5OfflineExperimentTests
{
    [Fact]
    [Trait("Category", "OfflineTraining")]
    public void Select_R5_candidate_using_development_splits_only() =>
        R5Experiment.Select(Reader(), Required("R5_EXPERIMENT_OUTPUT"), Required("R5_EXPERIMENT_REVISION"));

    [Fact]
    [Trait("Category", "OfflineTraining")]
    public void Calibrate_R5_selection_in_a_fresh_process() =>
        R5Experiment.Calibrate(Reader(), Required("R5_CANDIDATE_INPUT"), Required("R5_EXPERIMENT_OUTPUT"));

    [Fact]
    [Trait("Category", "OfflineTraining")]
    public void Evaluate_frozen_R5_model_on_FinalTest_and_the_independent_ring_challenge() =>
        R5Experiment.Evaluate(Reader(), new(Required("R5_CHALLENGE_INPUT"), Required("R5_CHALLENGE_SHA")),
            Required("R5_CANDIDATE_INPUT"), Required("R5_EXPERIMENT_OUTPUT"), Required("R5_FREEZE_SHA"));

    private static V4RiskDatasetReader Reader() => new(Required("R5_COHORT_INPUT"), Required("R5_COHORT_SHA"));
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing experiment input {name}.");
}
