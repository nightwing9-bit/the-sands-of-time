using System.Collections.Immutable;
using System.Security.Cryptography;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Results;
using Marrow.Timeline;

using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;

namespace Revenant.Timeline;

public static class ObservedFinanceVersion
{
    public const string ReducerVersion = "revenant-finance-reducer/4";
    public const string InputSchemaVersion = "revenant-finance-fact/4";
    public const string StateSchemaVersion = "revenant-finance-state/4";
    public const string ContextSchemaVersion = "revenant-finance-context/4";
    public const string SchedulerContract = "sim-scheduler/1";
    public const string ObservationContract = "five-minute-seal/1";
    public const string SealSchema = "observation-seal/1";
    public const long BucketMs = 300_000;
    internal const long LastBoundaryMs = long.MaxValue - long.MaxValue % BucketMs;

    public static bool IsBoundary(long time) => time % BucketMs == 0;
    public static long NextBoundary(long boundary) => checked(boundary + BucketMs);
}

public enum ObservedRiskMode : byte { ModelRequired = 1, NotRequired = 2 }
public sealed record ObservedModelPin(string ModelId, string ArtifactSha256);
public sealed record ObservedRiskPin(string ModelId, string ArtifactSha256, int ThresholdPpm);

public sealed record ObservedFinanceContext
{
    public ObservedFinanceContext(string scenarioVersion, string manifestSha256, long originMs, PolicyRef basePolicy,
        string reviewPolicy, ObservedRiskMode riskMode, string riskFeatureSchema, ObservedRiskPin? riskModel,
        ImmutableArray<ObservedModelPin> anomalyModels, ImmutableArray<ObservedModelPin> liquidityModels)
    {
        if (string.IsNullOrWhiteSpace(scenarioVersion) || !ObservedFinanceEncoding.IsManifestHash(manifestSha256)
            || !ObservedFinanceVersion.IsBoundary(originMs) || originMs > long.MaxValue - ObservedFinanceVersion.BucketMs
            || basePolicy is null || PolicyCatalog.Resolve(basePolicy).IsFailure || reviewPolicy != Cases.ReviewPolicy.Version)
        {
            throw new CanonicalFormatException("Invalid V4 scenario, origin or policy pins.");
        }

        var validRisk = riskMode switch
        {
            ObservedRiskMode.NotRequired => riskFeatureSchema == "" && riskModel is null,
            ObservedRiskMode.ModelRequired => !string.IsNullOrWhiteSpace(riskFeatureSchema)
                && riskModel is { ThresholdPpm: > 0 and <= 1_000_000 }
                && !string.IsNullOrWhiteSpace(riskModel.ModelId) && ObservedFinanceEncoding.IsHash(riskModel.ArtifactSha256),
            _ => false,
        };
        if (!validRisk)
        {
            throw new CanonicalFormatException("Invalid V4 risk mode or model pins.");
        }

        ScenarioVersion = scenarioVersion;
        ManifestSha256 = manifestSha256;
        OriginMs = originMs;
        BasePolicy = basePolicy;
        ReviewPolicy = reviewPolicy;
        RiskMode = riskMode;
        RiskFeatureSchema = riskFeatureSchema;
        RiskModel = riskModel;
        AnomalyModels = SortModels(anomalyModels);
        LiquidityModels = SortModels(liquidityModels);
    }

    public string ScenarioVersion { get; }
    public string ManifestSha256 { get; }
    public long OriginMs { get; }
    public PolicyRef BasePolicy { get; }
    public string ReviewPolicy { get; }
    public ObservedRiskMode RiskMode { get; }
    public string RiskFeatureSchema { get; }
    public ObservedRiskPin? RiskModel { get; }
    public ImmutableArray<ObservedModelPin> AnomalyModels { get; }
    public ImmutableArray<ObservedModelPin> LiquidityModels { get; }

    internal Result<LedgerFinanceContext, LedgerFinanceError> LegacyContext() =>
        ScenarioVersion == BehavioralEpisodeScenario.Version
            ? LedgerFinanceContext.FromBehavioralEpisodePins(this)
            : LedgerFinanceContext.FromPins(ScenarioVersion, ManifestSha256, FinanceLedgerVersion.ReducerVersion, BasePolicy, ReviewPolicy);

    private static ImmutableArray<ObservedModelPin> SortModels(ImmutableArray<ObservedModelPin> models)
    {
        if (models.IsDefault || models.Any(m => m is null || string.IsNullOrWhiteSpace(m.ModelId) || !ObservedFinanceEncoding.IsHash(m.ArtifactSha256))
            || models.Select(m => m.ModelId).Distinct(StringComparer.Ordinal).Count() != models.Length)
        {
            throw new CanonicalFormatException("Invalid V4 advisory model pins.");
        }

        return [.. models.OrderBy(m => m.ModelId, StringComparer.Ordinal)];
    }
}

