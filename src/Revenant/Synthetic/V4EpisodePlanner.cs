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

internal enum V4DatasetSplit : byte { Train = 1, Validation = 2, Calibration = 3, FinalTest = 4 }

internal sealed record V4EpisodeConfig(long MasterSeed, long OriginMs, int Hours,
    int Train, int Validation, int Calibration, int FinalTest)
{
    internal const string Assignment = "independent-root-split/1";
    internal const long HourMs = 3_600_000;
    internal long Duration => checked(Hours * HourMs);

    internal static readonly V4EpisodeConfig Qualification = new(937_041_261, 1_768_176_000_000, 72, 12, 4, 4, 4);
    internal static readonly V4EpisodeConfig Pilot = Qualification with { Hours = 48, Train = 1, Validation = 1, Calibration = 0, FinalTest = 0 };
    internal static readonly V4EpisodeConfig Smoke = Qualification with { Hours = 2, Train = 1, Validation = 0, Calibration = 0, FinalTest = 0 };

    internal IEnumerable<V4RootGroup> Roots()
    {
        var counts = new[] { Train, Validation, Calibration, FinalTest };
        if (Hours is < 2 or > 720 || counts.Any(n => n is < 0 or > 1_000) || counts.Sum() == 0
            || OriginMs < 0 || OriginMs > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() - Duration
            || !ObservedFinanceVersion.IsBoundary(OriginMs))
        {
            throw new ArgumentException("Invalid episode configuration.");
        }
        for (var i = 0; i < counts.Length; i++)
        {
            for (var j = 0; j < counts[i]; j++)
            {
                var split = (V4DatasetSplit)(i + 1);
                var bytes = new CanonicalWriter().WriteString(Assignment).WriteInt64(MasterSeed).WriteByte((byte)split).WriteInt32(j).ToArray();
                var digest = SHA256.HashData(bytes);
                yield return new(split, j, Convert.ToHexStringLower(digest), BinaryPrimitives.ReadInt64BigEndian(digest));
            }
        }
    }
}

internal sealed record V4RootGroup(V4DatasetSplit Split, int Ordinal, string Key, long Seed);
internal sealed record V4PlannedPayment(PaymentIntent Intent, bool RequestReturn);
internal sealed record V4EpisodePlan(ScenarioRun Opening, long EndMs, ImmutableArray<V4PlannedPayment> Payments);
internal sealed record V4EpisodeAnnotation(ImmutableArray<V4PaymentTruth> Payments, ImmutableArray<V4AnomalyTruth> Anomalies);
internal sealed record V4EpisodeDesign(V4RootGroup Group, V4EpisodePlan Financial, V4EpisodeAnnotation Truth,
    string HistoryFingerprint, ImmutableArray<string> CampaignFingerprints);

internal static class V4EpisodePlanner
{
    private sealed record Draft(long At, int From, int To, long Amount, bool Return, bool HardNegative,
        EpisodeAttack? Attack = null, EpisodeAnomaly Anomaly = EpisodeAnomaly.None);

