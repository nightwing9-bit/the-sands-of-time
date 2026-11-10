using Marrow.Kernel.Results;

namespace Marrow.Timeline.Internal;

/// <summary>
/// Restore and replay over the persisted journal. Every history segment (a source, or a child's own suffix)
/// is reconstructed with <b>that segment's</b> pinned reducer/codecs (resolved by its manifest version key)
/// and its durable pinned context bytes; nothing falls back to "current" settings.
/// <list type="bullet">
/// <item><b>Restore(T)</b>: nearest own complete checkpoint of the segment that owns T, record chain verified
/// from that checkpoint's record through T, deterministic fold, and every folded state hash compared with the
/// hash recorded in the step. A base checkpoint is additionally bound to its genesis record.</item>
/// <item><b>Replay</b>: from position 0 of the root source through every ancestor segment to the history's last
/// position, verifying each genesis record, each own record chain and the manifest terminal hashes.</item>
/// </list>
/// Integrity failures of stored bytes/hashes are <see cref="TimelineErrorCode.CorruptHistory"/>; a missing or
/// non-round-tripping reducer/codec/context is <see cref="TimelineErrorCode.IncompatibleVersion"/>.
/// Reads use a fixed number of store queries per segment (no per-step I/O).
/// </summary>
internal sealed class TimelineReconstructor<TState, TInput, TContext>
{
    private delegate T SpanDecoder<out T>(ReadOnlySpan<byte> bytes);

    private readonly ITimelineStore _store;
    private readonly ITimelineVersionResolver<TState, TInput, TContext> _resolver;
    private readonly int _maxAncestorDepth;

