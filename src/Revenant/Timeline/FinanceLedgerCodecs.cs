using System.Collections.Immutable;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;

namespace Revenant.Timeline;

/// <summary>
/// The V2 Timeline transition: the pure <see cref="FinanceLedgerReducer"/> applied to one position's single fact. Position
/// <c>k</c> of a V2 history is exactly fact <c>k</c> of the run, with journal input id <see cref="InputId"/>.
/// </summary>
public sealed class FinanceLedgerTransition : IDeterministicTransition<LedgerFinanceState, FinanceFact, LedgerFinanceContext>
{
    public static FinanceLedgerTransition Instance { get; } = new();

    public static string InputId(ScenarioRunId run, long factSequence) => $"{run.Value:N}/fact/{factSequence}";

    public LedgerFinanceState Apply(
        LedgerFinanceState previous, long position, IReadOnlyList<OrderedInput<FinanceFact>> inputs, LedgerFinanceContext pinnedContext)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count != 1 || inputs[0].Payload.Sequence != position
            || !string.Equals(inputs[0].InputId, InputId(previous.RunId, position), StringComparison.Ordinal))
        {
            throw new FinanceLedgerTransitionException(new LedgerFinanceError(
                LedgerFinanceErrorCode.OutOfOrderFact, $"Position {position} must carry exactly fact {position} of run {previous.RunId}."));
        }
        var next = FinanceLedgerReducer.Apply(previous, inputs[0].Payload, pinnedContext);
        return next.TryGetValue(out var state) ? state : throw new FinanceLedgerTransitionException(next.Error);
    }
}

public sealed class FinanceLedgerTransitionException(LedgerFinanceError error)
    : InvalidOperationException($"Finance V2 reducer rejected the step: {error}")
{
    public LedgerFinanceError Error { get; } = error;
}

/// <summary>Input codec of V2 histories: the canonical <see cref="FinanceFactCodec"/> envelope (input schema <see cref="FinanceLedgerVersion.InputSchemaVersion"/>).</summary>
public sealed class FinanceFactInputCodec : IInputCodec<FinanceFact>
{
    public static FinanceFactInputCodec Instance { get; } = new();
    public byte[] EncodeCanonical(FinanceFact input) => FinanceFactCodec.Encode(input);
    public FinanceFact Decode(ReadOnlySpan<byte> bytes) => FinanceFactCodec.Decode(bytes);
}

/// <summary>Context codec of V2 histories. Decoding re-resolves every pin; an unknown or changed pin fails closed.</summary>
public sealed class LedgerFinanceContextCodec : IContextCodec<LedgerFinanceContext>
{
    public static LedgerFinanceContextCodec Instance { get; } = new();

    public byte[] EncodeCanonical(LedgerFinanceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.EncodeCanonical();
    }

    public LedgerFinanceContext Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        if (r.ReadInt32() != 1)
        {
            throw new CanonicalFormatException("Unsupported finance V2 context format version.");
        }
        var scenario = r.ReadString();
        var manifest = r.ReadString();
        var reducer = r.ReadString();
        var policy = new PolicyRef(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString());
        var review = r.ReadString();
        r.EnsureEnd();
        var pinned = LedgerFinanceContext.FromPins(scenario, manifest, reducer, policy, review);
        return pinned.TryGetValue(out var context) ? context : throw new CanonicalFormatException(pinned.Error.Message);
    }
}

/// <summary>
/// Canonical complete-state codec of V2 histories (state schema <see cref="FinanceLedgerVersion.StateSchemaVersion"/>). Every field
/// is written; maps in ordinal key order. Unknown format, malformed or trailing bytes throw.
/// </summary>
public sealed class LedgerFinanceStateCodec : IStateCodec<LedgerFinanceState>
{
    public const int FormatVersion = 1;
    public static LedgerFinanceStateCodec Instance { get; } = new();

    public byte[] EncodeCanonical(LedgerFinanceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var w = new CanonicalWriter()
            .WriteInt32(FormatVersion).WriteGuid(state.RunId.Value).WriteString(state.ScenarioVersion).WriteString(state.ManifestContentHash)
            .WriteString(state.Currency).WriteInt64(state.ClockUtcMs).WriteInt64(state.LastIntentSequence).WriteInt64(state.LastFact)
            .WriteCount(state.Accounts.Count);
        foreach (var a in state.Accounts.Values)
        {
            w.WriteString(a.AccountId).WriteString(a.BankId).WriteInt64(a.PostedMinor).WriteInt64(a.ReservedMinor);
        }
        w.WriteCount(state.Payments.Count);
        foreach (var p in state.Payments.Values)
        {
            w.WriteBytes(PaymentIntentCodec.Instance.EncodeCanonical(p.Intent)).WriteInt64(p.IntentFact).WriteInt32(p.Velocity15m)
                .WriteByte((byte)p.Status).WriteByte((byte)p.Reason).WriteCount(p.Trail.Length);
            foreach (var s in p.Trail)
            {
                w.WriteByte((byte)s);
            }
            w.WriteInt64(p.ReservedMinor).WriteBoolean(p.Case.HasValue).WriteByte((byte)(p.Case ?? 0))
                .WriteInt32(p.RequiredApprovals).WriteInt32(p.Approvals).WriteBoolean(p.SettlementPosted).WriteBoolean(p.ReturnPosted);
        }
        w.WriteCount(state.VelocityWindows.Count);
        foreach (var (account, times) in state.VelocityWindows)
        {
            w.WriteString(account).WriteCount(times.Length);
            foreach (var t in times)
            {
                w.WriteInt64(t);
            }
        }
        return w.ToArray();
    }

