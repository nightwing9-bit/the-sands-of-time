using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using Marrow.Kernel.Canonical;
using Revenant.Contracts;
using Revenant.Observations;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Synthetic;

internal enum LongitudinalSplit : byte { Pilot = 1, Train = 2, Validation = 3, Calibration = 4, FinalTest = 5, Challenge = 6 }
internal enum LongitudinalEventKind : byte { VelocityAnomaly = 1, FraudAssociatedVelocityAnomaly = 2, LegitimateBurst = 3, DevelopingPressure = 4, SuddenShock = 5 }

/// <summary>
/// Statistical outgoing-velocity truth (truth schema 2). It is a property of the observed counts relative to the account's own
/// ordinary behavior, independent of business intent: Anomalous when the observed window is improbable under the ordinary process,
/// Normal when it is typical, Ambiguous in between. Ambiguous windows are neither hits nor false alerts.
/// </summary>
internal enum VelocityTruth : byte { Anomalous = 1, Ambiguous = 2, Normal = 3 }

/// <summary>Business intent behind injected activity, kept separate from statistical truth.</summary>
internal enum EventIntent : byte { Legitimate = 1, FraudAssociated = 2 }

internal enum VelocityMechanism : byte
{
    OffSchedule = 1, AmplitudeSurge = 2, SustainedElevation = 3, DormantBurst = 4, RecurringBatch = 5,

    /// <summary>Outgoing installments of a developing-pressure drain or its recovery transfers.</summary>
    TreasuryDrain = 6,
}

internal sealed record LongitudinalRoot(LongitudinalSplit Split, int Index, string Key, long Seed, int Days);

/// <summary>Hidden evaluation annotation. Truth-schema-1 events leave the optional schema-2 fields null.</summary>
internal sealed record LongitudinalEvent(string Id, string Subject, LongitudinalEventKind Kind, int Start, int End,
    VelocityTruth? Truth = null, EventIntent? Intent = null, VelocityMechanism? Mechanism = null, long? ObservedCount = null,
    double? OrdinaryMean = null, double? TailProbability = null);
internal sealed record V4LongitudinalEpisode(LongitudinalRoot Root, V4EpisodePlan Financial, ImmutableArray<LongitudinalEvent> Events,
    IReadOnlyDictionary<string, long> ReviewTimes, string BehaviorSha256, IReadOnlyDictionary<string, string>? Rhythms = null,
    string Generator = BehavioralEpisodeScenario.GeneratorVersionV3, string TruthSchema = V4LongitudinalPlanner.TruthSchema);

internal static class V4LongitudinalPlanner
{
    internal const string Version = BehavioralEpisodeScenario.GeneratorVersionV3;
    internal const string TruthSchema = "revenant-longitudinal-truth/1";
    internal const int BucketsPerDay = 288;
    private const long Bucket = ObservedFinanceVersion.BucketMs;