public enum ObservedFactKind : byte
{
    PaymentIntent = 1, ReviewDecision = 2, ReturnInstruction = 3, Screening = 4, Settlement = 5, ObservationSeal = 6,
}

public sealed record ObservationSealV1(ScenarioRunId RunId, long BoundaryMs, long IntervalStartMs, long PrefixFactSeq,
    string PrefixFactDigestSha256, string ResolvedCommandDigestSha256);

public sealed record ObservedFinanceInput(ScenarioRunId RunId, long Sequence, long EffectiveTimeMs, ObservedFactKind Kind,
    string PayloadSchema, ImmutableArray<byte> Payload, string? EvidenceSchema = null, string? EvidenceSha256 = null)
{
    public static ObservedFinanceInput Seal(ObservationSealV1 seal) =>
        new(seal.RunId, checked(seal.PrefixFactSeq + 1), seal.BoundaryMs, ObservedFactKind.ObservationSeal,
            ObservedFinanceVersion.SealSchema, [.. ObservationSealCodec.Encode(seal)]);
}

public sealed record ObservedFinanceState(LedgerFinanceState Ledger, long LastEffectiveTimeMs, long LastSealedBoundaryMs, string PrefixDigest)
{
    public ScenarioRunId RunId => Ledger.RunId;
    public long LastFact => Ledger.LastFact;
    internal V4ReviewCounts ReviewCounts { get; init; }
}

public static class ObservedFinanceReducer
{
    private const string PrefixContract = "revenant-v4-fact-prefix/1";

    public static string InitialDigest(ScenarioRunId runId) =>
        ObservedFinanceEncoding.Hash(new CanonicalWriter().WriteString(PrefixContract).WriteGuid(runId.Value));

    public static Result<ObservedFinanceState, LedgerFinanceError> Initial(ScenarioRun run, ObservedFinanceContext context)
    {
        var pins = context.LegacyContext();
        if (!pins.TryGetValue(out var legacy) || run.RunId.IsEmpty || run.InitialState.RunId != run.RunId
            || !(context.ScenarioVersion == BehavioralEpisodeScenario.Version
                ? BehavioralEpisodeScenario.MatchesOpening(run, context.OriginMs)
                : ScenarioManifestCodec.HasValidContentHash(run.Manifest)))
        {
            return Fail(LedgerFinanceErrorCode.VersionMismatch, "V4 ledger initialization requires a supported, verified manifest.");
        }

        var initial = FinanceLedgerReducer.Initial(run, legacy);
        return initial.TryGetValue(out var ledger)
            ? Ok(new(ledger, context.OriginMs, context.OriginMs, InitialDigest(run.RunId)))
            : Result<ObservedFinanceState, LedgerFinanceError>.Failure(initial.Error);
    }

