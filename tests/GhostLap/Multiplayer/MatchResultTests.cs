using System.Collections.Immutable;
using FsCheck;
using FsCheck.Fluent;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using Xunit;
using static GhostLap.Tests.Multiplayer.MatchTestKit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// Task 10.4: the durable result document (placement, eligibility, sealed rating algorithm id, stats, canonical digest). Final states come either from
/// the production engine running all 2700 ticks, or are built directly from the pinned <see cref="PlayerState"/> record when a test needs exact scores.
/// </summary>
public sealed class MatchResultTests
{
    /// <summary>A resolved final world in which slot i scored <c>scores[i]</c> (as pickups x 25) and slot i is forfeited when <c>forfeitAt[i] &gt; 0</c>.</summary>
    private static MpMatchState Final(int[] scores, long[] forfeitAt)
    {
        var context = MpMatchContext.Create(42, scores.Length);
        var world = MpMatchState.Initial(context).World;
        var players = world.Players.Select((p, i) => p with { Pickups = scores[i] / GameRulesV1.CandlePickupPoints }).ToImmutableArray();
        return new MpMatchState(world with { Tick = GameRulesV1.RoundTicks, Players = players }, [.. forfeitAt]);
    }

    private static MatchResultDocument Build(int[] scores, long[] forfeitAt, SealedRoundConfig? config = null) =>
        MatchResults.Build(config ?? Seal(scores.Length), Final(scores, forfeitAt), new MpMatchStats(scores.Length)).Document;

    private static int[] Placements(MatchResultDocument d) => [.. d.Players.Select(p => p.CompetitivePlacement)];

    /// <summary>**Validates: Requirements 10.13** Equal finisher totals share a placement and the next placement skips; slot order never breaks a tie.</summary>
    [Fact]
    public void Equal_totals_share_a_placement_and_slot_order_is_not_a_tiebreak()
    {
        Assert.Equal([1, 1, 3], Placements(Build([100, 100, 50], [0, 0, 0])));
        Assert.Equal([3, 1, 1, 3], Placements(Build([25, 75, 75, 25], [0, 0, 0, 0])));
        Assert.Equal([1, 1], Placements(Build([0, 0], [0, 0])));
    }

    /// <summary>**Validates: Requirements 10.13** Every finisher ranks above every forfeit, even one with a higher frozen score; forfeits rank by frozen score and can tie.</summary>
    [Fact]
    public void Finishers_rank_above_forfeits_whatever_the_forfeit_scored()
    {
        var d = Build([25, 500, 50, 500], [0, 900, 0, 1200]);
        Assert.Equal([2, 3, 1, 3], Placements(d));
        Assert.Equal(
            [MatchResultContract.OutcomeFinisher, MatchResultContract.OutcomeForfeit, MatchResultContract.OutcomeFinisher, MatchResultContract.OutcomeForfeit],
            d.Players.Select(p => p.Outcome));
        Assert.Equal([0L, 900L, 0L, 1200L], d.Players.Select(p => p.ForfeitTick));
        Assert.Equal([1, 2], Placements(Build([0, 75], [0, 600])));
    }

    /// <summary>**Validates: Requirements 10.13** mp-eligibility-v1: no finisher is unrated; one finisher with two distinct guest principals is rated; a non-guest roster is unrated.</summary>
    [Fact]
    public void Eligibility_follows_mp_eligibility_v1()
    {
        var allForfeit = Build([50, 75, 0], [500, 500, 700]);
        Assert.Equal((false, MatchResultContract.ReasonNoFinisher), (allForfeit.RatedEligible, allForfeit.EligibilityReason));
        Assert.Equal([2, 1, 3], Placements(allForfeit));

        var oneFinisher = Build([0, 300], [0, 451]);
        Assert.Equal((true, MatchResultContract.ReasonEligible), (oneFinisher.RatedEligible, oneFinisher.EligibilityReason));
        Assert.Equal([1, 2], Placements(oneFinisher));

        var key = Enumerable.Repeat((byte)9, 32).ToArray();
        var staff = RoundSealing.Seal(key, Guid.NewGuid(), 1, [new("a", "guest:1"), new("b", "staff:2")], new MultiplayerOptions(), Generation);
        var mixed = MatchResults.Build(staff, Final([10, 20], [0, 0]), new MpMatchStats(2)).Document;
        Assert.Equal((false, MatchResultContract.ReasonTooFewGuestPrincipals), (mixed.RatedEligible, mixed.EligibilityReason));
        Assert.Equal(MatchResultContract.EligibilityPolicy, mixed.EligibilityPolicy);
    }

