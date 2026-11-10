using System.Collections.Immutable;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;

namespace Revenant.Timeline;

/// <summary>
/// Canonical complete-state codec for <see cref="FinanceProjectionState"/>, state schema
/// <see cref="FinanceTimelineVersions.StateSchemaVersion"/>. It encodes every simulation-significant field:
/// run identity and pins, clock and last sequence, every account position, every payment record (exogenous
/// intent bytes plus the decision evidence of the branch that produced it) and every payer velocity window.
/// Maps are written in their ordinal key order. Unknown format, malformed or trailing bytes throw.
/// </summary>
public sealed class FinanceStateCodec : IStateCodec<FinanceProjectionState>
{
    public const int FormatVersion = 1;

    public static FinanceStateCodec Instance { get; } = new();

    public byte[] EncodeCanonical(FinanceProjectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var w = new CanonicalWriter()
            .WriteInt32(FormatVersion)
            .WriteGuid(state.RunId.Value)
            .WriteString(state.ScenarioVersion)
            .WriteString(state.ManifestContentHash)
            .WriteString(state.Currency)
            .WriteInt64(state.ClockUtcMs)
            .WriteInt64(state.LastSequence)
            .WriteCount(state.Accounts.Count);
        foreach (var a in state.Accounts.Values)
        {
            w.WriteString(a.AccountId).WriteString(a.BankId).WriteString(a.RunScopedKey)
                .WriteInt64(a.PostedMinor).WriteInt64(a.ReservedMinor);
        }

        w.WriteCount(state.Payments.Count);
        foreach (var p in state.Payments.Values)
        {
            w.WriteBytes(PaymentIntentCodec.Instance.EncodeCanonical(p.Intent))
                .WriteString(p.PolicyId)
                .WriteInt32(p.Velocity15m)
                .WriteByte((byte)p.Status)
                .WriteByte((byte)p.Reason)
                .WriteCount(p.Trail.Length);
            foreach (var s in p.Trail)
            {
                w.WriteByte((byte)s);
            }

            w.WriteInt64(p.ScreenedAtUtcMs);
            WriteOptional(w, p.HeldAtUtcMs);
            WriteOptional(w, p.SettledAtUtcMs);
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

    public FinanceProjectionState Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        var version = r.ReadInt32();
        if (version != FormatVersion)
        {
            throw new CanonicalFormatException($"Unsupported finance state format version {version}.");
        }

        var runId = new ScenarioRunId(r.ReadGuid());
        if (runId.IsEmpty)
        {
            throw new CanonicalFormatException("Finance state has an empty ScenarioRunId.");
        }

        var scenarioVersion = r.ReadString();
        var manifestHash = r.ReadString();
        var currency = r.ReadString();
        var clock = r.ReadInt64();
        var lastSequence = r.ReadInt64();

        var accounts = ImmutableSortedDictionary.CreateBuilder<string, AccountPosition>(StringComparer.Ordinal);
        var accountCount = r.ReadCount();
        for (var i = 0; i < accountCount; i++)
        {
            var account = new AccountPosition(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadInt64(), r.ReadInt64());
            if (!accounts.TryAdd(account.AccountId, account))
            {
                throw new CanonicalFormatException($"Duplicate account '{account.AccountId}'.");
            }
        }

        var payments = ImmutableSortedDictionary.CreateBuilder<string, PaymentRecord>(StringComparer.Ordinal);
        var paymentCount = r.ReadCount();
        for (var i = 0; i < paymentCount; i++)
        {
            var intent = PaymentIntentCodec.Instance.Decode(r.ReadBytes());
            if (intent.RunId != runId)
            {
                throw new CanonicalFormatException($"Payment '{intent.IntentId}' belongs to another run.");
            }

            var policyId = r.ReadString();
            var velocity = r.ReadInt32();
            var status = ReadEnum<PaymentStatus>(r.ReadByte());
            var reason = ReadEnum<PaymentReason>(r.ReadByte());
            var trailCount = r.ReadCount();
            var trail = ImmutableArray.CreateBuilder<PaymentStatus>(trailCount);
            for (var t = 0; t < trailCount; t++)
            {
                trail.Add(ReadEnum<PaymentStatus>(r.ReadByte()));
            }

            var screenedAt = r.ReadInt64();
            var heldAt = ReadOptional(ref r);
            var settledAt = ReadOptional(ref r);
            var record = new PaymentRecord(
                intent, policyId, velocity, status, reason, trail.MoveToImmutable(), screenedAt, heldAt, settledAt);
            if (!payments.TryAdd(intent.IntentId, record))
            {
                throw new CanonicalFormatException($"Duplicate payment '{intent.IntentId}'.");
            }
        }

        var windows = ImmutableSortedDictionary.CreateBuilder<string, ImmutableArray<long>>(StringComparer.Ordinal);
        var windowCount = r.ReadCount();
        for (var i = 0; i < windowCount; i++)
        {
            var account = r.ReadString();
            var count = r.ReadCount();
            var times = ImmutableArray.CreateBuilder<long>(count);
            for (var t = 0; t < count; t++)
            {
                times.Add(r.ReadInt64());
            }

            if (!windows.TryAdd(account, times.MoveToImmutable()))
            {
                throw new CanonicalFormatException($"Duplicate velocity window '{account}'.");
            }
        }

        r.EnsureEnd();
        return new FinanceProjectionState(
            runId, scenarioVersion, manifestHash, currency, clock, lastSequence,
            accounts.ToImmutable(), payments.ToImmutable(), windows.ToImmutable());
    }

    private static void WriteOptional(CanonicalWriter w, long? value) =>
        w.WriteBoolean(value.HasValue).WriteInt64(value ?? 0);

    private static long? ReadOptional(ref CanonicalReader r)
    {
        var has = r.ReadBoolean();
        var value = r.ReadInt64();
        if (!has && value != 0)
        {
            throw new CanonicalFormatException("Absent optional value must be encoded as zero.");
        }

        return has ? value : null;
    }

    private static T ReadEnum<T>(byte value)
        where T : struct, Enum
    {
        var e = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(e) ? e : throw new CanonicalFormatException($"Unknown {typeof(T).Name} value {value}.");
    }
}

/// <summary>
/// Canonical pinned-context codec, context schema <see cref="FinanceTimelineVersions.ContextSchemaVersion"/>.
/// The bytes are <see cref="FinanceContext.EncodeCanonical"/>; decoding re-resolves the exact pinned policy
/// and fails closed (throws) when this build cannot reproduce it, so cold restore never uses current defaults.
/// </summary>
public sealed class FinanceContextCodec : IContextCodec<FinanceContext>
{
    public const int FormatVersion = 1;

    public static FinanceContextCodec Instance { get; } = new();

    public byte[] EncodeCanonical(FinanceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.EncodeCanonical();
    }

    public FinanceContext Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        var version = r.ReadInt32();
        if (version != FormatVersion)
        {
            throw new CanonicalFormatException($"Unsupported finance context format version {version}.");
        }

        var scenarioVersion = r.ReadString();
        var manifestHash = r.ReadString();
        var inputSchema = r.ReadString();
        var featureSchema = r.ReadString();
        var reducerVersion = r.ReadString();
        var policy = new PolicyRef(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString());
        r.EnsureEnd();

        var pinned = FinanceContext.FromPins(scenarioVersion, manifestHash, inputSchema, featureSchema, reducerVersion, policy);
        return pinned.TryGetValue(out var context)
            ? context
            : throw new CanonicalFormatException($"Pinned finance context cannot be reproduced: {pinned.Error.Message}");
    }
}