    internal static V4EpisodeDesign Plan(V4EpisodeConfig config, V4RootGroup group, ScenarioRunId physicalRun)
    {
        var population = Stream(group, "population");
        var schedule = Stream(group, "ordinary-schedules");
        var behavior = Stream(group, "account-behavior");
        var campaigns = Stream(group, "adversarial-campaigns");
        var counterparty = Stream(group, "counterparty-selection");
        var amounts = Stream(group, "amount-variation");
        var time = Stream(group, "temporal-variation");
        var episodeIdentity = Stream(group, "episode-identities");
        var run = BehavioralEpisodeScenario.Create(physicalRun, group.Seed, config.OriginMs, config.Duration,
            [.. Enumerable.Range(0, 6).Select(_ => population.NextInt(3, 7))]);
        var accounts = run.Manifest.Accounts;
        var byBank = accounts.Select((a, i) => (a.BankId, i)).GroupBy(x => x.BankId)
            .Select(g => g.Select(x => x.i).ToArray()).ToArray();
        var commercial = behavior.NextInt(0, accounts.Length);
        var household = Other(commercial);
        var takeover = Other(household, commercial);
        var payroll = byBank.Select(b => b[behavior.NextInt(0, b.Length)]).ToArray();
        var supplier = Other(commercial);
        var utility = Other(household);
        var spare = Other(takeover);
        var dayCount = (config.Hours + 23) / 24;
        var drafts = new List<Draft>();

        for (var day = 0; day < dayCount; day++)
        {
            for (var i = 0; i < payroll.Length; i++)
            {
                var sender = payroll[i];
                var recipient = payroll[(i + 1) % payroll.Length];
                Add(Slot(day, 190 + 13 * i), sender, recipient, amounts.NextInt(45_000, 135_000));
            }
            Add(Slot(day, 320), household, utility, amounts.NextInt(3_000, 12_000));
            Add(Slot(day, 340), takeover, spare, amounts.NextInt(2_000, 11_000));
            var ordinary = schedule.NextInt(5, 9);
            for (var i = 0; i < ordinary; i++)
            {
                var from = schedule.NextInt(0, accounts.Length);
                Add(Slot(day, 430 + 38 * i), from, Other(from), amounts.NextInt(1_000, 90_000), returnRequested: i == 0);
            }

            // Recurring vendor batches coexist with high-value one-off lookalikes.
            var peak = Slot(day, 725 + schedule.NextInt(-48, 49));
            var burstSize = schedule.NextInt(7, 10);
            for (var i = 0; i < burstSize; i++)
            {
                Add(peak + i * 13_000, commercial, i % 3 == 0 ? supplier : Other(commercial),
                    amounts.NextInt(55_000, 180_000), hard: true, anomaly: EpisodeAnomaly.OperationalBurst);
            }
            var infrequent = Other(commercial, household);
            Add(Slot(day, 866), infrequent, Other(infrequent), amounts.NextInt(125_000, day == 0 ? 420_000 : 690_000), hard: true);
        }

        // One independently parameterized campaign of each family. Anchors and participants vary by root.
        var campaignDay = dayCount - 1;
        var ring = byBank.Select(bank => bank[campaigns.NextInt(0, bank.Length)]).ToArray();
        campaigns.Shuffle(ring);
        var ringAt = Slot(campaignDay, 390 + campaigns.NextInt(-85, 86));
        for (var i = 0; i < ring.Length; i++)
        {
            Add(ringAt + 64_000L * i, ring[i], ring[(i + 1) % ring.Length], amounts.NextInt(15_000, 160_000),
                attack: EpisodeAttack.Ring, anomaly: EpisodeAnomaly.CoordinatedActivity);
        }

        var takeoverAt = Slot(campaignDay, 615 + campaigns.NextInt(-22, 23));
        var novel = Other(takeover, spare);
        Add(takeoverAt, takeover, novel, amounts.NextInt(135_000, 365_000),
            attack: EpisodeAttack.Takeover, anomaly: EpisodeAnomaly.BehaviorDeviation);
        Add(takeoverAt + 42_000, takeover, Other(takeover, novel), amounts.NextInt(21_000, 140_000),
            attack: EpisodeAttack.Takeover, anomaly: EpisodeAnomaly.BehaviorDeviation);

        var disperser = campaigns.NextInt(0, accounts.Length);
        var dispersalSize = campaigns.NextInt(4, 7);
        for (var i = 0; i < dispersalSize; i++)
        {
            Add(Slot(campaignDay, 240 + 110 * i), disperser, Other(disperser), amounts.NextInt(1_500, 14_000),
                attack: EpisodeAttack.Dispersal);
        }

        var coordinated = byBank.Select(bank => bank[campaigns.NextInt(0, bank.Length)]).ToArray();
        var burstAt = Slot(campaignDay, 770 + campaigns.NextInt(-48, 49));
        var hub = campaigns.NextInt(0, accounts.Length);
        for (var i = 0; i < coordinated.Length; i++)
        {
            var from = coordinated[i];
            Add(burstAt + i * 12_000, from, from == hub ? Other(from) : hub, amounts.NextInt(32_000, 155_000),
                attack: EpisodeAttack.CoordinatedBurst, anomaly: EpisodeAnomaly.CoordinatedActivity);
        }

        var abuseFrom = household;
        var abuseTo = Other(abuseFrom, utility);
        var abuseAt = Slot(campaignDay, 845 + campaigns.NextInt(-48, 49));
        Add(abuseAt, abuseFrom, abuseTo, amounts.NextInt(11_000, 108_000), attack: EpisodeAttack.BeneficiaryAbuse);
        Add(abuseAt + 41_000, abuseFrom, utility, amounts.NextInt(6_000, 95_000), attack: EpisodeAttack.BeneficiaryAbuse);
        Add(abuseAt + 84_000, abuseFrom, abuseTo, amounts.NextInt(15_000, 185_000), attack: EpisodeAttack.BeneficiaryAbuse);

        var ordered = drafts.OrderBy(d => d.At).ToArray();
        var financial = ImmutableArray.CreateBuilder<V4PlannedPayment>(ordered.Length);
        var paymentTruth = ImmutableArray.CreateBuilder<V4PaymentTruth>(ordered.Length);
        var anomalyTruth = new Dictionary<(string Account, long Start), V4AnomalyTruth>();
        var seen = new HashSet<(int, int)>();
        var campaignKeys = Enum.GetValues<EpisodeAttack>().ToDictionary(a => a, a =>
            group.Key + "/" + episodeIdentity.NextUInt64().ToString("x16", CultureInfo.InvariantCulture));
        for (var i = 0; i < ordered.Length; i++)
        {
            var d = ordered[i];
            var src = accounts[d.From];
            var dest = accounts[d.To];
            var id = string.Create(CultureInfo.InvariantCulture, $"P-{i + 1:D8}");
            var intent = new PaymentIntent(physicalRun, id, i + 1, d.At, src.BankId, src.AccountId, dest.BankId,
                dest.AccountId, Money.Usd(d.Amount), SyntheticTruth.Legitimate, new(seen.Add((d.From, d.To)), false));
            financial.Add(new(intent, d.Return));
            paymentTruth.Add(new(group.Key, id, d.Attack.HasValue, d.Attack, d.Attack is { } a ? campaignKeys[a] : null, d.HardNegative));

            var bucket = d.At - d.At % ObservedFinanceVersion.BucketMs;
            var key = (src.AccountId, bucket);
            if (!anomalyTruth.TryGetValue(key, out var existing) || !existing.Anomalous && d.Anomaly != EpisodeAnomaly.None)
            {
                anomalyTruth[key] = new(group.Key, src.AccountId, bucket, bucket + ObservedFinanceVersion.BucketMs,
                    d.Anomaly, d.Anomaly != EpisodeAnomaly.None);
            }
        }

        return new(group, new(run, checked(config.OriginMs + config.Duration), financial.MoveToImmutable()),
            new(paymentTruth.MoveToImmutable(), [.. anomalyTruth.Values.OrderBy(x => x.BoundaryMs).ThenBy(x => x.AccountId, StringComparer.Ordinal)]),
            Fingerprint(ordered), [.. ordered.Where(d => d.Attack.HasValue).GroupBy(d => d.Attack).OrderBy(g => g.Key)
                .Select(g => Fingerprint(g.ToArray()))]);

        long Slot(int day, int part)
        {
            var offset = day * 24 * V4EpisodeConfig.HourMs;
            var dayMs = Math.Min(24 * V4EpisodeConfig.HourMs, config.Duration - offset);
            return checked(config.OriginMs + offset + dayMs * part / 1_000 + time.NextInt(0, 30_000));
        }

        int Other(int first, int second = -1)
        {
            int target;
            do { target = counterparty.NextInt(0, accounts.Length); } while (target == first || target == second);
            return target;
        }

        void Add(long at, int from, int to, long amount, bool returnRequested = false, bool hard = false,
            EpisodeAttack? attack = null, EpisodeAnomaly anomaly = EpisodeAnomaly.None)
        {
            if (at < config.OriginMs || at >= config.OriginMs + config.Duration) throw new InvalidOperationException("Plan exceeds episode duration.");
            drafts.Add(new(at, from, to, amount, returnRequested, hard, attack, anomaly));
        }
    }

    private static string Fingerprint(Draft[] ordered)
    {
        var w = ObservationEncoding.Begin("episode-behavior-fingerprint/1").WriteCount(ordered.Length);
        foreach (var d in ordered)
        {
            // Account ordinals allow exact duplicate behavior to be detected across identity namespaces.
            w.WriteInt64(d.At).WriteInt32(d.From).WriteInt32(d.To).WriteInt64(d.Amount).WriteBoolean(d.Return);
        }
        return ObservationEncoding.Hash(w.ToArray());
    }

    private static SeededRandom Stream(V4RootGroup group, string domain)
    {
        var hash = SHA256.HashData(new CanonicalWriter().WriteString(BehavioralEpisodeScenario.GeneratorVersion)
            .WriteString(group.Key).WriteString(domain).ToArray());
        return SeededRandom.ForStream(BinaryPrimitives.ReadInt64BigEndian(hash), 0);
    }
}
