using Marrow.Kernel.Canonical;
using Marrow.Kernel.Results;
using Marrow.Timeline;
using Marrow.Timeline.Internal;
using Npgsql;
using NpgsqlTypes;

namespace Marrow.Infrastructure.Timeline;

/// <summary>
/// PostgreSQL implementation of the internal Timeline persistence port over the <c>marrow_timeline</c>
/// schema (migration <c>scripts/db/migrations/marrow_timeline/0001_timeline_journal.sql</c>).
/// Every write is one transaction that locks the manifest row, so appends to one history are
/// serialized and the optimistic expected-position check cannot race. Reads use a fixed number of
/// queries per call (no per-step round trips). Historical rows are only ever INSERTed.
/// Expected failures return <see cref="TimelineError"/>; an unreachable database surfaces as an exception
/// after rollback, leaving prior durable history intact.
/// </summary>
internal sealed class PostgresTimelineStore(NpgsqlDataSource dataSource) : ITimelineStore
{
    private const string HistoryColumns = """
        timeline_id, domain_id, owner_id, reducer_version, input_schema_version, state_schema_version,
        context_schema_version, context_hash, context_ref, rules_hash, seed_hash, parent_timeline_id,
        fork_position, base_position, base_state_hash, base_record_hash, request_fingerprint,
        last_position, latest_state_hash, last_record_hash, status, failure_reason
        """;

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<Result<StoredHistory, TimelineError>> CreateHistoryAsync(NewHistory request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GuardAsync(() => CreateCoreAsync(request, ct), ct);
    }

    public Task<Result<AppendReceipt, TimelineError>> AppendStepAsync(NewStep request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GuardAsync(() => AppendCoreAsync(request, ct), ct);
    }