    /// <summary>**Validates: Requirements 10.13, 10.14** The rating algorithm id is the one sealed at countdown; the builder never consults a default.</summary>
    [Fact]
    public void Rating_algorithm_id_is_carried_from_the_sealed_config()
    {
        var sealedV7 = Seal(2, options: new MultiplayerOptions { RatingAlgorithmId = "elo-pair-v7" });
        var d = Build([0, 25], [0, 0], sealedV7);
        Assert.Equal("elo-pair-v7", d.RatingAlgorithmId);
        Assert.NotEqual(MpRulesProfile.InitialRatingAlgorithmId, d.RatingAlgorithmId);
        Assert.Equal((sealedV7.ConfigDigest, sealedV7.MatchId.ToString("N"), sealedV7.Seed.ToString()), (d.ConfigDigest, d.MatchId, d.Seed));
    }

    /// <summary>**Validates: Requirements 10.13** A result exists only for the resolved final tick.</summary>
    [Fact]
    public void A_result_cannot_be_built_before_the_final_tick()
    {
        var config = Seal(2);
        var early = MpMatchState.Initial(MpMatchContext.From(config));
        Assert.Throws<ArgumentException>(() => MatchResults.Build(config, early, new MpMatchStats(2)));
    }

    /// <summary>
    /// **Validates: Requirements 10.13, 10.14** A full 2700-tick match through the production engine with real disconnects: slots disconnected from
    /// the start forfeit at tick 451; the result's stats equal the event stream; equal inputs give an equal digest; the JSON round trip keeps it.
    /// </summary>
    [Fact]
    public void Full_engine_matches_give_deterministic_results_with_exact_stats()
    {
        (MatchResultRecord Record, int BurstHits, int Contacts) Run()
        {
            var engine = Engine(4);
            engine.SetController(new SlotControl(2, 1, null)); // slot 2 never controls: grace from tick 1, forfeit at 451
            var hits = new int[4];
            var contacts = new int[4];
            var seq = new int[4];
            while (!engine.State.IsFinal)
            {
                var tick = engine.State.Tick;
                for (var slot = 0; slot < 4; slot++)
                {
                    if (slot == 2)
                    {
                        continue;
                    }
                    // Slot 3 drops out at tick 1000 (forfeit at 1451); the others weave toward the centre and press Burst now and then.
                    if (slot == 3 && tick == 1000)
                    {
                        engine.SetController(new SlotControl(3, 1, null));
                    }
                    var dx = (int)((tick / 90 + slot) % 3) - 1;
                    var dy = (int)((tick / 70 + slot * 2) % 3) - 1;
                    if (tick % 3 == 0) // the unit budget never refills on a frozen clock: stay under its 1000-item burst
                    {
                        Send(engine, slot, ++seq[slot], dx, dy, burst: tick % 120 < 6);
                    }
                }
                var outcome = engine.Step();
                foreach (var e in engine.State.World.Events)
                {
                    if (e.Kind == GameEventKind.BurstHit)
                    {
                        hits[e.Slot]++;
                    }
                    if (e.Kind == GameEventKind.ContactDrain)
                    {
                        contacts[e.Slot]++;
                    }
                }
                Assert.Equal(tick + 1, outcome.Tick);
            }
            var record = MatchResults.Build(engine.Config, engine.State, engine.Stats);
            for (var slot = 0; slot < 4; slot++)
            {
                Assert.Equal(hits[slot], record.Document.Players[slot].TimesHitByBurst);
                Assert.Equal(contacts[slot], record.Document.Players[slot].ContactDrainsSuffered);
                Assert.Equal(engine.State.World.Players[slot].TotalScore, record.Document.Players[slot].TotalScore);
            }
            return (record, hits.Sum(), contacts.Sum());
        }

        var first = Run();
        var second = Run();
        var d = first.Record.Document;
        Assert.Equal(first.Record.Digest, second.Record.Digest);
        Assert.Equal([0L, 0L, 451L, 1451L], d.Players.Select(p => p.ForfeitTick));
        Assert.True(d.Players[0].CompetitivePlacement <= 2 && d.Players[1].CompetitivePlacement <= 2);
        Assert.True(d.Players[2].CompetitivePlacement >= 3 && d.Players[3].CompetitivePlacement >= 3);
        Assert.True(d.RatedEligible);
        Assert.Equal(2700, d.FinalTick);

        var payload = MatchResults.PayloadFromJson(first.Record.PayloadJson());
        Assert.Equal(first.Record.Digest, payload.ResultDigest);
        Assert.Equal(first.Record.Digest, MatchResults.Digest(payload.Result));
        Assert.Equal(first.Record.Digest, MatchResults.Digest(MatchResults.DocumentFromJson(MatchResults.DocumentToJson(d))));
        Console.WriteLine($"RESULT-DIGEST {first.Record.Digest} placements={string.Join(",", Placements(d))} "
                          + $"scores={string.Join(",", d.Players.Select(p => p.TotalScore))} burstHits={first.BurstHits} contacts={first.Contacts}");
    }