    public static Result<ObservedFinanceState, LedgerFinanceError> Apply(
        ObservedFinanceState state, ObservedFinanceInput input, ObservedFinanceContext context)
    {
        if (state.Ledger.ManifestContentHash != context.ManifestSha256 || state.Ledger.ScenarioVersion != context.ScenarioVersion
            || state.LastSealedBoundaryMs < context.OriginMs)
        {
            return Fail(LedgerFinanceErrorCode.VersionMismatch, "V4 context does not match the finance state.");
        }

        if (input.RunId != state.RunId)
        {
            return Fail(LedgerFinanceErrorCode.RunMismatch, "V4 fact names another run.");
        }

        if (state.LastFact == long.MaxValue || input.Sequence != state.LastFact + 1 || input.EffectiveTimeMs < state.LastEffectiveTimeMs)
        {
            return Fail(LedgerFinanceErrorCode.OutOfOrderFact, "V4 fact position or effective time is out of order.");
        }

        try
        {
            ObservedFinanceInputCodec.Validate(input);
            var boundary = ObservedFinanceVersion.NextBoundary(state.LastSealedBoundaryMs);
            var ledger = state.Ledger;
            var reviewCounts = state.ReviewCounts;
            if (context.RiskMode == ObservedRiskMode.ModelRequired ? !V4ModelScreeningEvidence.Supports(context)
                : reviewCounts != default)
            {
                return Fail(LedgerFinanceErrorCode.VersionMismatch, "Unsupported V4 model pins.");
            }
            if (input.Kind == ObservedFactKind.ObservationSeal)
            {
                var seal = ObservationSealCodec.Decode(input.Payload.AsSpan());
                if (seal.BoundaryMs != boundary || state.LastEffectiveTimeMs >= boundary || seal.PrefixFactDigestSha256 != state.PrefixDigest)
                {
                    return Fail(LedgerFinanceErrorCode.InconsistentFact, "V4 seal does not certify the next boundary and existing prefix.");
                }
                ledger = ledger with { LastFact = input.Sequence };
            }
            else
            {
                if (input.EffectiveTimeMs >= boundary)
                {
                    return Fail(LedgerFinanceErrorCode.OutOfOrderFact, "V4 financial fact must wait for the intervening seal.");
                }
                if (input.Kind == ObservedFactKind.Screening && context.RiskMode == ObservedRiskMode.ModelRequired)
                {
                    var evidence = V4ModelScreeningEvidence.Decode(input);
                    evidence.Verify(state, context, input);
                    ledger = ModelLedgerReducer.Screen(ledger, ledger.Payments[evidence.IntentId], evidence.EffectiveReason)
                        with { LastFact = input.Sequence };
                    reviewCounts = evidence.NextCounts;
                }
                else
                {
                    if (input.EvidenceSchema is not null)
                    {
                        return Fail(LedgerFinanceErrorCode.IllegalStep, "Unsupported V4 financial transition.");
                    }
                    var legacy = context.LegacyContext();
                    if (!legacy.TryGetValue(out var pins))
                    {
                        return Result<ObservedFinanceState, LedgerFinanceError>.Failure(legacy.Error);
                    }
                    var result = FinanceLedgerReducer.Apply(ledger,
                        new FinanceFact((FinanceFactKind)input.Kind, input.Sequence, input.PayloadSchema, input.Payload.ToArray(), null), pins);
                    if (!result.TryGetValue(out ledger!))
                    {
                        return Result<ObservedFinanceState, LedgerFinanceError>.Failure(result.Error);
                    }
                }
            }

            return Ok(new(ledger, input.EffectiveTimeMs,
                input.Kind == ObservedFactKind.ObservationSeal ? boundary : state.LastSealedBoundaryMs, AppendDigest(state.PrefixDigest, input))
                { ReviewCounts = reviewCounts });
        }
        catch (Exception ex) when (ex is CanonicalFormatException or OverflowException)
        {
            return Fail(LedgerFinanceErrorCode.MalformedFact, ex.Message);
        }
    }

    internal static string AppendDigest(string previous, ObservedFinanceInput input) =>
        ObservedFinanceEncoding.Hash(new CanonicalWriter().WriteString(PrefixContract).WriteBytes(Convert.FromHexString(previous))
            .WriteInt64(input.Sequence).WriteInt64(input.EffectiveTimeMs).WriteByte((byte)input.Kind)
            .WriteBytes(SHA256.HashData(input.Payload.AsSpan())));

    private static Result<ObservedFinanceState, LedgerFinanceError> Ok(ObservedFinanceState state) =>
        Result<ObservedFinanceState, LedgerFinanceError>.Success(state);

    private static Result<ObservedFinanceState, LedgerFinanceError> Fail(LedgerFinanceErrorCode code, string message) =>
        Result<ObservedFinanceState, LedgerFinanceError>.Failure(new(code, message));
}

public sealed class ObservedFinanceTransitionException(LedgerFinanceError error) : InvalidOperationException(error.ToString())
{
    public LedgerFinanceError Error { get; } = error;
}

public sealed class ObservedFinanceTransition : IDeterministicTransition<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>
{
    public static ObservedFinanceTransition Instance { get; } = new();

    public ObservedFinanceState Apply(ObservedFinanceState previous, long position, IReadOnlyList<OrderedInput<ObservedFinanceInput>> inputs,
        ObservedFinanceContext pinnedContext)
    {
        if (inputs.Count != 1 || inputs[0].Payload.Sequence != position
            || inputs[0].InputId != FinanceLedgerTransition.InputId(previous.RunId, position))
        {
            throw new ObservedFinanceTransitionException(new(LedgerFinanceErrorCode.OutOfOrderFact, "Expected one matching V4 finance fact."));
        }
        var result = ObservedFinanceReducer.Apply(previous, inputs[0].Payload, pinnedContext);
        return result.TryGetValue(out var state) ? state : throw new ObservedFinanceTransitionException(result.Error);
    }
}

public static class ObservedFinanceTimelineVersions
{
    public const int CheckpointInterval = 32;

    public static TimelineVersionKey V4 { get; } = new(FinanceLedgerVersion.DomainId, ObservedFinanceVersion.ReducerVersion,
        ObservedFinanceVersion.InputSchemaVersion, ObservedFinanceVersion.StateSchemaVersion, ObservedFinanceVersion.ContextSchemaVersion);

    public static TimelineComponents<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext> V4Components { get; } = new(
        V4, ObservedFinanceTransition.Instance, ObservedFinanceStateCodec.Instance, ObservedFinanceInputCodec.Instance, ObservedFinanceContextCodec.Instance);

    public static IReadOnlyList<TimelineComponents<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>> All { get; } = [V4Components];
}
