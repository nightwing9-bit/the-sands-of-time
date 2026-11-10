using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Marrow.Timeline.Internal;

namespace Marrow.Tests.Timeline.Persistence;

/// <summary>Byte-level store inputs. The store never decodes domain payloads, so tests use small canonical blobs.</summary>
internal static class StoreData
{
    public static readonly TimelineVersionKey V1 = new("test.store", "reducer-1", "input-1", "state-1", "context-1");

    public static byte[] State(long value) => new CanonicalWriter().WriteInt64(value).ToArray();

    public static string StateHash(long value) => TimelineHashing.StateHash(V1.StateSchemaVersion, State(value));

    public static byte[] Context(int value) => new CanonicalWriter().WriteInt32(value).ToArray();

    public static byte[] Payload(string text) => new CanonicalWriter().WriteString(text).ToArray();

    public static string Key() => Guid.NewGuid().ToString("N");

    public static NewHistory Source(string? key = null, string owner = "owner-a", int context = 7) =>
        new(owner, V1, Context(context), "rules-1", "seed-1", State(0), key ?? Key(), null);

    public static NewHistory Fork(TimelineId parent, long position, string? key = null, int context = 8, long? baseState = null) =>
        new("owner-a", V1, Context(context), "rules-1", "seed-1", State(baseState ?? position), key ?? Key(),
            new ForkOrigin(parent, position));

    /// <summary>Next step after <paramref name="expectedLast"/>; its state "value" is the position (state hash of State(position)).</summary>
    public static NewStep Step(TimelineId id, long expectedLast, string? key = null, params (string Id, int Ordinal)[] inputs) =>
        StepAt(id, expectedLast, expectedLast + 1, key, inputs);

    public static NewStep StepAt(TimelineId id, long expectedLast, long position, string? key = null, params (string Id, int Ordinal)[] inputs) =>
        new(id, expectedLast,
            new StepRecord<byte[]>(position, inputs.Select(i => new OrderedInput<byte[]>(i.Id, i.Ordinal, Payload(i.Id))).ToList()),
            StateHash(position), null, key ?? Key());
}
