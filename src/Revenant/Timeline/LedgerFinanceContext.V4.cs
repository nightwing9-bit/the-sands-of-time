using Marrow.Kernel.Results;
using Revenant.Finance;
using Revenant.Scenario;

namespace Revenant.Timeline;

public sealed partial record LedgerFinanceContext
{
    // Only V4 may use the unchanged ledger transitions with the behavioral scenario.
    internal static Result<LedgerFinanceContext, LedgerFinanceError> FromBehavioralEpisodePins(ObservedFinanceContext context)
    {
        var policy = context.BasePolicy;
        if (context.ScenarioVersion != BehavioralEpisodeScenario.Version
            || (context.RiskMode == ObservedRiskMode.ModelRequired && !V4ModelScreeningEvidence.Supports(context))
            || (policy != PolicyCatalog.RefOf(PolicyCatalog.V7) && policy != PolicyCatalog.RefOf(PolicyCatalog.V8)))
        {
            return Fail("Unsupported V4 behavioral episode pins.");
        }

        return Result<LedgerFinanceContext, LedgerFinanceError>.Success(new(
            context.ScenarioVersion, context.ManifestSha256, FinanceLedgerVersion.ReducerVersion,
            policy, PolicyCatalog.Resolve(policy).Value, context.ReviewPolicy));
    }
}
