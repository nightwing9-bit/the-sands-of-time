using System.Collections.Immutable;
using Marrow.Kernel.Canonical;
using Revenant.Observations;

namespace Revenant.Intelligence;

internal enum RiskUnitV2 : byte { Flag = 1, Count, MinorOver256, Ppm, Seconds, Usd }
internal readonly record struct RiskColumnV2(string Name, RiskUnitV2 Unit)
{
    internal int Minimum => Unit == RiskUnitV2.Usd ? 1 : 0;
    internal int Maximum => Unit switch
    {
        RiskUnitV2.Flag or RiskUnitV2.Usd => 1,
        RiskUnitV2.Seconds => 86_400,
        _ => RiskFeatureVectorV2.MaxValue,
    };
}

internal sealed class RiskFeatureVectorV2
{
    internal const string Schema = "risk-features-v2";
    internal const int MaxValue = 8_388_607;
    internal const int CurrentCount = 8;
    internal static readonly ImmutableArray<RiskColumnV2> Columns =
    [
        new("amount", RiskUnitV2.MinorOver256), new("currency_usd", RiskUnitV2.Usd),
        new("amount_to_available", RiskUnitV2.Ppm), new("posted", RiskUnitV2.MinorOver256),
        new("reserved", RiskUnitV2.MinorOver256), new("available", RiskUnitV2.MinorOver256),
        new("reported_new_beneficiary", RiskUnitV2.Flag), new("zero_available", RiskUnitV2.Flag),
        new("beneficiary_seen", RiskUnitV2.Flag),
        new("counterparty_seen", RiskUnitV2.Flag), new("outgoing_age", RiskUnitV2.Seconds),
        new("outgoing_missing", RiskUnitV2.Flag), new("amount_to_mean_submitted_24h", RiskUnitV2.Ppm),
        new("submitted_24h_missing", RiskUnitV2.Flag),
        .. new[] { "5m", "30m", "60m", "24h" }.SelectMany(suffix => new RiskColumnV2[]
        {
            new("complete_" + suffix, RiskUnitV2.Flag),
            new("submitted_count_" + suffix, RiskUnitV2.Count), new("submitted_" + suffix, RiskUnitV2.MinorOver256),
            new("settled_out_count_" + suffix, RiskUnitV2.Count), new("settled_out_" + suffix, RiskUnitV2.MinorOver256),
            new("settled_in_count_" + suffix, RiskUnitV2.Count), new("settled_in_" + suffix, RiskUnitV2.MinorOver256),
            new("holds_" + suffix, RiskUnitV2.Count), new("returns_in_" + suffix, RiskUnitV2.Count),
            new("returns_out_" + suffix, RiskUnitV2.Count), new("counterparties_" + suffix, RiskUnitV2.Count),
            new("beneficiaries_" + suffix, RiskUnitV2.Count),
        }),
        new("incoming_age", RiskUnitV2.Seconds), new("incoming_missing", RiskUnitV2.Flag),
        new("destination_in_count_30m", RiskUnitV2.Count), new("destination_in_senders_30m", RiskUnitV2.Count),
        new("destination_in_30m", RiskUnitV2.MinorOver256), new("source_cross_bank_settled_share_30m", RiskUnitV2.Ppm),
        new("forwarded_principal_match_30m", RiskUnitV2.Ppm), new("forwarded_principal_missing_30m", RiskUnitV2.Flag),
    ];
    internal const int AccountCount = 62;
    internal static readonly string SchemaHash = ObservationEncoding.Hash(EncodeSchema());
    internal ImmutableArray<int> Values { get; }
    internal string Digest => ObservationEncoding.Hash(Encode());

    internal RiskFeatureVectorV2(ImmutableArray<int> values)
    {
        if (values.IsDefault || values.Length != Columns.Length)
            throw new CanonicalFormatException("Wrong risk feature count.");
        for (var i = 0; i < values.Length; i++)
            if (values[i] < Columns[i].Minimum || values[i] > Columns[i].Maximum)
                throw new CanonicalFormatException($"Invalid risk feature: {Columns[i].Name}.");
        Values = values;
    }

    internal static byte[] EncodeSchema()
    {
        var writer = ObservationEncoding.Begin(Schema)
            .WriteString("inclusive-trailing-window;prior-fact-sequence;candidate-excluded;usd-only;partial-history-with-coverage;null-zero-plus-indicator;floor-and-clip;int32-big-endian;exact-single")
            .WriteCount(Columns.Length);
        foreach (var column in Columns)
            writer.WriteString(column.Name).WriteByte((byte)column.Unit).WriteInt32(column.Minimum).WriteInt32(column.Maximum);
        return writer.ToArray();
    }

    internal byte[] Encode()
    {
        var writer = ObservationEncoding.Begin(Schema).WriteString(SchemaHash).WriteCount(Values.Length);
        foreach (var value in Values) writer.WriteInt32(value);
        return writer.ToArray();
    }

    internal static RiskFeatureVectorV2 Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        ObservationEncoding.Expect(ref r, Schema);
        if (r.ReadString() != SchemaHash || r.ReadInt32() != Columns.Length)
            throw new CanonicalFormatException("Incompatible risk feature schema or count.");
        var values = ImmutableArray.CreateBuilder<int>(Columns.Length);
        for (var i = 0; i < Columns.Length; i++) values.Add(r.ReadInt32());
        r.EnsureEnd();
        return new(values.MoveToImmutable());
    }

    internal float[] ToModelInput() => Values.Select(n => (float)n).ToArray();

    internal static int Count(long n) => Clip(n);
    internal static int Minor(long n) => Clip(n / 256);
    internal static int Flag(bool b) => b ? 1 : 0;
    internal static int Age(long? ms) => (int)Math.Min(86_400, (ms ?? 0) / 1_000);
    internal static int Ratio(Int128 numerator, long denominator) =>
        denominator == 0 ? 0 : Clip(numerator * 1_000_000 / denominator);
    private static int Clip(Int128 n) => n < 0 ? throw new CanonicalFormatException("Negative risk feature.")
        : (int)Int128.Min(n, MaxValue);
}