    public Task<Result<StoredHistory, TimelineError>> GetHistoryAsync(TimelineId id, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var history = await LoadHistoryAsync(conn, null, id, lockMode: null, ct);
            return history is null ? NotFound<StoredHistory>(id) : TimelineResult.Ok(history);
        }, ct);

    public Task<Result<IReadOnlyList<StoredStep>, TimelineError>> ReadStepsAsync(
        TimelineId id, long afterPosition, long throughPosition, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var history = await LoadHistoryAsync(conn, null, id, lockMode: null, ct);
            if (history is null)
            {
                return NotFound<IReadOnlyList<StoredStep>>(id);
            }

            if (afterPosition < history.BasePosition || afterPosition > throughPosition
                || throughPosition > history.Manifest.LastPosition)
            {
                return TimelineResult.Fail<IReadOnlyList<StoredStep>>(TimelineErrorCode.InvalidPosition,
                    $"Own steps ({afterPosition}, {throughPosition}] are outside {history.BasePosition}..{history.Manifest.LastPosition}.");
            }

            return await ReadOwnStepsAsync(conn, null, id, afterPosition, throughPosition, ct);
        }, ct);

    public Task<Result<StoredCheckpoint, TimelineError>> ReadNearestCheckpointAsync(TimelineId id, long atOrBefore, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var history = await LoadHistoryAsync(conn, null, id, lockMode: null, ct);
            if (history is null)
            {
                return NotFound<StoredCheckpoint>(id);
            }

            if (atOrBefore < history.BasePosition || atOrBefore > history.Manifest.LastPosition)
            {
                return TimelineResult.Fail<StoredCheckpoint>(TimelineErrorCode.InvalidPosition,
                    $"Position {atOrBefore} is outside own range {history.BasePosition}..{history.Manifest.LastPosition}.");
            }

            await using var cmd = new NpgsqlCommand("""
                SELECT position, state_schema_version, state_hash, state_bytes
                FROM marrow_timeline.checkpoints
                WHERE timeline_id = @id AND position <= @at
                ORDER BY position DESC
                LIMIT 1
                """, conn);
            cmd.Parameters.Add(Param("id", id.Value));
            cmd.Parameters.Add(Param("at", atOrBefore));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return TimelineResult.Fail<StoredCheckpoint>(TimelineErrorCode.CorruptHistory,
                    $"History {id.Value} has no checkpoint at or before {atOrBefore} (base checkpoint missing).");
            }

            var checkpoint = new StoredCheckpoint(
                reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<byte[]>(3), reader.GetString(2));
            if (!string.Equals(checkpoint.StateSchemaVersion, history.Manifest.StateSchemaVersion, StringComparison.Ordinal))
            {
                return TimelineResult.Fail<StoredCheckpoint>(TimelineErrorCode.IncompatibleVersion,
                    $"Checkpoint {checkpoint.Position} state schema '{checkpoint.StateSchemaVersion}' differs from pinned '{history.Manifest.StateSchemaVersion}'.");
            }

            return string.Equals(
                TimelineHashing.StateHash(checkpoint.StateSchemaVersion, checkpoint.CanonicalState), checkpoint.StateHash, StringComparison.Ordinal)
                ? TimelineResult.Ok(checkpoint)
                : TimelineResult.Fail<StoredCheckpoint>(TimelineErrorCode.CorruptHistory,
                    $"Checkpoint {checkpoint.Position} bytes do not match its stored state hash.");
        }, ct);

    public Task<Result<StoredContext, TimelineError>> ReadContextAsync(string contextArtifactRef, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextArtifactRef);
        return GuardAsync(async () =>
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT context_schema_version, context_hash, canonical_bytes
                FROM marrow_timeline.context_artifacts WHERE context_ref = @ref
                """, conn);
            cmd.Parameters.Add(Param("ref", contextArtifactRef));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return TimelineResult.Fail<StoredContext>(TimelineErrorCode.IncompatibleVersion,
                    $"Pinned context artifact {contextArtifactRef} is not available.");
            }

            var context = new StoredContext(contextArtifactRef, reader.GetString(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2));
            var recomputed = TimelineHashing.ContextHash(context.ContextSchemaVersion, context.CanonicalContext);
            return string.Equals(recomputed, context.ContextHash, StringComparison.Ordinal)
                   && string.Equals(TimelineHashing.ContextArtifactRef(recomputed), contextArtifactRef, StringComparison.Ordinal)
                ? TimelineResult.Ok(context)
                : TimelineResult.Fail<StoredContext>(TimelineErrorCode.CorruptHistory,
                    $"Context artifact {contextArtifactRef} bytes do not match their content address.");
        }, ct);
    }

    public Task<Result<CompletionReceipt, TimelineError>> MarkVerifiedAsync(
        TimelineId id, long expectedLastPosition, string replayedFinalStateHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayedFinalStateHash);
        return GuardAsync(async () =>
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var history = await LoadHistoryAsync(conn, tx, id, "FOR UPDATE", ct);
            if (history is null)
            {
                return NotFound<CompletionReceipt>(id);
            }

            var m = history.Manifest;
            var receipt = new CompletionReceipt(id, m.LastPosition, m.LatestStateHash, m.LastRecordHash, HistoryStatus.Verified);
            if (m.Status == HistoryStatus.Verified && m.LastPosition == expectedLastPosition
                && string.Equals(m.LatestStateHash, replayedFinalStateHash, StringComparison.Ordinal))
            {
                return TimelineResult.Ok(receipt); // idempotent retry of a completed verification
            }

            var precondition = TimelineRules.CheckAppendable(m, expectedLastPosition);
            if (precondition is not null)
            {
                return TimelineResult.Fail<CompletionReceipt>(precondition);
            }

            if (!string.Equals(m.LatestStateHash, replayedFinalStateHash, StringComparison.Ordinal))
            {
                return TimelineResult.Fail<CompletionReceipt>(TimelineErrorCode.CorruptHistory,
                    $"Replayed final state hash does not match the recorded hash at position {m.LastPosition}.");
            }

            var steps = await ReadOwnStepsAsync(conn, tx, id, history.BasePosition, m.LastPosition, ct);
            if (!steps.TryGetValue(out var ownSteps))
            {
                return TimelineResult.Fail<CompletionReceipt>(steps.Error);
            }

            var chainError = VerifyStoredChain(history, ownSteps);
            if (chainError is not null)
            {
                return TimelineResult.Fail<CompletionReceipt>(chainError);
            }

            await using (var update = new NpgsqlCommand("""
                UPDATE marrow_timeline.histories SET status = 'Verified', updated_at = now()
                WHERE timeline_id = @id AND status = 'Recording' AND last_position = @expected
                """, conn, tx))
            {
                update.Parameters.Add(Param("id", id.Value));
                update.Parameters.Add(Param("expected", expectedLastPosition));
                if (await update.ExecuteNonQueryAsync(ct) != 1)
                {
                    return TimelineResult.Fail<CompletionReceipt>(TimelineErrorCode.Conflict, "History changed during completion.");
                }
            }

            await CommitAsync(tx, ct);
            return TimelineResult.Ok(receipt);
        }, ct);
    }

    public Task<Result<StoredHistory, TimelineError>> MarkFailedAsync(TimelineId id, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return GuardAsync(async () =>
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var history = await LoadHistoryAsync(conn, tx, id, "FOR UPDATE", ct);
            if (history is null)
            {
                return NotFound<StoredHistory>(id);
            }

            if (history.Manifest.Status == HistoryStatus.Failed)
            {
                return TimelineResult.Ok(history); // terminal; the first recorded reason is kept
            }

            await using (var update = new NpgsqlCommand("""
                UPDATE marrow_timeline.histories SET status = 'Failed', failure_reason = @reason, updated_at = now()
                WHERE timeline_id = @id
                """, conn, tx))
            {
                update.Parameters.Add(Param("id", id.Value));
                update.Parameters.Add(Param("reason", reason));
                await update.ExecuteNonQueryAsync(ct);
            }

            var failed = await LoadHistoryAsync(conn, tx, id, lockMode: null, ct);
            await CommitAsync(tx, ct);
            return TimelineResult.Ok(failed!);
        }, ct);
    }

    private async Task<Result<StoredHistory, TimelineError>> CreateCoreAsync(NewHistory request, CancellationToken ct)
    {
        var versions = request.Versions;
        var contextHash = TimelineHashing.ContextHash(versions.ContextSchemaVersion, request.CanonicalContext);
        var contextRef = TimelineHashing.ContextArtifactRef(contextHash);
        var baseStateHash = TimelineHashing.StateHash(versions.StateSchemaVersion, request.CanonicalBaseState);
        var fingerprint = Fingerprint(request, contextHash, baseStateHash);
        var parentId = request.Fork?.ParentId;
        var basePosition = request.Fork?.ForkPosition ?? 0;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existing = await FindByOperationScopeAsync(conn, tx, request, ct);
        if (existing is not null)
        {
            return SameRequest(existing.Value, fingerprint);
        }

        var anchorRecordHash = TimelineHashing.GenesisAnchor;
        if (request.Fork is { } fork)
        {
            var anchor = await ResolveForkAnchorAsync(conn, tx, fork, versions, baseStateHash, ct);
            if (!anchor.TryGetValue(out var anchorHash))
            {
                return TimelineResult.Fail<StoredHistory>(anchor.Error);
            }

            anchorRecordHash = anchorHash;
        }

        var id = new TimelineId(Guid.CreateVersion7());
        var baseRecordHash = TimelineHashing.ChainHash(anchorRecordHash, TimelineHashing.GenesisEnvelope(
            id, versions, contextHash, request.RulesHash, request.SeedHash, parentId, basePosition, baseStateHash));

        await using (var artifact = new NpgsqlCommand("""
            INSERT INTO marrow_timeline.context_artifacts (context_ref, context_schema_version, context_hash, canonical_bytes)
            VALUES (@ref, @schema, @hash, @bytes)
            ON CONFLICT (context_ref) DO NOTHING
            """, conn, tx))
        {
            artifact.Parameters.Add(Param("ref", contextRef));
            artifact.Parameters.Add(Param("schema", versions.ContextSchemaVersion));
            artifact.Parameters.Add(Param("hash", contextHash));
            artifact.Parameters.Add(Param("bytes", request.CanonicalContext));
            await artifact.ExecuteNonQueryAsync(ct);
        }

        bool inserted;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO marrow_timeline.histories (
              timeline_id, domain_id, owner_id, reducer_version, input_schema_version, state_schema_version,
              context_schema_version, context_hash, context_ref, rules_hash, seed_hash, parent_timeline_id,
              fork_position, base_position, base_state_hash, base_record_hash, operation_key, request_fingerprint,
              last_position, latest_state_hash, last_record_hash, status)
            VALUES (
              @id, @domain, @owner, @reducer, @input, @state, @context, @contextHash, @contextRef, @rules, @seed,
              @parent, @fork, @base, @baseState, @baseRecord, @key, @fingerprint, @base, @baseState, @baseRecord, 'Recording')
            ON CONFLICT DO NOTHING
            """, conn, tx))
        {
            insert.Parameters.Add(Param("id", id.Value));
            insert.Parameters.Add(Param("domain", versions.DomainId));
            insert.Parameters.Add(Param("owner", request.OwnerId));
            insert.Parameters.Add(Param("reducer", versions.ReducerVersion));
            insert.Parameters.Add(Param("input", versions.InputSchemaVersion));
            insert.Parameters.Add(Param("state", versions.StateSchemaVersion));
            insert.Parameters.Add(Param("context", versions.ContextSchemaVersion));
            insert.Parameters.Add(Param("contextHash", contextHash));
            insert.Parameters.Add(Param("contextRef", contextRef));
            insert.Parameters.Add(Param("rules", request.RulesHash));
            insert.Parameters.Add(Param("seed", request.SeedHash));
            insert.Parameters.Add(NullableParam("parent", NpgsqlDbType.Uuid, parentId?.Value));
            insert.Parameters.Add(NullableParam("fork", NpgsqlDbType.Bigint, request.Fork?.ForkPosition));
            insert.Parameters.Add(Param("base", basePosition));
            insert.Parameters.Add(Param("baseState", baseStateHash));
            insert.Parameters.Add(Param("baseRecord", baseRecordHash));
            insert.Parameters.Add(Param("key", request.OperationKey));
            insert.Parameters.Add(Param("fingerprint", fingerprint));
            inserted = await insert.ExecuteNonQueryAsync(ct) == 1;
        }

        if (!inserted)
        {
            // A concurrent create with the same operation scope committed first.
            var winner = await FindByOperationScopeAsync(conn, tx, request, ct);
            return winner is null
                ? TimelineResult.Fail<StoredHistory>(TimelineErrorCode.Conflict, "History creation conflicted with a concurrent write.")
                : SameRequest(winner.Value, fingerprint);
        }

        await using (var checkpoint = new NpgsqlCommand("""
            INSERT INTO marrow_timeline.checkpoints (timeline_id, position, state_schema_version, state_hash, state_bytes)
            VALUES (@id, @base, @schema, @hash, @bytes)
            """, conn, tx))
        {
            checkpoint.Parameters.Add(Param("id", id.Value));
            checkpoint.Parameters.Add(Param("base", basePosition));
            checkpoint.Parameters.Add(Param("schema", versions.StateSchemaVersion));
            checkpoint.Parameters.Add(Param("hash", baseStateHash));
            checkpoint.Parameters.Add(Param("bytes", request.CanonicalBaseState));
            await checkpoint.ExecuteNonQueryAsync(ct);
        }

        var created = await LoadHistoryAsync(conn, tx, id, lockMode: null, ct);
        await CommitAsync(tx, ct);
        return TimelineResult.Ok(created!);
    }

    private async Task<Result<AppendReceipt, TimelineError>> AppendCoreAsync(NewStep request, CancellationToken ct)
    {
        var canonical = TimelineRules.CanonicalizeNextStep(request.ExpectedLastPosition, request.Step);
        if (!canonical.TryGetValue(out var inputs))
        {
            return TimelineResult.Fail<AppendReceipt>(canonical.Error);
        }

        if (!TimelineHashing.IsHash(request.StateHashAfter))
        {
            throw new ArgumentException("StateHashAfter must be 64 lowercase hex characters.", nameof(request));
        }

        var position = request.Step.Position;
        var envelope = TimelineHashing.StepEnvelope(position, inputs, request.StateHashAfter);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var history = await LoadHistoryAsync(conn, tx, request.Timeline, "FOR UPDATE", ct);
        if (history is null)
        {
            return NotFound<AppendReceipt>(request.Timeline);
        }

        // Idempotency by (history, operation key): identical content returns the original receipt.
        await using (var prior = new NpgsqlCommand("""
            SELECT position, state_hash, previous_record_hash, record_hash
            FROM marrow_timeline.steps WHERE timeline_id = @id AND operation_key = @key
            """, conn, tx))
        {
            prior.Parameters.Add(Param("id", request.Timeline.Value));
            prior.Parameters.Add(Param("key", request.OperationKey));
            await using var reader = await prior.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var priorPosition = reader.GetInt64(0);
                var priorState = reader.GetString(1);
                var priorPrevious = reader.GetString(2);
                var priorRecord = reader.GetString(3);
                return priorPosition == position
                       && string.Equals(TimelineHashing.ChainHash(priorPrevious, envelope), priorRecord, StringComparison.Ordinal)
                    ? TimelineResult.Ok(new AppendReceipt(request.Timeline, priorPosition, priorState, priorRecord))
                    : TimelineResult.Fail<AppendReceipt>(TimelineErrorCode.Conflict,
                        $"Operation key was already used for a different step at position {priorPosition}.");
            }
        }

        var precondition = TimelineRules.CheckAppendable(history.Manifest, request.ExpectedLastPosition);
        if (precondition is not null)
        {
            return TimelineResult.Fail<AppendReceipt>(precondition);
        }

        if (inputs.Count > 0)
        {
            // InputIds are unique across the whole logical history: this history's own steps plus every inherited
            // (immutable) ancestor prefix through its fork position. Ancestor inputs after T are not part of it.
            await using var duplicates = new NpgsqlCommand("""
                WITH RECURSIVE lineage (timeline_id, parent_timeline_id, fork_position, through, depth) AS (
                  SELECT timeline_id, parent_timeline_id, fork_position, 9223372036854775807::bigint, 0
                  FROM marrow_timeline.histories WHERE timeline_id = @id
                  UNION ALL
                  SELECT p.timeline_id, p.parent_timeline_id, p.fork_position, LEAST(l.through, l.fork_position), l.depth + 1
                  FROM marrow_timeline.histories p JOIN lineage l ON p.timeline_id = l.parent_timeline_id
                  WHERE l.depth < 1024
                )
                SELECT si.input_id FROM marrow_timeline.step_inputs si
                JOIN lineage l ON si.timeline_id = l.timeline_id AND si.position <= l.through
                WHERE si.input_id = ANY(@ids) LIMIT 1
                """, conn, tx);
            duplicates.Parameters.Add(Param("id", request.Timeline.Value));
            duplicates.Parameters.Add(Param("ids", inputs.Select(i => i.InputId).ToArray()));
            if (await duplicates.ExecuteScalarAsync(ct) is string duplicate)
            {
                return TimelineResult.Fail<AppendReceipt>(TimelineErrorCode.Conflict,
                    $"InputId '{duplicate}' already exists in this history.");
            }
        }

        if (request.CheckpointState is { } checkpointState
            && !string.Equals(TimelineHashing.StateHash(history.Manifest.StateSchemaVersion, checkpointState),
                request.StateHashAfter, StringComparison.Ordinal))
        {
            return TimelineResult.Fail<AppendReceipt>(TimelineErrorCode.CorruptHistory,
                $"Checkpoint bytes for step {position} do not match the step state hash.");
        }

        var recordHash = TimelineHashing.ChainHash(history.Manifest.LastRecordHash, envelope);

        await using var batch = new NpgsqlBatch(conn, tx);
        var step = new NpgsqlBatchCommand("""
            INSERT INTO marrow_timeline.steps (timeline_id, position, operation_key, input_count, state_hash, previous_record_hash, record_hash)
            VALUES (@id, @position, @key, @count, @state, @previous, @record)
            """);
        step.Parameters.Add(Param("id", request.Timeline.Value));
        step.Parameters.Add(Param("position", position));
        step.Parameters.Add(Param("key", request.OperationKey));
        step.Parameters.Add(Param("count", inputs.Count));
        step.Parameters.Add(Param("state", request.StateHashAfter));
        step.Parameters.Add(Param("previous", history.Manifest.LastRecordHash));
        step.Parameters.Add(Param("record", recordHash));
        batch.BatchCommands.Add(step);

        if (inputs.Count > 0)
        {
            var rows = new NpgsqlBatchCommand("""
                INSERT INTO marrow_timeline.step_inputs (timeline_id, position, input_index, input_id, ordinal, payload)
                SELECT @id, @position, (t.idx - 1)::integer, t.input_id, t.ordinal, t.payload
                FROM unnest(@ids, @ordinals, @payloads) WITH ORDINALITY AS t(input_id, ordinal, payload, idx)
                """);
            rows.Parameters.Add(Param("id", request.Timeline.Value));
            rows.Parameters.Add(Param("position", position));
            rows.Parameters.Add(Param("ids", inputs.Select(i => i.InputId).ToArray()));
            rows.Parameters.Add(Param("ordinals", inputs.Select(i => i.Ordinal).ToArray()));
            rows.Parameters.Add(Param("payloads", inputs.Select(i => i.Payload).ToArray()));
            batch.BatchCommands.Add(rows);
        }

        if (request.CheckpointState is { } bytes)
        {
            var checkpoint = new NpgsqlBatchCommand("""
                INSERT INTO marrow_timeline.checkpoints (timeline_id, position, state_schema_version, state_hash, state_bytes)
                VALUES (@id, @position, @schema, @state, @bytes)
                """);
            checkpoint.Parameters.Add(Param("id", request.Timeline.Value));
            checkpoint.Parameters.Add(Param("position", position));
            checkpoint.Parameters.Add(Param("schema", history.Manifest.StateSchemaVersion));
            checkpoint.Parameters.Add(Param("state", request.StateHashAfter));
            checkpoint.Parameters.Add(Param("bytes", bytes));
            batch.BatchCommands.Add(checkpoint);
        }

        var progress = new NpgsqlBatchCommand("""
            UPDATE marrow_timeline.histories
            SET last_position = @position, latest_state_hash = @state, last_record_hash = @record, updated_at = now()
            WHERE timeline_id = @id AND status = 'Recording' AND last_position = @expected
            """);
        progress.Parameters.Add(Param("id", request.Timeline.Value));
        progress.Parameters.Add(Param("position", position));
        progress.Parameters.Add(Param("state", request.StateHashAfter));
        progress.Parameters.Add(Param("record", recordHash));
        progress.Parameters.Add(Param("expected", request.ExpectedLastPosition));
        batch.BatchCommands.Add(progress);

        await batch.ExecuteNonQueryAsync(ct);
        if (progress.RecordsAffected != 1)
        {
            return TimelineResult.Fail<AppendReceipt>(TimelineErrorCode.Conflict, "History changed during append.");
        }

        await CommitAsync(tx, ct);
        return TimelineResult.Ok(new AppendReceipt(request.Timeline, position, request.StateHashAfter, recordHash));
    }

    /// <summary>
    /// Locks the parent (FOR SHARE, so it cannot be failed concurrently), applies the v1 fork rule and
    /// returns the record hash anchoring the child chain: the record of the segment that owns position T,
    /// walking up ancestors when T lies in an inherited prefix. The supplied base state must hash to the
    /// recorded state hash at T.
    /// </summary>
    private static async Task<Result<string, TimelineError>> ResolveForkAnchorAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, ForkOrigin fork, TimelineVersionKey childVersions,
        string childBaseStateHash, CancellationToken ct)
    {
        var parent = await LoadHistoryAsync(conn, tx, fork.ParentId, "FOR SHARE", ct);
        if (parent is null)
        {
            return NotFound<string>(fork.ParentId);
        }

        var rule = TimelineRules.CheckForkable(parent.Manifest, fork.ForkPosition);
        if (rule is not null)
        {
            return TimelineResult.Fail<string>(rule);
        }

        var segment = parent;
        for (var depth = 0; fork.ForkPosition < segment.BasePosition; depth++)
        {
            if (depth > 1024 || segment.Manifest.ParentId is not { } ancestorId)
            {
                return TimelineResult.Fail<string>(TimelineErrorCode.CorruptHistory, "Fork ancestry is malformed.");
            }

            segment = await LoadHistoryAsync(conn, tx, ancestorId, lockMode: null, ct)
                ?? throw new InvalidOperationException("Ancestor row disappeared despite a foreign key.");
        }

        if (!string.Equals(segment.Manifest.DomainId, childVersions.DomainId, StringComparison.Ordinal)
            || !string.Equals(segment.Manifest.StateSchemaVersion, childVersions.StateSchemaVersion, StringComparison.Ordinal))
        {
            return TimelineResult.Fail<string>(TimelineErrorCode.IncompatibleVersion,
                "A child must keep the domain and state schema version of the segment it forks from.");
        }

        string recordHash;
        string stateHash;
        if (fork.ForkPosition == segment.BasePosition)
        {
            recordHash = segment.BaseRecordHash;
            stateHash = segment.BaseStateHash;
        }
        else
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT record_hash, state_hash FROM marrow_timeline.steps WHERE timeline_id = @id AND position = @position
                """, conn, tx);
            cmd.Parameters.Add(Param("id", segment.Manifest.Id.Value));
            cmd.Parameters.Add(Param("position", fork.ForkPosition));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return TimelineResult.Fail<string>(TimelineErrorCode.CorruptHistory, $"Source step {fork.ForkPosition} is missing.");
            }

            recordHash = reader.GetString(0);
            stateHash = reader.GetString(1);
        }

        return string.Equals(stateHash, childBaseStateHash, StringComparison.Ordinal)
            ? TimelineResult.Ok(recordHash)
            : TimelineResult.Fail<string>(TimelineErrorCode.CorruptHistory,
                $"Fork base state does not match the source's recorded state hash at {fork.ForkPosition}.");
    }

    private static async Task<Result<IReadOnlyList<StoredStep>, TimelineError>> ReadOwnStepsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, TimelineId id, long after, long through, CancellationToken ct)
    {
        var steps = new List<(long Position, int Count, string State, string Previous, string Record)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT position, input_count, state_hash, previous_record_hash, record_hash
            FROM marrow_timeline.steps
            WHERE timeline_id = @id AND position > @after AND position <= @through
            ORDER BY position
            """, conn, tx))
        {
            cmd.Parameters.Add(Param("id", id.Value));
            cmd.Parameters.Add(Param("after", after));
            cmd.Parameters.Add(Param("through", through));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                steps.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            }
        }

        var inputsByPosition = new Dictionary<long, List<OrderedInput<byte[]>>>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT position, input_id, ordinal, payload
            FROM marrow_timeline.step_inputs
            WHERE timeline_id = @id AND position > @after AND position <= @through
            ORDER BY position, input_index
            """, conn, tx))
        {
            cmd.Parameters.Add(Param("id", id.Value));
            cmd.Parameters.Add(Param("after", after));
            cmd.Parameters.Add(Param("through", through));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var position = reader.GetInt64(0);
                if (!inputsByPosition.TryGetValue(position, out var list))
                {
                    inputsByPosition[position] = list = [];
                }

                list.Add(new OrderedInput<byte[]>(reader.GetString(1), reader.GetInt32(2), reader.GetFieldValue<byte[]>(3)));
            }
        }

        var result = new List<StoredStep>(steps.Count);
        foreach (var s in steps)
        {
            IReadOnlyList<OrderedInput<byte[]>> inputs = inputsByPosition.TryGetValue(s.Position, out var list) ? list : [];
            if (inputs.Count != s.Count)
            {
                return TimelineResult.Fail<IReadOnlyList<StoredStep>>(TimelineErrorCode.CorruptHistory,
                    $"Step {s.Position} has {inputs.Count} stored inputs but records {s.Count}.");
            }

            result.Add(new StoredStep(s.Position, inputs, s.State, s.Previous, s.Record));
        }

        var expected = through - after;
        return result.Count == expected
            ? TimelineResult.Ok<IReadOnlyList<StoredStep>>(result)
            : TimelineResult.Fail<IReadOnlyList<StoredStep>>(TimelineErrorCode.CorruptHistory,
                $"Expected {expected} stored steps in ({after}, {through}] but found {result.Count}.");
    }

    private static TimelineError? VerifyStoredChain(StoredHistory history, IReadOnlyList<StoredStep> steps)
    {
        var chainError = TimelineHashing.VerifyChain(history.BasePosition, history.BaseRecordHash, steps);
        if (chainError is not null)
        {
            return chainError;
        }

        var lastRecord = steps.Count == 0 ? history.BaseRecordHash : steps[^1].RecordHash;
        var lastState = steps.Count == 0 ? history.BaseStateHash : steps[^1].StateHash;
        return string.Equals(lastRecord, history.Manifest.LastRecordHash, StringComparison.Ordinal)
               && string.Equals(lastState, history.Manifest.LatestStateHash, StringComparison.Ordinal)
            ? null
            : new(TimelineErrorCode.CorruptHistory, "Manifest terminal hashes do not match the stored record chain.");
    }

    private static async Task<StoredHistory?> LoadHistoryAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, TimelineId id, string? lockMode, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {HistoryColumns} FROM marrow_timeline.histories WHERE timeline_id = @id {lockMode}", conn, tx);
        cmd.Parameters.Add(Param("id", id.Value));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadHistory(reader) : null;
    }

    private static async Task<(StoredHistory History, string Fingerprint)?> FindByOperationScopeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, NewHistory request, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT {HistoryColumns} FROM marrow_timeline.histories
            WHERE owner_id = @owner AND operation_key = @key
              AND parent_timeline_id IS NOT DISTINCT FROM @parent AND fork_position IS NOT DISTINCT FROM @fork
            """, conn, tx);
        cmd.Parameters.Add(Param("owner", request.OwnerId));
        cmd.Parameters.Add(Param("key", request.OperationKey));
        cmd.Parameters.Add(NullableParam("parent", NpgsqlDbType.Uuid, request.Fork?.ParentId.Value));
        cmd.Parameters.Add(NullableParam("fork", NpgsqlDbType.Bigint, request.Fork?.ForkPosition));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (ReadHistory(reader), reader.GetString(16)) : null;
    }

    private static Result<StoredHistory, TimelineError> SameRequest((StoredHistory History, string Fingerprint) existing, string fingerprint) =>
        string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? TimelineResult.Ok(existing.History)
            : TimelineResult.Fail<StoredHistory>(TimelineErrorCode.Conflict,
                "Operation key was already used with a different create/fork request.");

    private static StoredHistory ReadHistory(NpgsqlDataReader r)
    {
        var parent = r.IsDBNull(11) ? (TimelineId?)null : new TimelineId(r.GetGuid(11));
        var fork = r.IsDBNull(12) ? (long?)null : r.GetInt64(12);
        var status = r.GetString(20) switch
        {
            "Recording" => HistoryStatus.Recording,
            "Verified" => HistoryStatus.Verified,
            "Failed" => HistoryStatus.Failed,
            var other => throw new InvalidOperationException($"Unknown stored history status '{other}'."),
        };
        var manifest = new HistoryManifest(
            Id: new TimelineId(r.GetGuid(0)),
            DomainId: r.GetString(1),
            OwnerId: r.GetString(2),
            InputSchemaVersion: r.GetString(4),
            StateSchemaVersion: r.GetString(5),
            ContextSchemaVersion: r.GetString(6),
            ContextHash: r.GetString(7),
            ContextArtifactRef: r.GetString(8),
            ReducerVersion: r.GetString(3),
            RulesHash: r.GetString(9),
            SeedHash: r.GetString(10),
            LastPosition: r.GetInt64(17),
            LatestStateHash: r.GetString(18),
            LastRecordHash: r.GetString(19),
            ParentId: parent,
            ForkPosition: fork,
            Status: status);
        return new StoredHistory(manifest, r.GetInt64(13), r.GetString(15), r.GetString(14), r.IsDBNull(21) ? null : r.GetString(21));
    }

    /// <summary>Canonical digest of everything a create/fork request asserts, for idempotent-retry comparison.</summary>
    private static string Fingerprint(NewHistory request, string contextHash, string baseStateHash)
    {
        var v = request.Versions;
        var writer = new CanonicalWriter()
            .WriteString("marrow.create.v1")
            .WriteString(request.OwnerId)
            .WriteString(v.DomainId)
            .WriteString(v.ReducerVersion)
            .WriteString(v.InputSchemaVersion)
            .WriteString(v.StateSchemaVersion)
            .WriteString(v.ContextSchemaVersion)
            .WriteString(contextHash)
            .WriteString(request.RulesHash)
            .WriteString(request.SeedHash)
            .WriteString(baseStateHash)
            .WriteBoolean(request.Fork is not null)
            .WriteGuid(request.Fork?.ParentId.Value ?? Guid.Empty)
            .WriteInt64(request.Fork?.ForkPosition ?? 0);
        return TimelineHashing.Sha256Hex(writer.ToArray());
    }

    /// <summary>Last cancellation point is before COMMIT, so a cancelled call never leaves an ambiguous commit.</summary>
    private static async Task CommitAsync(NpgsqlTransaction tx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync(CancellationToken.None);
    }

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
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return TimelineResult.Fail<T>(TimelineErrorCode.Conflict, $"Concurrent conflicting write ({ex.ConstraintName}).");
        }
    }

    private static Result<T, TimelineError> NotFound<T>(TimelineId id) =>
        TimelineResult.Fail<T>(TimelineErrorCode.NotFound, $"History {id.Value} does not exist.");

    private static NpgsqlParameter<T> Param<T>(string name, T value) => new(name, value);

    private static NpgsqlParameter NullableParam(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };
}
