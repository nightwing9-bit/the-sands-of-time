using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using Revenant.Contracts;
using Revenant.Observations;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Synthetic;

internal enum AccountRhythm : byte { Treasury = 1, Commercial = 2, Regular = 3, Sporadic = 4, Quiet = 5 }

/// <summary>
/// Generator 4 for R7/2. Generator 3 is preserved unchanged for its pilot evidence. This version replaces three statistical
/// defects measured on that pilot:
/// <list type="bullet">
/// <item>Ordinary account timing was redrawn every day. Here each account keeps a persistent rhythm (session times, lengths,
/// steps and per-slot volume) with bounded daily jitter, skipped slots and count noise, so its history reveals its behavior.</item>
/// <item>Anomalous and legitimate bursts came from one count mechanism and differed only by label. Here injected episodes come
/// from four distinct mechanisms, and statistical truth is assigned by an oracle that compares the observed window with the
/// account's own ordinary process (Monte Carlo, independent random stream). Business intent is recorded separately.</item>
/// <item>Interbank routes and cycle timing were redrawn every day. Here bank treasuries settle persistent bilateral cycles with
/// stochastic amounts, skipped instances, daily jitter and one explicit regime change per root, so available balances carry
/// learnable temporal structure besides retail noise, holds, returns, developing pressure and sudden shocks.</item>
/// </list>
/// Every planned payment is sized against a planned balance walk, so ordinary activity, injected episodes and pressure drains
/// remain fundable under the R3 ledger rules. Observed counts are the committed intents; export verifies they match the plan.
/// </summary>
internal static class V4LongitudinalPlannerG4
{
    internal const string Version = BehavioralEpisodeScenario.GeneratorVersionV4;
    internal const string TruthSchema = "revenant-longitudinal-truth/2";
    internal const int BucketsPerDay = V4LongitudinalPlanner.BucketsPerDay;
    internal const int AccountsPerBank = 4;
    internal const int BankCount = BehavioralEpisodeScenario.BankCount;

    /// <summary>
    /// Episodes are injected from day 4. The two-day-window detector (L = 576) needs 2L − 2 = 1,150 of the account's own buckets
    /// before its state no longer depends on the training series, so earlier episodes could never be scored.
    /// </summary>
    internal const int FirstInjectionDay = 4;

    internal const int InjectionsPerDay = 8;
    internal const int OracleDraws = 4_000;
    internal const double AnomalousTail = 0.001;
    internal const double AmbiguousTail = 0.05;
    private const long Bucket = ObservedFinanceVersion.BucketMs;

