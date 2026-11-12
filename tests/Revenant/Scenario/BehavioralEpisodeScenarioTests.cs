using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Scenario;

public sealed class BehavioralEpisodeScenarioTests
{
    internal static readonly long Origin = new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    internal const long Duration = 24 * ObservedFinanceVersion.BucketMs;
    internal static readonly ScenarioRunId RunId = new(Guid.Parse("00000000-0000-0000-0000-000000040001"));

    internal static ScenarioRun Opening(ScenarioRunId id, long duration = Duration) =>
        BehavioralEpisodeScenario.Create(id, 411, Origin, duration, [2, 3, 4, 2, 3, 4]);

    internal static ObservedFinanceContext Pin(ScenarioRun run) =>
        new(run.Manifest.ScenarioVersion, run.Manifest.ContentHash, run.Manifest.StartUtcMs,
            PolicyCatalog.RefOf(PolicyCatalog.V7), ReviewPolicy.Version, ObservedRiskMode.NotRequired, "", null, [], []);

    [Fact]
    public void Canonical_opening_is_run_independent_and_pins_duration_and_population()
    {
        var a = Opening(RunId);
        var b = Opening(new(Guid.Parse("00000000-0000-0000-0000-000000040002")));
        Assert.Equal(BehavioralEpisodeScenario.EncodeManifest(a.Manifest), BehavioralEpisodeScenario.EncodeManifest(b.Manifest));
        Assert.Equal(a.Manifest.ContentHash, b.Manifest.ContentHash);
        Assert.Equal(a.Manifest.ContentHash, ScenarioManifestCodec.Sha256Hex(BehavioralEpisodeScenario.EncodeManifest(a.Manifest)));
        Assert.NotEqual(a.Manifest.ContentHash, Opening(RunId, 2 * Duration).Manifest.ContentHash);
        Assert.NotEqual(a.Manifest.ContentHash,
            BehavioralEpisodeScenario.Create(RunId, 411, Origin, Duration, [2, 3, 4, 2, 3, 5]).Manifest.ContentHash);
        Assert.Equal(Enumerable.Range(0, 6).Select(FridayRingScenario.BankId), a.Manifest.Banks.Select(x => x.BankId));
        Assert.Equal(new[] { 2, 3, 4, 2, 3, 4 }, a.Manifest.Banks.Select(bank => a.Manifest.Accounts.Count(x => x.BankId == bank.BankId)));

        var codec = ObservedFinanceContextCodec.Instance;
        var context = codec.Decode(codec.EncodeCanonical(Pin(a)));
        Assert.Equal(a.Manifest.ContentHash, context.ManifestSha256);
        var opening = ObservedFinanceReducer.Initial(a, context).Value;
        Assert.Equal(a.Manifest.Accounts.Sum(account => account.OpeningBalance.MinorUnits), opening.Ledger.TotalPostedMinor);
        Assert.All(opening.Ledger.Accounts.Values, account => Assert.Equal(0, account.ReservedMinor));
        var stateCodec = ObservedFinanceStateCodec.Instance;
        var bytes = stateCodec.EncodeCanonical(opening);
        Assert.Equal(bytes, stateCodec.EncodeCanonical(stateCodec.Decode(bytes)));
        Assert.True(LedgerFinanceContext.Pin(a.Manifest, context.BasePolicy).IsFailure);
        Assert.True(LedgerFinanceContext.FromPins(context.ScenarioVersion, context.ManifestSha256,
            FinanceLedgerVersion.ReducerVersion, context.BasePolicy, context.ReviewPolicy).IsFailure);
    }

    [Fact]
    public void Opening_rejects_changed_duration_origin_or_unpinned_account_state()
    {
        var run = Opening(RunId);
        var context = Pin(run);
        Assert.True(ObservedFinanceReducer.Initial(Opening(RunId, 2 * Duration), context).IsFailure);
        Assert.True(ObservedFinanceReducer.Initial(run with
        {
            Manifest = run.Manifest with { V4EpisodeDurationMs = Duration * 2 },
        }, context).IsFailure);
        Assert.True(ObservedFinanceReducer.Initial(run with
        {
            InitialState = run.InitialState with { Accounts = run.InitialState.Accounts.RemoveAt(0) },
        }, context).IsFailure);
        var wrongOrigin = new ObservedFinanceContext(context.ScenarioVersion, context.ManifestSha256,
            Origin + ObservedFinanceVersion.BucketMs, context.BasePolicy, context.ReviewPolicy, context.RiskMode, "", null, [], []);
        Assert.True(ObservedFinanceReducer.Initial(run, wrongOrigin).IsFailure);
    }
}
