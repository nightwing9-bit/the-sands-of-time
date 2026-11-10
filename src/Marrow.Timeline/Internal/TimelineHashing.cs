using System.Security.Cryptography;
using Marrow.Kernel.Canonical;

namespace Marrow.Timeline.Internal;

/// <summary>
/// The one definition of Timeline hashes (SHA-256, lowercase hex). Used by the persistence adapter
/// when writing and by the engine when verifying, so both sides hash identical canonical bytes.
/// <list type="bullet">
/// <item>State hash: <c>H(stateSchemaVersion || canonicalStateBytes)</c>.</item>
/// <item>Context hash: <c>H(contextSchemaVersion || canonicalContextBytes)</c>; artifact ref is <c>sha256:&lt;hash&gt;</c>.</item>
/// <item>Record chain: <c>H(previousRecordHash || canonicalEnvelope)</c>, separate from state hashes.</item>
/// </list>
/// Each <c>||</c> part is length-prefixed by <see cref="CanonicalWriter"/>, except the fixed 32-byte previous hash.
/// A database-local chain is tamper-evidence only, not external notarization.
/// </summary>
internal static class TimelineHashing
{
    public const string ContextRefPrefix = "sha256:";

    /// <summary>Anchor "previous hash" of a source genesis record (32 zero bytes).</summary>
    public static readonly string GenesisAnchor = new('0', 64);

    public static string StateHash(string stateSchemaVersion, ReadOnlySpan<byte> canonicalState) =>
        Sha256Hex(new CanonicalWriter().WriteString(stateSchemaVersion).WriteBytes(canonicalState).ToArray());

    public static string ContextHash(string contextSchemaVersion, ReadOnlySpan<byte> canonicalContext) =>
        Sha256Hex(new CanonicalWriter().WriteString(contextSchemaVersion).WriteBytes(canonicalContext).ToArray());

    public static string ContextArtifactRef(string contextHash) => ContextRefPrefix + contextHash;

    public static bool IsHash(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Canonical envelope of one appended step: position, inputs in the canonical (Ordinal, InputId)
    /// order with their canonical payload bytes, and the state hash after applying the step.
    /// </summary>
    public static byte[] StepEnvelope(long position, IReadOnlyList<OrderedInput<byte[]>> canonicalInputs, string stateHashAfter)
    {
        ArgumentNullException.ThrowIfNull(canonicalInputs);
        var writer = new CanonicalWriter()
            .WriteString("marrow.step.v1")
            .WriteInt64(position)
            .WriteCount(canonicalInputs.Count);
        foreach (var input in canonicalInputs)
        {
            writer.WriteString(input.InputId).WriteInt32(input.Ordinal).WriteBytes(input.Payload);
        }

        return writer.WriteString(stateHashAfter).ToArray();
    }

    /// <summary>
    /// Canonical envelope of a history's base record (position 0 for a source, T for a fork). Binds the
    /// identity, pinned versions, context, rules, seed and complete base state hash into the chain.
    /// </summary>
    public static byte[] GenesisEnvelope(
        TimelineId id, TimelineVersionKey versions, string contextHash, string rulesHash, string seedHash,
        TimelineId? parentId, long basePosition, string baseStateHash)
    {
        ArgumentNullException.ThrowIfNull(versions);
        return new CanonicalWriter()
            .WriteString("marrow.genesis.v1")
            .WriteGuid(id.Value)
            .WriteString(versions.DomainId)
            .WriteString(versions.ReducerVersion)
            .WriteString(versions.InputSchemaVersion)
            .WriteString(versions.StateSchemaVersion)
            .WriteString(versions.ContextSchemaVersion)
            .WriteString(contextHash)
            .WriteString(rulesHash)
            .WriteString(seedHash)
            .WriteBoolean(parentId.HasValue)
            .WriteGuid(parentId?.Value ?? Guid.Empty)
            .WriteInt64(basePosition)
            .WriteString(baseStateHash)
            .ToArray();
    }

    /// <summary><c>H(previousRecordHash || envelope)</c> with the previous hash as its raw 32 bytes.</summary>
    public static string ChainHash(string previousRecordHash, ReadOnlySpan<byte> envelope)
    {
        if (!IsHash(previousRecordHash))
        {
            throw new ArgumentException("Previous record hash must be 64 lowercase hex characters.", nameof(previousRecordHash));
        }

        var buffer = new byte[32 + envelope.Length];
        Convert.FromHexString(previousRecordHash).CopyTo(buffer, 0);
        envelope.CopyTo(buffer.AsSpan(32));
        return Sha256Hex(buffer);
    }

    /// <summary>
    /// Verifies a history's own stored suffix: contiguous positions basePosition+1.., each previous hash
    /// linking to the prior record (starting at <paramref name="baseRecordHash"/>) and each record hash
    /// recomputed from its canonical envelope. Returns the error, or null when the chain is intact.
    /// </summary>
    public static TimelineError? VerifyChain(long basePosition, string baseRecordHash, IReadOnlyList<StoredStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var expectedPrevious = baseRecordHash;
        var expectedPosition = basePosition + 1;
        foreach (var step in steps)
        {
            if (step.Position != expectedPosition)
            {
                return new(TimelineErrorCode.CorruptHistory, $"Stored step {step.Position} found where {expectedPosition} was expected.");
            }

            if (!string.Equals(step.PreviousRecordHash, expectedPrevious, StringComparison.Ordinal))
            {
                return new(TimelineErrorCode.CorruptHistory, $"Step {step.Position} does not link to the previous record.");
            }

            var recomputed = ChainHash(step.PreviousRecordHash, StepEnvelope(step.Position, step.Inputs, step.StateHash));
            if (!string.Equals(recomputed, step.RecordHash, StringComparison.Ordinal))
            {
                return new(TimelineErrorCode.CorruptHistory, $"Step {step.Position} record hash does not match its stored content.");
            }

            expectedPrevious = step.RecordHash;
            expectedPosition++;
        }

        return null;
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