    public TimelineReconstructor(
        ITimelineStore store, ITimelineVersionResolver<TState, TInput, TContext> resolver, int maxAncestorDepth)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAncestorDepth, 1);
        _maxAncestorDepth = maxAncestorDepth;
    }

    /// <summary>A history segment with its resolved historical components and decoded pinned context.</summary>
    public sealed record Segment(StoredHistory History, TimelineComponents<TState, TInput, TContext> Components, TContext Context);

    /// <summary>Verified state at <see cref="Position"/> plus the record hash that ends the verified chain there.</summary>
    public sealed record Reconstruction(long Position, TState State, string StateHash, StateHashOrigin Origin, string RecordHash);

    /// <summary>Restores the state after exactly steps 1..<paramref name="position"/> of <paramref name="history"/> (bounds checked by caller).</summary>
    public async Task<Result<Reconstruction, TimelineError>> RestoreAsync(StoredHistory history, long position, CancellationToken ct)
    {
        var owner = await FindOwningSegmentAsync(history, position, ct);
        if (!owner.TryGetValue(out var owning))
        {
            return Fail<Reconstruction>(owner.Error);
        }

        var segment = await LoadSegmentAsync(owning, ct);
        return segment.TryGetValue(out var loaded)
            ? await RestoreWithinAsync(loaded, position, ct)
            : Fail<Reconstruction>(segment.Error);
    }

    /// <summary>Restores inside an already loaded segment; requires <c>BasePosition &lt;= position &lt;= LastPosition</c>.</summary>
    public async Task<Result<Reconstruction, TimelineError>> RestoreWithinAsync(Segment segment, long position, CancellationToken ct)
    {
        var h = segment.History;
        var m = h.Manifest;
        var checkpointResult = await _store.ReadNearestCheckpointAsync(m.Id, position, ct);
        if (!checkpointResult.TryGetValue(out var checkpoint))
        {
            return Fail<Reconstruction>(checkpointResult.Error);
        }

        if (checkpoint.Position < h.BasePosition || checkpoint.Position > position)
        {
            return Corrupt<Reconstruction>($"Checkpoint {checkpoint.Position} is outside {h.BasePosition}..{position}.");
        }

        IReadOnlyList<StoredStep> toFold;
        string checkpointRecordHash;
        if (checkpoint.Position == h.BasePosition)
        {
            var genesis = await VerifyGenesisAsync(h, ct);
            if (genesis is not null)
            {
                return Fail<Reconstruction>(genesis);
            }

            if (!Same(checkpoint.StateHash, h.BaseStateHash))
            {
                return Corrupt<Reconstruction>($"Base checkpoint {checkpoint.Position} does not match the genesis state hash.");
            }

            var steps = await _store.ReadStepsAsync(m.Id, h.BasePosition, position, ct);
            if (!steps.TryGetValue(out var own))
            {
                return Fail<Reconstruction>(steps.Error);
            }

            var chain = TimelineHashing.VerifyChain(h.BasePosition, h.BaseRecordHash, own);
            if (chain is not null)
            {
                return Fail<Reconstruction>(chain);
            }

            toFold = own;
            checkpointRecordHash = h.BaseRecordHash;
        }
        else
        {
            // Include the checkpoint's own step so the checkpoint is bound to its chain-protected recorded hash.
            var steps = await _store.ReadStepsAsync(m.Id, checkpoint.Position - 1, position, ct);
            if (!steps.TryGetValue(out var own))
            {
                return Fail<Reconstruction>(steps.Error);
            }

            if (own.Count == 0 || own[0].Position != checkpoint.Position)
            {
                return Corrupt<Reconstruction>($"Recorded step {checkpoint.Position} for its checkpoint is missing.");
            }

            var chain = TimelineHashing.VerifyChain(checkpoint.Position - 1, own[0].PreviousRecordHash, own);
            if (chain is not null)
            {
                return Fail<Reconstruction>(chain);
            }

            if (!Same(checkpoint.StateHash, own[0].StateHash))
            {
                return Corrupt<Reconstruction>($"Checkpoint {checkpoint.Position} does not match the recorded step state hash.");
            }

            toFold = own.Skip(1).ToArray();
            checkpointRecordHash = own[0].RecordHash;
        }

        var decoded = DecodeCanonical(
            segment.Components.StateCodec.Decode, segment.Components.StateCodec.EncodeCanonical,
            checkpoint.CanonicalState, $"checkpoint {checkpoint.Position} state", m.StateSchemaVersion);
        if (!decoded.TryGetValue(out var start))
        {
            return Fail<Reconstruction>(decoded.Error);
        }

        var folded = Fold(segment, start, checkpoint.StateHash, checkpoint.Position, toFold, ct);
        if (!folded.TryGetValue(out var end))
        {
            return Fail<Reconstruction>(folded.Error);
        }

        var recordHash = toFold.Count == 0 ? checkpointRecordHash : toFold[^1].RecordHash;
        if (position == m.LastPosition && !(Same(recordHash, m.LastRecordHash) && Same(end.Hash, m.LatestStateHash)))
        {
            return Corrupt<Reconstruction>("Manifest terminal hashes do not match the stored record chain.");
        }

        var origin = position == checkpoint.Position ? StateHashOrigin.StoredCheckpoint : StateHashOrigin.ReconstructedFromVerifiedChain;
        return TimelineResult.Ok(new Reconstruction(position, end.State, end.Hash, origin, recordHash));
    }

    /// <summary>Full replay from position 0 of the root source through the last position of <paramref name="history"/>.</summary>
    public async Task<Result<Reconstruction, TimelineError>> ReplayAsync(StoredHistory history, CancellationToken ct)
    {
        // Segments from the requested history up to the root, each with the last position it contributes.
        var path = new List<(StoredHistory Segment, long Through)>();
        var current = history;
        var through = history.Manifest.LastPosition;
        while (true)
        {
            path.Add((current, through));
            if (current.Manifest.ParentId is not { } parentId)
            {
                break;
            }

            if (path.Count > _maxAncestorDepth)
            {
                return Corrupt<Reconstruction>("Fork ancestry exceeds the supported depth or is cyclic.");
            }

            var parent = await GetAncestorAsync(parentId, ct);
            if (!parent.TryGetValue(out var p))
            {
                return Fail<Reconstruction>(parent.Error);
            }

            through = current.BasePosition;
            var owner = await FindOwningSegmentAsync(p, through, ct);
            if (!owner.TryGetValue(out var owning))
            {
                return Fail<Reconstruction>(owner.Error);
            }

            current = owning;
        }

        path.Reverse();
        TState state = default!;
        var stateHash = "";
        var recordHash = TimelineHashing.GenesisAnchor;
        for (var i = 0; i < path.Count; i++)
        {
            var (h, last) = path[i];
            var segmentResult = await LoadSegmentAsync(h, ct);
            if (!segmentResult.TryGetValue(out var segment))
            {
                return Fail<Reconstruction>(segmentResult.Error);
            }

            var genesis = VerifyGenesis(h, recordHash);
            if (genesis is not null)
            {
                return Fail<Reconstruction>(genesis);
            }

            var baseCheckpoint = await _store.ReadNearestCheckpointAsync(h.Manifest.Id, h.BasePosition, ct);
            if (!baseCheckpoint.TryGetValue(out var checkpoint))
            {
                return Fail<Reconstruction>(baseCheckpoint.Error);
            }

            if (checkpoint.Position != h.BasePosition || !Same(checkpoint.StateHash, h.BaseStateHash))
            {
                return Corrupt<Reconstruction>($"Base checkpoint of history {h.Manifest.Id.Value} does not match its genesis record.");
            }

            if (i == 0)
            {
                var decoded = DecodeCanonical(
                    segment.Components.StateCodec.Decode, segment.Components.StateCodec.EncodeCanonical,
                    checkpoint.CanonicalState, "initial state", h.Manifest.StateSchemaVersion);
                if (!decoded.TryGetValue(out var initial))
                {
                    return Fail<Reconstruction>(decoded.Error);
                }

                state = initial;
                stateHash = checkpoint.StateHash;
            }
            else if (!Same(stateHash, h.BaseStateHash))
            {
                return Corrupt<Reconstruction>(
                    $"Replayed parent state at fork position {h.BasePosition} differs from the child's recorded base state.");
            }

            var steps = await _store.ReadStepsAsync(h.Manifest.Id, h.BasePosition, last, ct);
            if (!steps.TryGetValue(out var own))
            {
                return Fail<Reconstruction>(steps.Error);
            }

            var chain = TimelineHashing.VerifyChain(h.BasePosition, h.BaseRecordHash, own);
            if (chain is not null)
            {
                return Fail<Reconstruction>(chain);
            }

            var folded = Fold(segment, state, stateHash, h.BasePosition, own, ct);
            if (!folded.TryGetValue(out var end))
            {
                return Fail<Reconstruction>(folded.Error);
            }

            state = end.State;
            stateHash = end.Hash;
            recordHash = own.Count == 0 ? h.BaseRecordHash : own[^1].RecordHash;
        }

        var m = history.Manifest;
        if (!Same(recordHash, m.LastRecordHash) || !Same(stateHash, m.LatestStateHash))
        {
            return Corrupt<Reconstruction>("Manifest terminal hashes do not match the replayed record chain.");
        }

        var origin = m.LastPosition == history.BasePosition ? StateHashOrigin.StoredCheckpoint : StateHashOrigin.ReconstructedFromVerifiedChain;
        return TimelineResult.Ok(new Reconstruction(m.LastPosition, state, stateHash, origin, recordHash));
    }

    /// <summary>Resolves the segment's pinned reducer/codecs and loads, verifies and decodes its pinned context.</summary>
    public async Task<Result<Segment, TimelineError>> LoadSegmentAsync(StoredHistory history, CancellationToken ct)
    {
        var m = history.Manifest;
        var resolved = _resolver.Resolve(m.Versions);
        if (!resolved.TryGetValue(out var components))
        {
            return Fail<Segment>(resolved.Error);
        }

        if (components.Versions != m.Versions)
        {
            return Fail<Segment>(new(TimelineErrorCode.IncompatibleVersion,
                $"Resolver returned components for {components.Versions} instead of pinned {m.Versions}."));
        }

        var stored = await _store.ReadContextAsync(m.ContextArtifactRef, ct);
        if (!stored.TryGetValue(out var artifact))
        {
            return Fail<Segment>(stored.Error);
        }

        if (!Same(artifact.ContextSchemaVersion, m.ContextSchemaVersion) || !Same(artifact.ContextHash, m.ContextHash))
        {
            return Corrupt<Segment>($"Pinned context artifact {m.ContextArtifactRef} does not match the manifest pin.");
        }

        var context = DecodeCanonical(
            components.ContextCodec.Decode, components.ContextCodec.EncodeCanonical,
            artifact.CanonicalContext, "pinned context", m.ContextSchemaVersion);
        return context.TryGetValue(out var decoded)
            ? TimelineResult.Ok(new Segment(history, components, decoded))
            : Fail<Segment>(context.Error);
    }

    /// <summary>Walks from <paramref name="start"/> to the ancestor segment whose own range contains <paramref name="position"/>.</summary>
    public async Task<Result<StoredHistory, TimelineError>> FindOwningSegmentAsync(StoredHistory start, long position, CancellationToken ct)
    {
        var segment = start;
        for (var depth = 0; position < segment.BasePosition; depth++)
        {
            if (depth >= _maxAncestorDepth || segment.Manifest.ParentId is not { } parentId)
            {
                return Corrupt<StoredHistory>("Fork ancestry is malformed, cyclic or deeper than supported.");
            }

            var parent = await GetAncestorAsync(parentId, ct);
            if (!parent.TryGetValue(out var p))
            {
                return parent;
            }

            if (p.Manifest.LastPosition < segment.BasePosition)
            {
                return Corrupt<StoredHistory>($"Ancestor {parentId.Value} ends before fork position {segment.BasePosition}.");
            }

            segment = p;
        }

        return TimelineResult.Ok(segment);
    }

    /// <summary>
    /// Follows parent links to the root source. Not <c>FindOwningSegmentAsync(h, 0)</c>: a child forked at
    /// position 0 owns its own base position 0, but its root is still the parent source.
    /// </summary>
    public async Task<Result<TimelineId, TimelineError>> FindRootAsync(StoredHistory start, CancellationToken ct)
    {
        var segment = start;
        for (var depth = 0; segment.Manifest.ParentId is { } parentId; depth++)
        {
            if (depth >= _maxAncestorDepth)
            {
                return Corrupt<TimelineId>("Fork ancestry is cyclic or deeper than supported.");
            }

            var parent = await GetAncestorAsync(parentId, ct);
            if (!parent.TryGetValue(out var p))
            {
                return Fail<TimelineId>(parent.Error);
            }

            segment = p;
        }

        return TimelineResult.Ok(segment.Manifest.Id);
    }

    /// <summary>
    /// Folds <paramref name="steps"/> (verified, contiguous after <paramref name="startPosition"/>) with the segment's
    /// pinned reducer and context, comparing each resulting state hash with the recorded step hash.
    /// Cancellation is observed before every step.
    /// </summary>
    private static Result<(TState State, string Hash), TimelineError> Fold(
        Segment segment, TState start, string startHash, long startPosition, IReadOnlyList<StoredStep> steps, CancellationToken ct)
    {
        var c = segment.Components;
        var schema = segment.History.Manifest.StateSchemaVersion;
        var state = start;
        var hash = startHash;
        var expected = startPosition + 1;
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (step.Position != expected)
            {
                return Corrupt<(TState, string)>($"Stored step {step.Position} found where {expected} was expected.");
            }

            var inputs = DecodeInputs(c, step, segment.History.Manifest.InputSchemaVersion);
            if (!inputs.TryGetValue(out var ordered))
            {
                return Fail<(TState, string)>(inputs.Error);
            }

            state = c.Transition.Apply(state, step.Position, ordered, segment.Context);
            hash = TimelineHashing.StateHash(schema, c.StateCodec.EncodeCanonical(state));
            if (!Same(hash, step.StateHash))
            {
                return Corrupt<(TState, string)>(
                    $"Replaying step {step.Position} with reducer '{c.Versions.ReducerVersion}' does not reproduce the recorded state hash.");
            }

            expected++;
        }

        return TimelineResult.Ok((state, hash));
    }

    /// <summary>Decodes stored inputs with the pinned input codec, requiring the canonical (Ordinal, InputId) order.</summary>
    private static Result<IReadOnlyList<OrderedInput<TInput>>, TimelineError> DecodeInputs(
        TimelineComponents<TState, TInput, TContext> c, StoredStep step, string inputSchemaVersion)
    {
        var result = new OrderedInput<TInput>[step.Inputs.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var stored = step.Inputs[i];
            if (i > 0 && TimelineRules.CompareInputs(step.Inputs[i - 1], stored) >= 0)
            {
                return Corrupt<IReadOnlyList<OrderedInput<TInput>>>(
                    $"Stored inputs of step {step.Position} are not in strict (Ordinal, InputId) order.");
            }

            var payload = DecodeCanonical(c.InputCodec.Decode, c.InputCodec.EncodeCanonical, stored.Payload,
                $"input '{stored.InputId}' of step {step.Position}", inputSchemaVersion);
            if (!payload.TryGetValue(out var value))
            {
                return Fail<IReadOnlyList<OrderedInput<TInput>>>(payload.Error);
            }

            result[i] = new OrderedInput<TInput>(stored.InputId, stored.Ordinal, value);
        }

        return TimelineResult.Ok<IReadOnlyList<OrderedInput<TInput>>>(result);
    }

    /// <summary>
    /// Decodes integrity-verified bytes with a pinned codec and requires that re-encoding reproduces them exactly.
    /// A throwing or lossy codec means the registered implementation cannot read this history: IncompatibleVersion.
    /// </summary>
    private static Result<T, TimelineError> DecodeCanonical<T>(
        SpanDecoder<T> decode, Func<T, byte[]> encode, byte[] bytes, string what, string schemaVersion)
    {
        T value;
        try
        {
            value = decode(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail<T>(new(TimelineErrorCode.IncompatibleVersion,
                $"Registered codec for schema '{schemaVersion}' cannot decode the stored {what} ({ex.GetType().Name})."));
        }

        return encode(value).AsSpan().SequenceEqual(bytes)
            ? TimelineResult.Ok(value)
            : Fail<T>(new(TimelineErrorCode.IncompatibleVersion,
                $"Registered codec for schema '{schemaVersion}' does not round-trip the stored {what} canonically."));
    }

    /// <summary>Recomputes the history's genesis record from its manifest pins and the anchoring parent record.</summary>
    private async Task<TimelineError?> VerifyGenesisAsync(StoredHistory history, CancellationToken ct)
    {
        if (history.Manifest.ParentId is not { } parentId)
        {
            return VerifyGenesis(history, TimelineHashing.GenesisAnchor);
        }

        var parent = await GetAncestorAsync(parentId, ct);
        if (!parent.TryGetValue(out var p))
        {
            return parent.Error;
        }

        var owner = await FindOwningSegmentAsync(p, history.BasePosition, ct);
        if (!owner.TryGetValue(out var segment))
        {
            return owner.Error;
        }

        string anchor;
        if (history.BasePosition == segment.BasePosition)
        {
            anchor = segment.BaseRecordHash;
        }
        else
        {
            var step = await _store.ReadStepsAsync(segment.Manifest.Id, history.BasePosition - 1, history.BasePosition, ct);
            if (!step.TryGetValue(out var record))
            {
                return step.Error;
            }

            if (record.Count != 1)
            {
                return new(TimelineErrorCode.CorruptHistory, $"Parent record at fork position {history.BasePosition} is missing.");
            }

            anchor = record[0].RecordHash;
        }

        return VerifyGenesis(history, anchor);
    }

    private static TimelineError? VerifyGenesis(StoredHistory history, string anchorRecordHash)
    {
        var m = history.Manifest;
        var shapeOk = m.ParentId is null
            ? m.ForkPosition is null && history.BasePosition == 0
            : m.ForkPosition == history.BasePosition;
        if (!shapeOk || !TimelineHashing.IsHash(anchorRecordHash)
            || !Same(m.ContextArtifactRef, TimelineHashing.ContextArtifactRef(m.ContextHash)))
        {
            return new(TimelineErrorCode.CorruptHistory, $"History {m.Id.Value} manifest pins are malformed.");
        }

        var expected = TimelineHashing.ChainHash(anchorRecordHash, TimelineHashing.GenesisEnvelope(
            m.Id, m.Versions, m.ContextHash, m.RulesHash, m.SeedHash, m.ParentId, history.BasePosition, history.BaseStateHash));
        return Same(expected, history.BaseRecordHash)
            ? null
            : new(TimelineErrorCode.CorruptHistory, $"History {m.Id.Value} genesis record does not match its manifest pins.");
    }

    private async Task<Result<StoredHistory, TimelineError>> GetAncestorAsync(TimelineId id, CancellationToken ct)
    {
        var result = await _store.GetHistoryAsync(id, ct);
        return result.IsFailure && result.Error.Code == TimelineErrorCode.NotFound
            ? Corrupt<StoredHistory>($"Ancestor history {id.Value} is missing.")
            : result;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    private static Result<T, TimelineError> Fail<T>(TimelineError error) => TimelineResult.Fail<T>(error);

    private static Result<T, TimelineError> Corrupt<T>(string message) =>
        TimelineResult.Fail<T>(TimelineErrorCode.CorruptHistory, message);
}