    /// <summary>**Validates: Requirements 10.14** Any change to a gameplay-significant field changes the digest; the event id is fixed by the match id.</summary>
    [Fact]
    public void The_digest_covers_every_field_and_the_event_id_depends_only_on_the_match()
    {
        var d = Build([25, 50], [0, 0]);
        var digest = MatchResults.Digest(d);
        var p = d.Players[1];
        MatchResultDocument[] variants =
        [
            d with { RatingAlgorithmId = "elo-pair-v2" },
            d with { RatedEligible = false, EligibilityReason = MatchResultContract.ReasonNoFinisher },
            d with { FinalStateHash = new string('0', 64) },
            d with { Seed = "1" },
            d with { Players = [d.Players[0], p with { TotalScore = p.TotalScore + 1 }] },
            d with { Players = [d.Players[0], p with { CompetitivePlacement = p.CompetitivePlacement + 1 }] },
            d with { Players = [d.Players[0], p with { Subject = "guest:other" }] },
            d with { Players = [d.Players[0], p with { ChargeLostToContact = 1 }] },
        ];
        Assert.All(variants, v => Assert.NotEqual(digest, MatchResults.Digest(v)));
        var match = Guid.Parse(d.MatchId);
        Assert.Equal(MatchResults.FinalizedEventId(match), MatchResults.FinalizedEventId(match));
        Assert.NotEqual(MatchResults.FinalizedEventId(match), MatchResults.FinalizedEventId(Guid.NewGuid()));
        Assert.Equal(8, MatchResults.FinalizedEventId(match).Version);
    }

    /// <summary>
    /// **Validates: Requirements 10.13** Property (mp-placement-v1) over random 2/3/4-player outcomes: placement = 1 + players strictly ahead; every
    /// finisher above every forfeit; equal (class, score) share a placement; relabelling slots does not change anyone's placement.
    /// </summary>
    [Fact]
    public void Placement_property_holds_for_random_outcomes()
    {
        var gen =
            from n in Gen.Choose(2, 4)
            from scores in Gen.ArrayOf(Gen.Choose(0, 6).Select(k => k * 25), n)
            from forfeits in Gen.ArrayOf(Gen.Choose(-1800, 2700).Select(t => t < 451 ? 0L : t), n)
            from shift in Gen.Choose(0, n - 1)
            select (scores, forfeits, shift);
        Prop.ForAll(gen.ToArbitrary(), c =>
        {
            var (scores, forfeits, shift) = c;
            var n = scores.Length;
            var placements = Placements(Build(scores, forfeits));
            for (var i = 0; i < n; i++)
            {
                var ahead = Enumerable.Range(0, n).Count(j => j != i
                    && ((forfeits[j] == 0 && forfeits[i] != 0) || ((forfeits[j] == 0) == (forfeits[i] == 0) && scores[j] > scores[i])));
                if (placements[i] != 1 + ahead)
                {
                    return false;
                }
                for (var j = 0; j < n; j++)
                {
                    if (forfeits[i] == 0 && forfeits[j] != 0 && placements[i] >= placements[j])
                    {
                        return false;
                    }
                    if ((forfeits[i] == 0) == (forfeits[j] == 0) && scores[i] == scores[j] && placements[i] != placements[j])
                    {
                        return false;
                    }
                }
            }
            var rotated = Placements(Build(
                [.. Enumerable.Range(0, n).Select(i => scores[(i + shift) % n])], [.. Enumerable.Range(0, n).Select(i => forfeits[(i + shift) % n])]));
            return Enumerable.Range(0, n).All(i => rotated[i] == placements[(i + shift) % n]);
        }).QuickCheckThrowOnFailure();
    }
}