    public LedgerFinanceState Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        if (r.ReadInt32() != FormatVersion)
        {
            throw new CanonicalFormatException("Unsupported finance V2 state format version.");
        }
        var runId = new ScenarioRunId(r.ReadGuid());
        if (runId.IsEmpty)
        {
            throw new CanonicalFormatException("Finance V2 state has an empty ScenarioRunId.");
        }
        var scenario = r.ReadString();
        var manifest = r.ReadString();
        var currency = r.ReadString();
        var clock = r.ReadInt64();
        var lastIntent = r.ReadInt64();
        var lastFact = r.ReadInt64();
        var accounts = ImmutableSortedDictionary.CreateBuilder<string, LedgerAccount>(StringComparer.Ordinal);
        var count = r.ReadCount();
        for (var i = 0; i < count; i++)
        {
            var a = new LedgerAccount(r.ReadString(), r.ReadString(), r.ReadInt64(), r.ReadInt64());
            if (!accounts.TryAdd(a.AccountId, a))
            {
                throw new CanonicalFormatException($"Duplicate account '{a.AccountId}'.");
            }
        }
        var payments = ImmutableSortedDictionary.CreateBuilder<string, LedgerPayment>(StringComparer.Ordinal);
        count = r.ReadCount();
        for (var i = 0; i < count; i++)
        {
            var intent = PaymentIntentCodec.Instance.Decode(r.ReadBytes());
            if (intent.RunId != runId)
            {
                throw new CanonicalFormatException($"Payment '{intent.IntentId}' belongs to another run.");
            }
            var intentFact = r.ReadInt64();
            var velocity = r.ReadInt32();
            var status = ReadEnum<PaymentStatus>(r.ReadByte());
            var reason = ReadEnum<PaymentReason>(r.ReadByte());
            var trailCount = r.ReadCount();
            var trail = ImmutableArray.CreateBuilder<PaymentStatus>(trailCount);
            for (var t = 0; t < trailCount; t++)
            {
                trail.Add(ReadEnum<PaymentStatus>(r.ReadByte()));
            }
            var reserved = r.ReadInt64();
            var hasCase = r.ReadBoolean();
            var caseByte = r.ReadByte();
            CaseStatus? caseStatus = hasCase ? ReadEnum<CaseStatus>(caseByte) : caseByte == 0 ? null : throw new CanonicalFormatException("Absent case with a value.");
            var payment = new LedgerPayment(intent, intentFact, velocity, status, reason, trail.MoveToImmutable(), reserved, caseStatus,
                r.ReadInt32(), r.ReadInt32(), r.ReadBoolean(), r.ReadBoolean());
            if (!payments.TryAdd(intent.IntentId, payment))
            {
                throw new CanonicalFormatException($"Duplicate payment '{intent.IntentId}'.");
            }
        }
        var windows = ImmutableSortedDictionary.CreateBuilder<string, ImmutableArray<long>>(StringComparer.Ordinal);
        count = r.ReadCount();
        for (var i = 0; i < count; i++)
        {
            var account = r.ReadString();
            var n = r.ReadCount();
            var times = ImmutableArray.CreateBuilder<long>(n);
            for (var t = 0; t < n; t++)
            {
                times.Add(r.ReadInt64());
            }
            if (!windows.TryAdd(account, times.MoveToImmutable()))
            {
                throw new CanonicalFormatException($"Duplicate velocity window '{account}'.");
            }
        }
        r.EnsureEnd();
        return new LedgerFinanceState(runId, scenario, manifest, currency, clock, lastIntent, lastFact,
            accounts.ToImmutable(), payments.ToImmutable(), windows.ToImmutable());
    }

    private static T ReadEnum<T>(byte value) where T : struct, Enum
    {
        var e = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(e) ? e : throw new CanonicalFormatException($"Unknown {typeof(T).Name} value {value}.");
    }
}

/// <summary>
/// Pinned Timeline version identity of durable (V2) Revenant finance histories and the components registered for it
/// (ADR-P1-17). It is a separate engine shape beside <see cref="FinanceTimelineVersions"/>; V1 stays registered there, unchanged.
/// A fork of a V2 source inherits this key from the source; no context substitution can switch it.
/// </summary>
public static class FinanceLedgerTimelineVersions
{
    /// <summary>Complete checkpoint spacing for V2 histories (one fact per position).</summary>
    public const int CheckpointInterval = 32;

    public static TimelineVersionKey V2 { get; } = new(
        FinanceLedgerVersion.DomainId, FinanceLedgerVersion.ReducerVersion, FinanceLedgerVersion.InputSchemaVersion,
        FinanceLedgerVersion.StateSchemaVersion, FinanceLedgerVersion.ContextSchemaVersion);

    public static TimelineComponents<LedgerFinanceState, FinanceFact, LedgerFinanceContext> V2Components { get; } = new(
        V2, FinanceLedgerTransition.Instance, LedgerFinanceStateCodec.Instance, FinanceFactInputCodec.Instance, LedgerFinanceContextCodec.Instance);

    /// <summary>Every V2-shape version a stored durable finance history may pin. Keep old entries when adding new ones.</summary>
    public static IReadOnlyList<TimelineComponents<LedgerFinanceState, FinanceFact, LedgerFinanceContext>> All { get; } = [V2Components];
}
