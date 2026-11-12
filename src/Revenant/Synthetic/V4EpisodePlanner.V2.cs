using Revenant.Contracts;
using Revenant.Scenario;

namespace Revenant.Synthetic;

internal static partial class V4EpisodePlanner
{
    private static V4EpisodeDesign PlanV2(V4EpisodeConfig config, V4RootGroup group, ScenarioRunId physicalRun)
    {
        const long hour = V4EpisodeConfig.HourMs;
        const long day = V4EpisodeConfig.WarmupMs;
        const int stratum = 6 * (int)hour;
        var population = Random("population");
        var schedule = Random("ordinary-schedules");
        var behavior = Random("account-behavior");
        var family = Random("attack-family");
        var timing = Random("campaign-timing");
        var counterparties = Random("counterparty-choice");
        var amounts = Random("payment-amounts");
        var lookalikes = Random("legitimate-lookalikes");
        var calendar = Random("calendar-origin");
        var origin = checked(config.OriginMs + calendar.NextInt(0, 56) * day + calendar.NextInt(0, 288) * 300_000L);
        var opening = BehavioralEpisodeScenario.Create(physicalRun, group.Seed, origin, config.Duration,
            [.. Enumerable.Range(0, 6).Select(_ => population.NextInt(3, 7))], config.Generator);
        var accounts = opening.Manifest.Accounts;
        var byBank = accounts.Select((a, i) => (a.BankId, i)).GroupBy(a => a.BankId)
            .Select(g => g.Select(a => a.i).ToArray()).ToArray();
        var drafts = new List<Draft>();
        var regulars = Enumerable.Range(0, accounts.Length).ToArray();
        behavior.Shuffle(regulars);
        var frequent = regulars.Take(accounts.Length / 3).ToArray();
        var recurring = frequent.Select(from => (From: from, To: Other(from, counterparties),
            Offset: schedule.NextInt(600_000, (int)day - 600_000))).ToArray();
        for (long elapsed = 0; elapsed < config.Duration; elapsed += day)
        {
            foreach (var recurringPayment in recurring)
            {
                Add(origin + elapsed + recurringPayment.Offset + schedule.NextInt(-480_000, 480_001),
                    recurringPayment.From, recurringPayment.To, amounts.NextInt(4_000, 170_000));
            }
            for (var i = 0; i < 17; i++)
            {
                var from = i % 2 == 0 ? Pick(frequent, schedule) : Participant(schedule);
                Add(origin + elapsed + schedule.NextInt(0, (int)day), from, Other(from, counterparties),
                    i % 5 == 0 ? amounts.NextInt(95_000, 365_000) : amounts.NextInt(1_500, 155_000),
                    hard: i % 5 == 0, returnRequested: i == 0);
            }
            if (schedule.NextInt(0, 4) != 0)
            {
                var vendor = recurring[schedule.NextInt(0, recurring.Length)];
                var at = origin + elapsed + schedule.NextInt(0, (int)day - 600_000);
                var invoices = schedule.NextInt(8, 11);
                for (var i = 0; i < invoices; i++)
                {
                    Add(at, vendor.From, i % 3 == 0 ? vendor.To : Other(vendor.From, counterparties),
                        amounts.NextInt(8_000, 180_000), hard: true, anomaly: EpisodeAnomaly.OperationalBurst);
                    at += schedule.NextInt(7_000, 53_001);
                }
            }
        }

        var families = Enum.GetValues<EpisodeAttack>();
        family.Shuffle(families);
        var slots = Enumerable.Range(0, (config.Hours - 24) / 6).ToArray();
        timing.Shuffle(slots);
        for (var index = 0; index < families.Length; index++)
        {
            // Sample both labels from the same time stratum; their start times are independent within it.
            var window = origin + day + slots[index % slots.Length] * (long)stratum;
            var attack = families[index];
            Pattern(attack, window + timing.NextInt(0, stratum - 3_600_000), malicious: true);
            Pattern(attack, window + lookalikes.NextInt(0, stratum - 3_600_000), malicious: false);
        }
        return Assemble(group, opening, config.Duration, drafts, Random("campaign-identity"));

        SeededRandom Random(string name) => Stream(group, name, BehavioralEpisodeScenario.GeneratorVersionV2);
        int Participant(SeededRandom rng) => rng.NextInt(0, accounts.Length);
        static int Pick(int[] candidates, SeededRandom rng) => candidates[rng.NextInt(0, candidates.Length)];
        int Other(int from, SeededRandom rng)
        {
            var candidate = rng.NextInt(0, accounts.Length - 1);
            return candidate >= from ? candidate + 1 : candidate;
        }

        void Pattern(EpisodeAttack attack, long at, bool malicious)
        {
            var participants = malicious ? family : lookalikes;
            var spacing = malicious ? timing : lookalikes;
            var destinations = malicious ? counterparties : lookalikes;
            var sender = Participant(participants);
            var recipient = Other(sender, destinations);
            switch (attack)
            {
                case EpisodeAttack.Ring:
                    var route = byBank.Select(bank => Pick(bank, participants)).ToArray();
                    participants.Shuffle(route);
                    var principal = amounts.NextInt(22_000, 155_000);
                    for (var i = 0; i < route.Length; i++)
                    {
                        // A legitimate cross-bank batch uses the same kind of accounts, amounts and cadence.
                        var from = malicious || i % 3 == 0 ? route[i] : sender;
                        var to = route[(i + 1) % route.Length];
                        if (from == to) to = Other(from, destinations);
                        Emit(from, to, malicious ? principal + amounts.NextInt(-3_000, 3_001)
                            : amounts.NextInt(19_000, 158_000), EpisodeAnomaly.CoordinatedActivity);
                        at += config.ShiftedRing ? spacing.NextInt(180_000, 490_001) : spacing.NextInt(16_000, 150_001);
                    }
                    break;
                case EpisodeAttack.Takeover:
                    Emit(sender, recipient, amounts.NextInt(95_000, 365_000), EpisodeAnomaly.BehaviorDeviation);
                    at += spacing.NextInt(18_000, 260_001);
                    Emit(sender, Other(sender, destinations), amounts.NextInt(12_000, 140_000), EpisodeAnomaly.BehaviorDeviation);
                    break;
                case EpisodeAttack.Dispersal:
                    var count = participants.NextInt(4, 7);
                    for (var i = 0; i < count; i++)
                    {
                        Emit(sender, Other(sender, destinations), amounts.NextInt(1_500, 30_000));
                        at += spacing.NextInt(90_000, 515_001);
                    }
                    break;
                case EpisodeAttack.CoordinatedBurst:
                    var senders = byBank.Select(bank => Pick(bank, participants)).ToArray();
                    participants.Shuffle(senders);
                    foreach (var from in senders)
                    {
                        var to = from == recipient || !malicious && participants.NextInt(0, 3) == 0
                            ? Other(from, destinations) : recipient;
                        Emit(from, to, amounts.NextInt(24_000, 175_000), EpisodeAnomaly.CoordinatedActivity);
                        at += spacing.NextInt(7_000, 92_001);
                    }
                    break;
                case EpisodeAttack.BeneficiaryAbuse:
                    for (var i = 0; i < 3; i++)
                    {
                        Emit(sender, i == 1 || !malicious && i == 2 ? Other(sender, destinations) : recipient,
                            amounts.NextInt(6_000, 185_000));
                        at += spacing.NextInt(20_000, 175_001);
                    }
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(attack));
            }

            void Emit(int from, int to, long amount, EpisodeAnomaly anomaly = EpisodeAnomaly.None) =>
                Add(at, from, to, amount, hard: !malicious, attack: malicious ? attack : null,
                    anomaly: malicious ? anomaly : EpisodeAnomaly.OperationalBurst);
        }

        void Add(long at, int from, int to, long amount, bool hard = false, bool returnRequested = false,
            EpisodeAttack? attack = null, EpisodeAnomaly anomaly = EpisodeAnomaly.None)
        {
            if (from == to || at < origin || at >= checked(origin + config.Duration)
                || (attack.HasValue && at < origin + day))
                throw new InvalidOperationException("V2 payment is outside the allowed episode schedule.");
            drafts.Add(new(at, from, to, amount, returnRequested, hard, attack, anomaly));
        }
    }
}