    internal static IEnumerable<LongitudinalRoot> Roots(LongitudinalSplit split, int days = 7)
    {
        if (days is not (7 or 14)) throw new ArgumentOutOfRangeException(nameof(days));
        var count = split switch
        {
            LongitudinalSplit.Pilot => 2,
            LongitudinalSplit.Train => 6,
            LongitudinalSplit.Validation or LongitudinalSplit.Calibration => 3,
            LongitudinalSplit.FinalTest or LongitudinalSplit.Challenge => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(split)),
        };
        for (var i = 0; i < count; i++)
        {
            var hash = SHA256.HashData(ObservationEncoding.Begin(Version).WriteString("root")
                .WriteByte((byte)split).WriteInt32(days).WriteInt32(i).ToArray());
            yield return new(split, i, Convert.ToHexStringLower(hash), BinaryPrimitives.ReadInt64BigEndian(hash), days);
        }
    }

    private sealed record Instruction(int BucketIndex, int Sender, int Receiver, long Amount, bool Return, int ReviewDelay);

    internal static V4LongitudinalEpisode Plan(LongitudinalRoot root, ScenarioRunId execution)
    {
        var calendar = SeededRandom.ForStream(root.Seed, 1);
        var activity = SeededRandom.ForStream(root.Seed, 2);
        var stress = SeededRandom.ForStream(root.Seed, 3);
        var liquidity = SeededRandom.ForStream(root.Seed, 4);
        var shifted = root.Split == LongitudinalSplit.Challenge;
        var duration = root.Days * BucketsPerDay;
        var origin = 1_772_323_200_000L + calendar.NextInt(0, 84 * BucketsPerDay) * Bucket;
        var run = BehavioralEpisodeScenario.Create(execution, root.Seed, origin, duration * Bucket, [2, 2, 2, 2, 2, 2], Version);
        var accounts = run.Manifest.Accounts;
        var instructions = new List<Instruction>();
        var events = new List<LongitudinalEvent>();
        var order = Enumerable.Range(0, accounts.Length).ToArray();
        activity.Shuffle(order);

        // Offset short stretches of recurring batches across days, accounts and roots.
        var profiles = new[] { 28, 26, 22, 20, 14, 11, 8, 5, 3, 2, 1, 1 };
        for (var day = 0; day < root.Days; day++)
        {
            var dayStart = day * BucketsPerDay;
            for (var profile = 0; profile < order.Length; profile++)
            {
                var sender = order[profile];
                var receiver = OtherBank(sender, activity.NextInt(1, 6));
                var phase = activity.NextInt(12, 238);
                var step = profile < 4 ? activity.NextInt(1, 3) : activity.NextInt(2, 5);
                for (var slot = 0; slot < profiles[profile]; slot++)
                {
                    if (activity.NextInt(0, 12) == 0) continue;
                    var at = dayStart + phase + slot * step;
                    if (at >= dayStart + BucketsPerDay - 1) break;
                    var count = slot % (shifted ? 5 : 4) == 0 ? activity.NextInt(2, 4) : 1;
                    for (var j = 0; j < count; j++)
                    {
                        // Reverse ordinary counterparties on successive cycles to keep normal transfers funded.
                        var to = j % 2 == 0 ? receiver : OtherBank(sender, activity.NextInt(1, 6));
                        Add(at, sender, to, activity.NextInt(150, 2_401), requestReturn: activity.NextInt(0, 45) == 0);
                    }
                }
            }

            var first = dayStart + activity.NextInt(18, 80);
            var second = dayStart + activity.NextInt(140, 235);
            var useSecondForAnomaly = activity.NextInt(0, 2) == 0;
            Burst(useSecondForAnomaly ? second : first, order[activity.NextInt(0, 7)],
                day % 3 == 0 ? LongitudinalEventKind.FraudAssociatedVelocityAnomaly : LongitudinalEventKind.VelocityAnomaly);
            Burst(useSecondForAnomaly ? first : second, order[activity.NextInt(0, 9)], LongitudinalEventKind.LegitimateBurst);

            // Commerce and treasury transfers overlap. Offset a short repeating interbank cycle across roots.
            var route = Enumerable.Range(0, 6).ToArray();
            liquidity.Shuffle(route);
            var period = shifted ? liquidity.NextInt(7, 11) : liquidity.NextInt(6, 9);
            var phaseOffset = liquidity.NextInt(0, period);
            var session = dayStart + liquidity.NextInt(28, 165);
            for (var interval = 0; interval < 72; interval += 2)
            {
                var phase = (interval + phaseOffset) % period;
                var pair = interval / (period * 2) % 3;
                var a = 2 * route[2 * pair];
                var b = 2 * route[2 * pair + 1];
                var direction = phase < period / 2;
                Add(session + interval, direction ? a : b, direction ? b : a, liquidity.NextInt(4_000, 33_001));
            }

            var reviewAt = dayStart + activity.NextInt(20, 245);
            var reviewSender = order[activity.NextInt(0, order.Length)];
            Add(reviewAt, reviewSender, OtherBank(reviewSender, 1), activity.NextInt(14_000, 57_001),
                reviewDelay: activity.NextInt(2, 9));
        }

        var stressed = Enumerable.Range(0, 6).ToArray();
        stress.Shuffle(stressed);
        for (var i = 0; i < 3; i++)
        {
            var bank = stressed[i];
            var recipient = stressed[i + 3];
            var day = 1 + i * (root.Days - 2) / 3;
            var start = day * BucketsPerDay + stress.NextInt(65, 215);
            var sudden = i == 2;
            var installments = sudden ? 1 : stress.NextInt(11, 16);
            var gap = sudden ? 1 : stress.NextInt(1, shifted ? 3 : 2);
            var recover = start + installments * gap + stress.NextInt(9, 20);
            var basisPoints = stress.NextInt(9_250, 9_601);
            for (var account = 0; account < 2; account++)
            {
                var from = 2 * bank + account;
                var to = 2 * recipient + account;
                var principal = accounts[from].OpeningBalance.MinorUnits * basisPoints / 10_000;
                for (var step = 0; step < installments; step++)
                {
                    var amount = principal * (step + 1) / installments - principal * step / installments;
                    Add(start + step * gap, from, to, amount);
                    Add(recover + step, to, from, amount);
                }
            }
            Annotate(accounts[bank * 2].BankId, sudden ? LongitudinalEventKind.SuddenShock : LongitudinalEventKind.DevelopingPressure,
                start, recover + installments - 1);
        }

        var ordered = instructions.OrderBy(x => x.BucketIndex).ToArray();
        var payments = ImmutableArray.CreateBuilder<V4PlannedPayment>(ordered.Length);
        var reviews = new Dictionary<string, long>(StringComparer.Ordinal);
        var seen = new HashSet<(int, int)>();
        var canonical = ObservationEncoding.Begin(Version).WriteInt64(origin).WriteCount(ordered.Length);
        var previous = -1;
        var slotOffset = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            var (bucket, sender, receiver, amount, returns, reviewDelay) = ordered[i];
            if (bucket != previous) slotOffset = activity.NextInt(250, 3_000);
            previous = bucket;
            var at = origin + bucket * Bucket + slotOffset;
            slotOffset += 1_500;
            if (slotOffset >= Bucket) throw new InvalidOperationException("Too many instructions in the synthetic interval.");
            var source = accounts[sender];
            var dest = accounts[receiver];
            var id = string.Create(CultureInfo.InvariantCulture, $"P-{i + 1:D8}");
            var intent = new PaymentIntent(execution, id, i + 1, at, source.BankId, source.AccountId, dest.BankId, dest.AccountId,
                Money.Usd(amount), SyntheticTruth.Legitimate, new(seen.Add((sender, receiver)), reviewDelay > 0));
            payments.Add(new(intent, returns));
            if (reviewDelay > 0) reviews.Add(id, origin + (bucket + reviewDelay) * Bucket + 125);
            canonical.WriteBytes(PaymentIntentCodec.EncodeFacts(intent)).WriteBoolean(returns).WriteInt32(reviewDelay);
        }

        return new(root, new(run, origin + duration * Bucket, payments.MoveToImmutable()),
            [.. events.OrderBy(e => e.Start).ThenBy(e => e.Id, StringComparer.Ordinal)], reviews, ObservationEncoding.Hash(canonical.ToArray()));

        void Add(int at, int from, int to, long amount, bool requestReturn = false, int reviewDelay = 0)
        {
            if (at < 0 || at + reviewDelay >= duration || from == to)
                throw new InvalidOperationException("Invalid longitudinal payment schedule.");
            instructions.Add(new(at, from, to, amount, requestReturn, reviewDelay));
        }

        void Burst(int at, int from, LongitudinalEventKind kind)
        {
            var length = activity.NextInt(1, 4);
            var intensity = activity.NextInt(3, 7);
            for (var k = 0; k < length; k++)
                for (var j = 0; j < intensity; j++)
                    Add(at + k, from, OtherBank(from, activity.NextInt(1, 6)), activity.NextInt(230, 3_200));
            Annotate(accounts[from].AccountId, kind, at, at + length - 1);
        }

        void Annotate(string subject, LongitudinalEventKind kind, int start, int end) =>
            events.Add(new($"{root.Key}/{events.Count + 1}", subject, kind, start, end));
    }

    private static int OtherBank(int index, int offset) => ((index / 2 + offset) % 6) * 2 + index % 2;
}
