using Marrow.Kernel.Results;

namespace Marrow.Timeline.Internal;

/// <summary>Engine tuning. <see cref="CheckpointInterval"/> is per engine instance so each domain can set its own spacing.</summary>
internal sealed record TimelineEngineOptions
{
    /// <summary>A complete checkpoint is stored at every position divisible by this value (position 0 always has one).</summary>
    public int CheckpointInterval { get; init; } = 64;

    /// <summary>Upper bound on fork ancestry walked during restore/replay.</summary>
    public int MaxAncestorDepth { get; init; } = 64;
}

/// <summary>
/// <see cref="ITimelineEngine{TState,TInput,TContext}"/> over the internal <see cref="ITimelineStore"/>.
/// Create/append/complete/restore/replay (task 2.3) and fork/aligned compare (task 2.4). A child is continued
/// and completed with the same append/complete path as a source. Internal: hosts obtain it through Infrastructure composition.
/// The engine never reads clocks, randomness or ambient settings; reducers run on canonically decoded inputs.
/// </summary>
internal sealed class PersistentTimelineEngine<TState, TInput, TContext> : ITimelineEngine<TState, TInput, TContext>
{
    private readonly ITimelineStore _store;
    private readonly ITimelineVersionResolver<TState, TInput, TContext> _resolver;
    private readonly TimelineReconstructor<TState, TInput, TContext> _reconstructor;
    private readonly int _checkpointInterval;