    internal static IEnumerable<LongitudinalRoot> Roots(LongitudinalSplit split, int days = 7)
    {
        if (days is not (7 or 14)) throw new ArgumentOutOfRangeException(nameof(days));
        var count = split switch
        {
            // Pilot roots 0–2 are executed in PostgreSQL for forecast development; 3–5 extend plan-derived anomaly development.
            LongitudinalSplit.Pilot => 6,
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

    private sealed record Session(int Start, int Length, int Step, int PerSlot);

    private sealed record Rhythm(AccountRhythm Kind, Session[] Sessions, int BackgroundMin, int BackgroundMax, int BackgroundFrom,
        int BackgroundTo, int Jitter, int SkipOneIn, int AmountMin, int AmountMax);

    /// <summary>A persistent bilateral treasury cycle. <see cref="Periods"/> holds the period in force on each day.</summary>
    private sealed record Cycle(int A, int B, int[] Periods, long Amount, int ImbalancePermille, int SessionStart, int SessionEnd);

    private sealed record Instruction(int BucketIndex, int Sender, int Receiver, long Amount, bool Return, int ReviewDelay, long Order);

    private sealed record Episode(int Account, LongitudinalEventKind Kind, VelocityMechanism Mechanism, EventIntent Intent,
        int Start, int End);

    internal static V4LongitudinalEpisode Plan(LongitudinalRoot root, ScenarioRunId execution) => Plan(root, execution, Version);

    internal static V4LongitudinalEpisode Plan(LongitudinalRoot root, ScenarioRunId execution, string version)
    {
        var calendar = SeededRandom.ForStream(root.Seed, 41);
        var ordinary = SeededRandom.ForStream(root.Seed, 42);
        var stress = SeededRandom.ForStream(root.Seed, 43);
        var treasury = SeededRandom.ForStream(root.Seed, 44);
        var inject = SeededRandom.ForStream(root.Seed, 45);
        var review = SeededRandom.ForStream(root.Seed, 46);
        var traits = SeededRandom.ForStream(root.Seed, 47);
        var shifted = root.Split == LongitudinalSplit.Challenge;
        var duration = root.Days * BucketsPerDay;
        var origin = 1_772_323_200_000L + calendar.NextInt(0, 84 * BucketsPerDay) * Bucket;
        var run = BehavioralEpisodeScenario.Create(execution, root.Seed, origin, duration * Bucket,
            [.. Enumerable.Repeat(AccountsPerBank, BankCount)], version);
        var accounts = run.Manifest.Accounts;
        if (accounts.Length != BankCount * AccountsPerBank) throw new InvalidOperationException("Unexpected generator-4 population.");
        var opening = accounts.Select(a => a.OpeningBalance.MinorUnits).ToArray();
        var bankOpening = Enumerable.Range(0, BankCount).Select(b => Enumerable.Range(b * AccountsPerBank, AccountsPerBank)
            .Sum(a => opening[a])).ToArray();

        var rhythms = AssignRhythms(traits, shifted);
        var cycles = AssignCycles(traits, Enumerable.Range(0, BankCount).Select(b => opening[b * AccountsPerBank]).ToArray(), root.Days, shifted);
        var instructions = new List<Instruction>();
        var episodes = new List<Episode>();
        var hardNegatives = new List<Episode>();
        long order = 0;

        // Ordinary customer rhythms, one persistent process per account.
        for (var day = 0; day < root.Days; day++)
            for (var account = 0; account < accounts.Length; account++)
            {
                if (rhythms[account].Kind == AccountRhythm.Treasury) continue;
                var r = rhythms[account];
                var emitted = new List<(int Tod, int Count, int Session)>();
                OrdinaryDay(r, ordinary, (tod, count, session) => emitted.Add((tod, count, session)));
                foreach (var (tod, count, _) in emitted)
                    for (var j = 0; j < count; j++)
                        Add(day * BucketsPerDay + tod, account, Counterparty(account, ordinary), ordinary.NextInt(r.AmountMin, r.AmountMax + 1),
                            requestReturn: ordinary.NextInt(0, 45) == 0);
                if (r.Kind == AccountRhythm.Commercial && day >= FirstInjectionDay)
                    foreach (var session in emitted.Where(e => e.Session >= 0).GroupBy(e => e.Session))
                        hardNegatives.Add(new(account, LongitudinalEventKind.LegitimateBurst, VelocityMechanism.RecurringBatch,
                            EventIntent.Legitimate, day * BucketsPerDay + session.Min(e => e.Tod), day * BucketsPerDay + session.Max(e => e.Tod)));
            }

        // Treasury settlement cycles between bank pairs.
        for (var day = 0; day < root.Days; day++)
            foreach (var cycle in cycles)
                CycleDay(cycle, day, treasury, (from, to, tod, amount) =>
                    Add(day * BucketsPerDay + tod, Treasury(from), Treasury(to), amount));

        // One reviewed payment per day creates a genuine reservation, review and settlement.
        var reviewDays = new List<(int Bucket, int Sender)>();
        for (var day = 0; day < root.Days; day++)
        {
            var sender = review.NextInt(0, accounts.Length);
            var at = day * BucketsPerDay + review.NextInt(20, 245);
            reviewDays.Add((at, sender));
            Add(at, sender, Counterparty(sender, review), review.NextInt(5_000, 20_001), reviewDelay: review.NextInt(2, 9));
        }

        // Injected outgoing-velocity episodes on customer accounts. Statistical truth is assigned later by the oracle.
        var intensity = rhythms.Select((r, a) => r.Kind == AccountRhythm.Treasury ? null : MeanIntensity(r, root.Seed, a)).ToArray();
        for (var day = FirstInjectionDay; day < root.Days; day++)
        {
            var candidates = Enumerable.Range(0, accounts.Length).Where(a => rhythms[a].Kind != AccountRhythm.Treasury).ToArray();
            inject.Shuffle(candidates);
            foreach (var account in candidates.Take(InjectionsPerDay))
                Inject(day, account);
        }

        // Developing pressure and one sudden shock, sized from the planned balances so every drain is fundable.
        if (version == V4LongitudinalPlannerG5.Version)
            foreach (var sweep in V4LongitudinalPlannerG5.OperationalSweeps(root, bankOpening))
                Add(sweep.Bucket, Treasury(sweep.From), Treasury(sweep.To), sweep.Amount);
        var walk = Walk();
        var stressed = Enumerable.Range(0, BankCount).ToArray();
        stress.Shuffle(stressed);
        var stressDays = Enumerable.Range(1, root.Days - 1).ToArray();
        stress.Shuffle(stressDays);
        var newPressure = version == V4LongitudinalPlannerG5.Version;
        var suddenIndex = newPressure ? BankCount : stress.NextInt(0, 3);
        var bankEvents = new List<(string Bank, LongitudinalEventKind Kind, int Start, int End)>();
        for (var i = 0; i < (newPressure ? BankCount + 1 : 3); i++)
        {
            var bank = stressed[i % BankCount];
            var recipient = stressed[(i + 3) % BankCount];
            var start = stressDays[i % stressDays.Length] * BucketsPerDay + stress.NextInt(65, 215);
            var sudden = i == suddenIndex;
            if (newPressure && i >= stressDays.Length)
                start = stressDays[0] * BucketsPerDay + 10;
            var installments = sudden ? 1 : stress.NextInt(8, 17);
            var gap = sudden ? 1 : stress.NextInt(1, shifted ? 4 : 3);
            var pause = stress.NextInt(9, 21);
            // The drain and its recovery must complete inside the root (relevant for late, slow shifted-challenge drains).
            start = Math.Min(start, duration - 1 - ((installments - 1) * gap + pause + installments));
            var drainEnd = start + (installments - 1) * gap;
            var recover = drainEnd + pause;
            var keepPermille = stress.NextInt(40, newPressure && !sudden ? 131 : 81);
            var members = Enumerable.Range(bank * AccountsPerBank, AccountsPerBank).ToArray();
            var planned = members.Select(a => Math.Max(0, walk.BalanceAt(a, start))).ToArray();
            var total = planned.Sum();
            if (total < bankOpening[bank] * 15 / 100) continue;
            var keep = bankOpening[bank] * keepPermille / 1_000;
            for (var m = 0; m < members.Length; m++)
            {
                var principal = Math.Max(0, planned[m] - keep * planned[m] / Math.Max(1, total) - Reserve(members[m]));
                if (principal < installments) continue;
                var to = recipient * AccountsPerBank + (members[m] - bank * AccountsPerBank);
                for (var step = 0; step < installments; step++)
                {
                    var amount = principal * (step + 1) / installments - principal * step / installments;
                    Add(start + step * gap, members[m], to, amount);
                    Add(recover + step, to, members[m], amount);
                }
                episodes.Add(new(members[m], LongitudinalEventKind.VelocityAnomaly, VelocityMechanism.TreasuryDrain,
                    EventIntent.Legitimate, start, drainEnd));
                episodes.Add(new(to, LongitudinalEventKind.VelocityAnomaly, VelocityMechanism.TreasuryDrain,
                    EventIntent.Legitimate, recover, recover + installments - 1));
            }
            bankEvents.Add((accounts[bank * AccountsPerBank].BankId,
                sudden ? LongitudinalEventKind.SuddenShock : LongitudinalEventKind.DevelopingPressure, start, recover + installments - 1));
        }

        // Final walk: any instruction that would overdraw its planned sender is dropped, so counts reflect fundable intents.
        var final = Walk();
        var counts = new long[accounts.Length, duration];
        foreach (var x in final.Kept) counts[x.Sender, x.BucketIndex]++;

        var oracle = SeededRandom.ForStream(root.Seed, 48);
        var events = new List<LongitudinalEvent>();
        foreach (var e in episodes.OrderBy(e => e.Start).ThenBy(e => e.Account))
        {
            long observed = 0;
            for (var b = e.Start; b <= e.End; b++) observed += counts[e.Account, b];
            var (mean, tail) = Oracle(e.Account, e.Start, e.End, observed);
            var truth = tail <= AnomalousTail ? VelocityTruth.Anomalous : tail <= AmbiguousTail ? VelocityTruth.Ambiguous : VelocityTruth.Normal;
            var kind = e.Intent == EventIntent.FraudAssociated ? LongitudinalEventKind.FraudAssociatedVelocityAnomaly : e.Kind;
            events.Add(Event(accounts[e.Account].AccountId, kind, e.Start, e.End, truth, e.Intent, e.Mechanism, observed, mean, tail));
        }
        foreach (var e in hardNegatives)
        {
            long observed = 0;
            for (var b = e.Start; b <= e.End; b++) observed += counts[e.Account, b];
            if (episodes.Any(x => x.Account == e.Account && x.Start <= e.End && e.Start <= x.End)) continue;
            events.Add(Event(accounts[e.Account].AccountId, e.Kind, e.Start, e.End, VelocityTruth.Normal, e.Intent, e.Mechanism,
                observed, null, null));
        }
        foreach (var (bank, kind, start, end) in bankEvents)
            events.Add(Event(bank, kind, start, end, null, null, null, null, null, null));

        var ordered = final.Kept;
        var payments = ImmutableArray.CreateBuilder<V4PlannedPayment>(ordered.Count);
        var reviews = new Dictionary<string, long>(StringComparer.Ordinal);
        var seen = new HashSet<(int, int)>();
        var canonical = ObservationEncoding.Begin(version).WriteInt64(origin).WriteCount(ordered.Count);
        var previous = -1;
        var slotOffset = 0;
        var timing = SeededRandom.ForStream(root.Seed, 49);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (bucket, sender, receiver, amount, returns, reviewDelay, _) = ordered[i];
            if (bucket != previous) slotOffset = timing.NextInt(250, 3_000);
            previous = bucket;
            var at = origin + bucket * Bucket + slotOffset;
            slotOffset += 1_400;
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

        var labels = events.OrderBy(e => e.Start).ThenBy(e => e.Subject, StringComparer.Ordinal).ThenBy(e => e.Kind)
            .Select((e, i) => e with { Id = $"{root.Key}/{i + 1}" }).ToImmutableArray();
        return new(root, new(run, origin + duration * Bucket, payments.MoveToImmutable()), labels, reviews,
            ObservationEncoding.Hash(canonical.ToArray()),
            accounts.Select((a, i) => (a.AccountId, rhythms[i].Kind.ToString())).ToDictionary(x => x.AccountId, x => x.Item2, StringComparer.Ordinal),
            version, TruthSchema);

        int Treasury(int bank) => bank * AccountsPerBank;

        long Reserve(int account) => opening[account] / 50;

        int Counterparty(int sender, SeededRandom random)
        {
            var bank = (sender / AccountsPerBank + random.NextInt(1, BankCount)) % BankCount;
            return bank * AccountsPerBank + random.NextInt(1, AccountsPerBank);
        }

        void Add(int at, int from, int to, long amount, bool requestReturn = false, int reviewDelay = 0)
        {
            if (at < 0 || at + reviewDelay >= duration || from == to || amount <= 0)
                throw new InvalidOperationException("Invalid longitudinal payment schedule.");
            instructions.Add(new(at, from, to, amount, requestReturn, reviewDelay, order++));
        }

        void Inject(int day, int account)
        {
            var r = rhythms[account];
            var dayStart = day * BucketsPerDay;
            var mechanisms = r.Kind is AccountRhythm.Sporadic or AccountRhythm.Quiet
                ? new[] { VelocityMechanism.OffSchedule, VelocityMechanism.SustainedElevation, VelocityMechanism.DormantBurst }
                : [VelocityMechanism.OffSchedule, VelocityMechanism.AmplitudeSurge, VelocityMechanism.SustainedElevation];
            var mechanism = mechanisms[inject.NextInt(0, mechanisms.Length)];
            var fraudPercent = mechanism switch
            {
                VelocityMechanism.DormantBurst => 70, VelocityMechanism.SustainedElevation => 30, _ => 50,
            };
            var intent = inject.NextInt(0, 100) < fraudPercent ? EventIntent.FraudAssociated : EventIntent.Legitimate;
            int length;
            Func<int, int> perBucket;
            int start;
            switch (mechanism)
            {
                case VelocityMechanism.OffSchedule:
                    length = inject.NextInt(2, 5);
                    var typical = r.Sessions.Length == 0 ? 1 : r.Sessions.Max(s => s.PerSlot);
                    perBucket = _ => inject.NextInt(Math.Max(1, typical - 1), typical + 2);
                    start = QuietStart(account, length) is { } quiet ? quiet : -1;
                    break;
                case VelocityMechanism.AmplitudeSurge:
                    length = inject.NextInt(1, 4);
                    var session = r.Sessions[inject.NextInt(0, r.Sessions.Length)];
                    var multiplier = inject.NextInt(2, 4);
                    perBucket = _ => session.PerSlot * multiplier;
                    start = session.Start + inject.NextInt(0, Math.Max(1, session.Length * session.Step - length));
                    break;
                case VelocityMechanism.SustainedElevation:
                    length = inject.NextInt(6, 13);
                    perBucket = _ => 1;
                    start = QuietStart(account, length) is { } calm ? calm : -1;
                    break;
                default:
                    length = inject.NextInt(1, 3);
                    var burst = inject.NextInt(3, 7);
                    perBucket = k => k == 0 ? burst - burst / 2 : burst / 2;
                    if (length == 1) perBucket = _ => burst;
                    start = inject.NextInt(30, 250);
                    break;
            }
            if (start < 0 || start + length > BucketsPerDay) return;
            var at = dayStart + start;
            if (episodes.Any(e => e.Account == account && e.Start <= at + length - 1 + 6 && at <= e.End + 6)) return;
            var (low, high) = intent == EventIntent.FraudAssociated ? (500, 4_000) : (150, 2_400);
            for (var k = 0; k < length; k++)
                for (var j = perBucket(k); j > 0; j--)
                    Add(at + k, account, Counterparty(account, inject), inject.NextInt(low, high + 1));
            episodes.Add(new(account, LongitudinalEventKind.VelocityAnomaly, mechanism, intent, at, at + length - 1));
        }

        int? QuietStart(int account, int length)
        {
            var mean = intensity[account]!;
            var options = Enumerable.Range(12, BucketsPerDay - 12 - length)
                .Where(s => Enumerable.Range(s - 2, length + 4).All(t => t < 0 || t >= BucketsPerDay || mean[t] < 0.02)).ToArray();
            return options.Length == 0 ? null : options[inject.NextInt(0, options.Length)];
        }

        (double Mean, double Tail) Oracle(int account, int start, int end, long observed)
        {
            var day = start / BucketsPerDay;
            var from = start % BucketsPerDay;
            var to = end % BucketsPerDay;
            if (end / BucketsPerDay != day) to = BucketsPerDay - 1;
            var exceed = 0;
            double total = 0;
            var draw = new long[BucketsPerDay];
            for (var n = 0; n < OracleDraws; n++)
            {
                Array.Clear(draw);
                if (rhythms[account].Kind == AccountRhythm.Treasury)
                {
                    foreach (var cycle in cycles.Where(c => Treasury(c.A) == account || Treasury(c.B) == account))
                        CycleDay(cycle, day, oracle, (f, _, tod, _) => { if (Treasury(f) == account) draw[tod]++; });
                }
                else OrdinaryDay(rhythms[account], oracle, (tod, count, _) => draw[tod] += count);
                long sum = 0;
                for (var t = from; t <= to; t++) sum += draw[t];
                total += sum;
                if (sum >= observed) exceed++;
            }
            return (total / OracleDraws, (exceed + 1d) / (OracleDraws + 1));
        }

        (List<Instruction> Kept, Func<int, int, long> BalanceAt) Walk()
        {
            var sorted = instructions.OrderBy(x => x.BucketIndex).ThenBy(x => x.Order).ToList();
            var balance = (long[])opening.Clone();
            var kept = new List<Instruction>(sorted.Count);
            var history = Enumerable.Range(0, accounts.Length).Select(_ => new List<(int Bucket, long Balance)>()).ToArray();
            foreach (var x in sorted)
            {
                if (balance[x.Sender] - x.Amount < Reserve(x.Sender)) continue;
                balance[x.Sender] -= x.Amount;
                balance[x.Receiver] += x.Amount;
                history[x.Sender].Add((x.BucketIndex, balance[x.Sender]));
                history[x.Receiver].Add((x.BucketIndex, balance[x.Receiver]));
                kept.Add(x);
            }
            return (kept, (account, bucket) =>
            {
                var entries = history[account];
                var last = opening[account];
                foreach (var (b, value) in entries)
                {
                    if (b >= bucket) break;
                    last = value;
                }
                return last;
            });
        }

        LongitudinalEvent Event(string subject, LongitudinalEventKind kind, int start, int end, VelocityTruth? truth, EventIntent? intent,
            VelocityMechanism? mechanism, long? observed, double? mean, double? tail) =>
            new(string.Empty, subject, kind, start, end, truth, intent, mechanism, observed, mean, tail);
    }

    private static Rhythm[] AssignRhythms(SeededRandom traits, bool shifted)
    {
        var rhythms = new Rhythm[BankCount * AccountsPerBank];
        var customer = Enumerable.Range(0, rhythms.Length).Where(a => a % AccountsPerBank != 0).ToArray();
        traits.Shuffle(customer);
        var kinds = new[]
        {
            AccountRhythm.Commercial, AccountRhythm.Commercial, AccountRhythm.Commercial, AccountRhythm.Commercial, AccountRhythm.Commercial,
            AccountRhythm.Regular, AccountRhythm.Regular, AccountRhythm.Regular, AccountRhythm.Regular, AccountRhythm.Regular, AccountRhythm.Regular,
            AccountRhythm.Sporadic, AccountRhythm.Sporadic, AccountRhythm.Sporadic, AccountRhythm.Sporadic,
            AccountRhythm.Quiet, AccountRhythm.Quiet, AccountRhythm.Quiet,
        };
        for (var b = 0; b < BankCount; b++)
            rhythms[b * AccountsPerBank] = new(AccountRhythm.Treasury, [], 0, 0, 0, 0, 0, 1, 0, 0);
        for (var i = 0; i < customer.Length; i++)
        {
            var jitter = shifted ? 4 : 2;
            rhythms[customer[i]] = kinds[i] switch
            {
                AccountRhythm.Commercial => new(AccountRhythm.Commercial,
                [
                    new(traits.NextInt(40, 111), traits.NextInt(6, 13), 1, traits.NextInt(2, 5)),
                    new(traits.NextInt(150, 231), traits.NextInt(6, 13), 1, traits.NextInt(2, 5)),
                ], 1, 3, 30, 260, jitter, 8, 150, 900),
                AccountRhythm.Regular => new(AccountRhythm.Regular, Enumerable.Range(0, traits.NextInt(1, 3))
                    .Select(k => new Session(k == 0 ? traits.NextInt(30, 130) : traits.NextInt(150, 240), traits.NextInt(6, 15),
                        traits.NextInt(1, 4), 1)).ToArray(), 1, 3, 30, 260, jitter + 1, 6, 150, 1_500),
                AccountRhythm.Sporadic => new(AccountRhythm.Sporadic, [], 8, 14, 20, 270, 0, 1, 150, 2_400),
                _ => new(AccountRhythm.Quiet, [], 3, 6, 30, 250, 0, 1, 150, 2_400),
            };
        }
        return rhythms;
    }

    private static Cycle[] AssignCycles(SeededRandom traits, long[] treasuryOpening, int days, bool shifted)
    {
        int[] major = shifted ? [6, 9, 12] : [6, 8, 12];
        int[] minor = shifted ? [4, 7, 10] : [4, 5, 10];
        var cycles = new List<Cycle>();
        foreach (var (periods, low, high) in new[] { (major, 10, 31), (minor, 3, 11) })
        {
            var banks = Enumerable.Range(0, BankCount).ToArray();
            traits.Shuffle(banks);
            for (var p = 0; p < 3; p++)
            {
                var (a, b) = (banks[2 * p], banks[2 * p + 1]);
                var period = periods[traits.NextInt(0, periods.Length)];
                var schedule = Enumerable.Repeat(period, days).ToArray();
                cycles.Add(new(a, b, schedule, Math.Min(treasuryOpening[a], treasuryOpening[b]) * traits.NextInt(low, high) / 1_000,
                    traits.NextInt(950, 1_051), traits.NextInt(60, 111), traits.NextInt(180, 241)));
            }
        }
        // One explicit regime change per root: a major cycle switches period from a random day onward.
        var changed = cycles[traits.NextInt(0, 3)];
        var from = traits.NextInt(2, days);
        var next = major.Where(p => p != changed.Periods[0]).ToArray()[traits.NextInt(0, major.Length - 1)];
        for (var d = from; d < days; d++) changed.Periods[d] = next;
        return cycles.ToArray();
    }

    /// <summary>One day of an account's ordinary process. Shared by the plan and the truth oracle.</summary>
    private static void OrdinaryDay(Rhythm r, SeededRandom random, Action<int, int, int> emit)
    {
        for (var s = 0; s < r.Sessions.Length; s++)
        {
            var session = r.Sessions[s];
            var shift = r.Jitter == 0 ? 0 : random.NextInt(-r.Jitter, r.Jitter + 1);
            for (var slot = 0; slot < session.Length; slot++)
            {
                if (random.NextInt(0, r.SkipOneIn) == 0) continue;
                var tod = session.Start + shift + slot * session.Step;
                var count = Math.Max(1, session.PerSlot + random.NextInt(-1, 2));
                if (tod is >= 0 and < BucketsPerDay) emit(tod, count, s);
            }
        }
        if (r.BackgroundMax == 0) return;
        var background = random.NextInt(r.BackgroundMin, r.BackgroundMax + 1);
        for (var i = 0; i < background; i++) emit(random.NextInt(r.BackgroundFrom, r.BackgroundTo), 1, -1);
    }

    /// <summary>One day of a bilateral treasury cycle: A pays B, and half a period later B pays A an imbalanced amount.</summary>
    private static void CycleDay(Cycle c, int day, SeededRandom random, Action<int, int, int, long> emit)
    {
        var period = c.Periods[day];
        var shift = random.NextInt(-3, 4);
        for (var t = c.SessionStart + shift; t + period / 2 <= c.SessionEnd + shift && t + period / 2 < BucketsPerDay; t += period)
        {
            if (random.NextInt(0, 12) != 0)
                emit(c.A, c.B, t, Math.Max(1, c.Amount * random.NextInt(850, 1_151) / 1_000));
            if (random.NextInt(0, 12) != 0)
                emit(c.B, c.A, t + period / 2, Math.Max(1, c.Amount * c.ImbalancePermille / 1_000 * random.NextInt(850, 1_151) / 1_000));
        }
    }

    /// <summary>Expected ordinary count per time of day (Monte Carlo, independent stream), used to place off-schedule episodes.</summary>
    private static double[] MeanIntensity(Rhythm r, long seed, int account)
    {
        var random = SeededRandom.ForStream(seed, 0x4D000000UL + (ulong)account);
        var mean = new double[BucketsPerDay];
        const int draws = 400;
        for (var n = 0; n < draws; n++) OrdinaryDay(r, random, (tod, count, _) => mean[tod] += count / (double)draws);
        return mean;
    }
}