    public PersistentTimelineEngine(
        ITimelineStore store, ITimelineVersionResolver<TState, TInput, TContext> resolver, TimelineEngineOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        options ??= new TimelineEngineOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CheckpointInterval, 1, nameof(options));
        _checkpointInterval = options.CheckpointInterval;
        _reconstructor = new(store, resolver, options.MaxAncestorDepth);
    }

    public Task<Result<TimelineId, TimelineError>> CreateSourceAsync(
        CreateSourceRequest<TState, TContext> source, string operationKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        return GuardAsync(async () =>
        {
            var resolved = _resolver.Resolve(source.Versions);
            if (!resolved.TryGetValue(out var c))
            {
                return TimelineResult.Fail<TimelineId>(resolved.Error);
            }

            var state = c.StateCodec.EncodeCanonical(source.InitialState);
            var context = c.ContextCodec.EncodeCanonical(source.PinnedContext);
            if (!RoundTrips(c.StateCodec.Decode, c.StateCodec.EncodeCanonical, state)
                || !RoundTrips(c.ContextCodec.Decode, c.ContextCodec.EncodeCanonical, context))
            {
                return TimelineResult.Fail<TimelineId>(TimelineErrorCode.IncompatibleVersion,
                    $"Codecs registered for {source.Versions} do not round-trip the initial state/context canonically.");
            }

            var created = await _store.CreateHistoryAsync(new NewHistory(
                source.OwnerId, source.Versions, context, source.RulesHash, source.SeedHash, state, operationKey, Fork: null), ct);
            return created.TryGetValue(out var history)
                ? TimelineResult.Ok(history.Manifest.Id)
                : TimelineResult.Fail<TimelineId>(created.Error);
        }, ct);
    }

    public Task<Result<AppendReceipt, TimelineError>> AppendAsync(
        TimelineId history, long expectedLastPosition, StepRecord<TInput> nextStep, string operationKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nextStep);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        return GuardAsync(async () =>
        {
            var canonical = TimelineRules.CanonicalizeNextStep(expectedLastPosition, nextStep);
            if (!canonical.TryGetValue(out var ordered))
            {
                return TimelineResult.Fail<AppendReceipt>(canonical.Error);
            }

            var stored = await _store.GetHistoryAsync(history, ct);
            if (!stored.TryGetValue(out var h))
            {
                return TimelineResult.Fail<AppendReceipt>(stored.Error);
            }

            // Retries of an already committed step pass an earlier expected position; the store resolves those by key.
            if (expectedLastPosition < h.BasePosition || expectedLastPosition > h.Manifest.LastPosition)
            {
                return TimelineResult.Fail<AppendReceipt>(TimelineErrorCode.Conflict,
                    $"Expected last position {expectedLastPosition} but history owns {h.BasePosition}..{h.Manifest.LastPosition}.");
            }

            var segmentResult = await _reconstructor.LoadSegmentAsync(h, ct);
            if (!segmentResult.TryGetValue(out var segment))
            {
                return TimelineResult.Fail<AppendReceipt>(segmentResult.Error);
            }

            var previous = await _reconstructor.RestoreWithinAsync(segment, expectedLastPosition, ct);
            if (!previous.TryGetValue(out var before))
            {
                return TimelineResult.Fail<AppendReceipt>(previous.Error);
            }

            // Apply the reducer to the canonically decoded inputs, exactly what a later replay will see.
            var c = segment.Components;
            var canonicalInputs = new OrderedInput<byte[]>[ordered.Count];
            var decodedInputs = new OrderedInput<TInput>[ordered.Count];
            for (var i = 0; i < ordered.Count; i++)
            {
                var input = ordered[i];
                var bytes = c.InputCodec.EncodeCanonical(input.Payload);
                TInput decoded;
                try
                {
                    decoded = c.InputCodec.Decode(bytes);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return TimelineResult.Fail<AppendReceipt>(TimelineErrorCode.IncompatibleVersion,
                        $"Input codec for '{c.Versions.InputSchemaVersion}' cannot decode its own encoding of '{input.InputId}'.");
                }

                if (!c.InputCodec.EncodeCanonical(decoded).AsSpan().SequenceEqual(bytes))
                {
                    return TimelineResult.Fail<AppendReceipt>(TimelineErrorCode.IncompatibleVersion,
                        $"Input codec for '{c.Versions.InputSchemaVersion}' does not round-trip '{input.InputId}' canonically.");
                }

                canonicalInputs[i] = new OrderedInput<byte[]>(input.InputId, input.Ordinal, bytes);
                decodedInputs[i] = new OrderedInput<TInput>(input.InputId, input.Ordinal, decoded);
            }

            ct.ThrowIfCancellationRequested();
            var after = c.Transition.Apply(before.State, nextStep.Position, decodedInputs, segment.Context);
            var stateBytes = c.StateCodec.EncodeCanonical(after);
            var stateHash = TimelineHashing.StateHash(h.Manifest.StateSchemaVersion, stateBytes);
            var checkpoint = nextStep.Position % _checkpointInterval == 0 ? stateBytes : null;

            return await _store.AppendStepAsync(new NewStep(
                history, expectedLastPosition, new StepRecord<byte[]>(nextStep.Position, canonicalInputs),
                stateHash, checkpoint, operationKey), ct);
        }, ct);
    }

    public Task<Result<CompletionReceipt, TimelineError>> CompleteAsync(TimelineId history, long expectedLastPosition, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            var stored = await _store.GetHistoryAsync(history, ct);
            if (!stored.TryGetValue(out var h))
            {
                return TimelineResult.Fail<CompletionReceipt>(stored.Error);
            }

            if (h.Manifest.Status != HistoryStatus.Verified)
            {
                var precondition = TimelineRules.CheckAppendable(h.Manifest, expectedLastPosition);
                if (precondition is not null)
                {
                    return TimelineResult.Fail<CompletionReceipt>(precondition);
                }
            }

            var replay = await _reconstructor.ReplayAsync(h, ct);
            if (!replay.TryGetValue(out var final))
            {
                if (replay.Error.Code == TimelineErrorCode.CorruptHistory && h.Manifest.Status == HistoryStatus.Recording)
                {
                    // Irrecoverable: record an auditable terminal status; history rows stay untouched.
                    await _store.MarkFailedAsync(history, $"Completion replay failed: {replay.Error.Message}", CancellationToken.None);
                }

                return TimelineResult.Fail<CompletionReceipt>(replay.Error);
            }

            return await _store.MarkVerifiedAsync(history, expectedLastPosition, final.StateHash, ct);
        }, ct);

    public Task<Result<VerifiedState<TState>, TimelineError>> RestoreAsync(TimelineId history, long position, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            var stored = await _store.GetHistoryAsync(history, ct);
            if (!stored.TryGetValue(out var h))
            {
                return TimelineResult.Fail<VerifiedState<TState>>(stored.Error);
            }

            var bounds = TimelineRules.CheckReadablePosition(h.Manifest, position);
            if (bounds is not null)
            {
                return TimelineResult.Fail<VerifiedState<TState>>(bounds);
            }

            var restored = await _reconstructor.RestoreAsync(h, position, ct);
            return restored.TryGetValue(out var r)
                ? TimelineResult.Ok(new VerifiedState<TState>(history, r.Position, r.State, r.StateHash, r.Origin, h.Manifest.Status))
                : TimelineResult.Fail<VerifiedState<TState>>(restored.Error);
        }, ct);

    public Task<Result<VerifiedReplay<TState>, TimelineError>> ReplayAsync(TimelineId history, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            var stored = await _store.GetHistoryAsync(history, ct);
            if (!stored.TryGetValue(out var h))
            {
                return TimelineResult.Fail<VerifiedReplay<TState>>(stored.Error);
            }

            var replay = await _reconstructor.ReplayAsync(h, ct);
            return replay.TryGetValue(out var r)
                ? TimelineResult.Ok(new VerifiedReplay<TState>(history, r.Position, r.State, r.StateHash, r.RecordHash, h.Manifest.Status))
                : TimelineResult.Fail<VerifiedReplay<TState>>(replay.Error);
        }, ct);

    /// <summary>
    /// Restores the Verified source at T through its ancestors' own pinned contexts/reducers, checks that the
    /// restored state and the new context are canonical under the child's components, then lets the store
    /// atomically create the child (pinned context artifact, manifest with branch record, base checkpoint).
    /// No source row is copied or changed; the child owns only steps T+1.. appended through <see cref="AppendAsync"/>.
    /// v1: the child inherits the source's owner, version key, rules hash and seed hash; only the context changes.
    /// </summary>
    public Task<Result<TimelineId, TimelineError>> ForkAsync(
        TimelineId source, long forkPosition, string operationKey, TContext pinnedBranchContext, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        return GuardAsync(async () =>
        {
            var stored = await _store.GetHistoryAsync(source, ct);
            if (!stored.TryGetValue(out var s))
            {
                return TimelineResult.Fail<TimelineId>(stored.Error);
            }

            var rule = TimelineRules.CheckForkable(s.Manifest, forkPosition);
            if (rule is not null)
            {
                return TimelineResult.Fail<TimelineId>(rule);
            }

            var restored = await _reconstructor.RestoreAsync(s, forkPosition, ct);
            if (!restored.TryGetValue(out var atFork))
            {
                return TimelineResult.Fail<TimelineId>(restored.Error);
            }

            // Child-state compatibility, checked before any child transition can run.
            var childVersions = s.Manifest.Versions;
            var resolved = _resolver.Resolve(childVersions);
            if (!resolved.TryGetValue(out var c))
            {
                return TimelineResult.Fail<TimelineId>(resolved.Error);
            }

            var baseState = c.StateCodec.EncodeCanonical(atFork.State);
            var context = c.ContextCodec.EncodeCanonical(pinnedBranchContext);
            if (!RoundTrips(c.StateCodec.Decode, c.StateCodec.EncodeCanonical, baseState)
                || !RoundTrips(c.ContextCodec.Decode, c.ContextCodec.EncodeCanonical, context))
            {
                return TimelineResult.Fail<TimelineId>(TimelineErrorCode.IncompatibleVersion,
                    $"Codecs registered for {childVersions} do not round-trip the fork state/context canonically.");
            }

            if (!string.Equals(TimelineHashing.StateHash(childVersions.StateSchemaVersion, baseState), atFork.StateHash, StringComparison.Ordinal))
            {
                return TimelineResult.Fail<TimelineId>(TimelineErrorCode.IncompatibleVersion,
                    $"State restored at {forkPosition} does not encode to its verified hash under the child's {childVersions}.");
            }

            var created = await _store.CreateHistoryAsync(new NewHistory(
                s.Manifest.OwnerId, childVersions, context, s.Manifest.RulesHash, s.Manifest.SeedHash, baseState,
                operationKey, new ForkOrigin(source, forkPosition)), ct);
            return created.TryGetValue(out var child)
                ? TimelineResult.Ok(child.Manifest.Id)
                : TimelineResult.Fail<TimelineId>(created.Error);
        }, ct);
    }

    /// <summary>
    /// Verified states of both histories at each requested position, in request order. Both histories must
    /// descend from the same root source (one shared position axis, else Conflict), and every position must be
    /// readable in both (else InvalidPosition for the whole call, checked before any reconstruction). Each
    /// returned state carries its own history status, so a Recording branch reports a verified prefix only.
    /// No outcome metric is computed here.
    /// </summary>
    public Task<Result<IReadOnlyList<AlignedState<TState>>, TimelineError>> CompareAlignedAsync(
        TimelineId source, TimelineId alternative, IReadOnlyList<long> positions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(positions);
        return GuardAsync(async () =>
        {
            var left = await _store.GetHistoryAsync(source, ct);
            if (!left.TryGetValue(out var a))
            {
                return TimelineResult.Fail<IReadOnlyList<AlignedState<TState>>>(left.Error);
            }

            var right = await _store.GetHistoryAsync(alternative, ct);
            if (!right.TryGetValue(out var b))
            {
                return TimelineResult.Fail<IReadOnlyList<AlignedState<TState>>>(right.Error);
            }

            foreach (var position in positions)
            {
                var bounds = TimelineRules.CheckReadablePosition(a.Manifest, position)
                             ?? TimelineRules.CheckReadablePosition(b.Manifest, position);
                if (bounds is not null)
                {
                    return TimelineResult.Fail<IReadOnlyList<AlignedState<TState>>>(bounds);
                }
            }

            var rootA = await _reconstructor.FindRootAsync(a, ct);
            if (!rootA.TryGetValue(out var ra))
            {
                return TimelineResult.Fail<IReadOnlyList<AlignedState<TState>>>(rootA.Error);
            }

            var rootB = await _reconstructor.FindRootAsync(b, ct);
            if (!rootB.TryGetValue(out var rb))
            {
                return TimelineResult.Fail<IReadOnlyList<AlignedState<TState>>>(rootB.Error);
            }

            if (ra != rb)
            {
                return TimelineResult.Fail<IReadOnlyList<AlignedState<TState>>>(TimelineErrorCode.Conflict,
                    "Histories do not descend from the same source, so their positions are not aligned.");
            }

            var aligned = new List<AlignedState<TState>>(positions.Count);
            foreach (var position in positions)
            {
                ct.ThrowIfCancellationRequested();
                var sa = await _reconstructor.RestoreAsync(a, position, ct);
                if (!sa.TryGetValue(out var va))
                {
                    return TimelineResult.Fail<IReadOnlyList<AlignedState<TState>>>(sa.Error);
                }

                var sb = await _reconstructor.RestoreAsync(b, position, ct);
                if (!sb.TryGetValue(out var vb))
                {
                    return TimelineResult.Fail<IReadOnlyList<AlignedState<TState>>>(sb.Error);
                }

                aligned.Add(new AlignedState<TState>(position,
                    new VerifiedState<TState>(source, va.Position, va.State, va.StateHash, va.Origin, a.Manifest.Status),
                    new VerifiedState<TState>(alternative, vb.Position, vb.State, vb.StateHash, vb.Origin, b.Manifest.Status)));
            }

            return TimelineResult.Ok<IReadOnlyList<AlignedState<TState>>>(aligned);
        }, ct);
    }

    private delegate T SpanDecoder<out T>(ReadOnlySpan<byte> bytes);

    private static bool RoundTrips<T>(SpanDecoder<T> decode, Func<T, byte[]> encode, byte[] bytes)
    {
        try
        {
            return encode(decode(bytes)).AsSpan().SequenceEqual(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Maps cancellation observed by the engine (fold loop, codecs) to the typed Cancelled failure.</summary>
    private static async Task<Result<T, TimelineError>> GuardAsync<T>(Func<Task<Result<T, TimelineError>>> operation, CancellationToken ct)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return TimelineResult.Fail<T>(TimelineErrorCode.Cancelled, "Operation was cancelled; nothing further was committed.");
        }
    }
}
