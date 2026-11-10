using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Marrow.Timeline;

namespace GhostLap.Solo;

/// <summary>
/// The Ghost Lap-owned row of one branch (<c>ghostlap.solo_branches</c>): the verified owner, the source original and the
/// exact fork tick, the pinned game identity copied from the source row, and the completion facts set once after the
/// branch reached the final tick and replay-verified.
/// </summary>
public sealed record SoloBranchRecord(
    TimelineId Timeline,
    TimelineId Source,
    long ForkTick,
    string OwnerSubjectId,
    PrincipalKind OwnerKind,
    string? TenantId,
    string OperationKey,
    string RulesVersion,
    string LayoutVersion,
    long Seed,
    byte MascotId,
    SoloOriginalStatus Status,
    long? FinalTick = null,
    string? FinalStateHash = null,
    string? LastRecordHash = null)
{
    /// <summary>True when both rows name the same branch request (owner, key, source, fork tick, game); lifecycle ignored.</summary>
    public bool SameIdentity(SoloBranchRecord other) =>
        Timeline == other.Timeline && SameRequest(other);

    /// <summary>Same owner and operation key asking for the same source, fork tick and game (the timeline id may differ).</summary>
    public bool SameRequest(SoloBranchRecord other) =>
        Source == other.Source && ForkTick == other.ForkTick && OwnerSubjectId == other.OwnerSubjectId
        && OwnerKind == other.OwnerKind && TenantId == other.TenantId && OperationKey == other.OperationKey
        && RulesVersion == other.RulesVersion && LayoutVersion == other.LayoutVersion && Seed == other.Seed
        && MascotId == other.MascotId;
}

public sealed partial class SoloOriginalStore
{
    private const string BranchColumns =
        "timeline_id, source_timeline_id, fork_tick, owner_subject_id, owner_kind, tenant_id, operation_key, rules_version, " +
        "layout_version, seed, mascot_id, status, final_tick, final_state_hash, last_record_hash";

    /// <summary>
    /// Inserts a Recording branch row. Registering the same identity again returns the stored row (idempotent retry); a
    /// row with the same timeline id or the same (owner, operation key) but a different request is a Conflict.
    /// </summary>
    public Task<Result<SoloBranchRecord, SoloError>> RegisterBranchAsync(SoloBranchRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Status != SoloOriginalStatus.Recording)
        {
            throw new ArgumentException("A branch is registered as Recording.", nameof(record));
        }

        return SoloResult.GuardAsync(async () =>
        {
            await using (var insert = _dataSource.CreateCommand(
                "INSERT INTO ghostlap.solo_branches (timeline_id, source_timeline_id, fork_tick, owner_subject_id, owner_kind, " +
                "tenant_id, operation_key, rules_version, layout_version, seed, mascot_id, status) " +
                "VALUES (@id, @source, @fork, @owner, @kind, @tenant, @key, @rules, @layout, @seed, @mascot, 'Recording') " +
                "ON CONFLICT DO NOTHING"))
            {
                insert.Parameters.AddWithValue("id", record.Timeline.Value);
                insert.Parameters.AddWithValue("source", record.Source.Value);
                insert.Parameters.AddWithValue("fork", record.ForkTick);
                insert.Parameters.AddWithValue("owner", record.OwnerSubjectId);
                insert.Parameters.AddWithValue("kind", record.OwnerKind.ToString());
                insert.Parameters.AddWithValue("tenant", (object?)record.TenantId ?? DBNull.Value);
                insert.Parameters.AddWithValue("key", record.OperationKey);
                insert.Parameters.AddWithValue("rules", record.RulesVersion);
                insert.Parameters.AddWithValue("layout", record.LayoutVersion);
                insert.Parameters.AddWithValue("seed", record.Seed);
                insert.Parameters.AddWithValue("mascot", (short)record.MascotId);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var stored = await ReadBranchAsync(record.Timeline, ct).ConfigureAwait(false);
            return stored is not null && stored.SameIdentity(record)
                ? SoloResult.Ok(stored)
                : SoloResult.Fail<SoloBranchRecord>(SoloErrorCode.Conflict,
                    "A different branch already uses this timeline id or operation key.");
        }, ct);
    }

    /// <summary>The stored branch row, or NotFound.</summary>
    public Task<Result<SoloBranchRecord, SoloError>> FindBranchAsync(TimelineId timeline, CancellationToken ct) =>
        SoloResult.GuardAsync(async () =>
        {
            var stored = await ReadBranchAsync(timeline, ct).ConfigureAwait(false);
            return stored is null
                ? SoloResult.Fail<SoloBranchRecord>(SoloErrorCode.NotFound, "No such branch.")
                : SoloResult.Ok(stored);
        }, ct);

    /// <summary>The single Recording to Complete transition of a branch; same contract as <see cref="MarkCompleteAsync"/>.</summary>
    public Task<Result<SoloBranchRecord, SoloError>> MarkBranchCompleteAsync(
        TimelineId timeline, long finalTick, string finalStateHash, string lastRecordHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalStateHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastRecordHash);
        return SoloResult.GuardAsync(async () =>
        {
            await using (var update = _dataSource.CreateCommand(
                "UPDATE ghostlap.solo_branches SET status = 'Complete', final_tick = @tick, final_state_hash = @state, " +
                "last_record_hash = @record, completed_at = now() WHERE timeline_id = @id AND status = 'Recording'"))
            {
                update.Parameters.AddWithValue("id", timeline.Value);
                update.Parameters.AddWithValue("tick", finalTick);
                update.Parameters.AddWithValue("state", finalStateHash);
                update.Parameters.AddWithValue("record", lastRecordHash);
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var stored = await ReadBranchAsync(timeline, ct).ConfigureAwait(false);
            if (stored is null)
            {
                return SoloResult.Fail<SoloBranchRecord>(SoloErrorCode.NotFound, "No such branch.");
            }

            return stored.Status == SoloOriginalStatus.Complete && stored.FinalTick == finalTick
                   && stored.FinalStateHash == finalStateHash && stored.LastRecordHash == lastRecordHash
                ? SoloResult.Ok(stored)
                : SoloResult.Fail<SoloBranchRecord>(SoloErrorCode.Conflict, "The branch was already completed with different facts.");
        }, ct);
    }

    /// <summary>The actor's branch for an operation key, or null. Driver failures propagate (callers guard them).</summary>
    internal async Task<SoloBranchRecord?> ReadBranchByKeyAsync(string ownerSubjectId, string operationKey, CancellationToken ct)
    {
        await using var select = _dataSource.CreateCommand(
            $"SELECT {BranchColumns} FROM ghostlap.solo_branches WHERE owner_subject_id = @owner AND operation_key = @key");
        select.Parameters.AddWithValue("owner", ownerSubjectId);
        select.Parameters.AddWithValue("key", operationKey);
        return await ReadBranchRowAsync(select, ct).ConfigureAwait(false);
    }

    /// <summary>The stored branch row or null. Driver failures propagate as exceptions (callers guard them).</summary>
    internal async Task<SoloBranchRecord?> ReadBranchAsync(TimelineId timeline, CancellationToken ct)
    {
        await using var select = _dataSource.CreateCommand($"SELECT {BranchColumns} FROM ghostlap.solo_branches WHERE timeline_id = @id");
        select.Parameters.AddWithValue("id", timeline.Value);
        return await ReadBranchRowAsync(select, ct).ConfigureAwait(false);
    }

    private static async Task<SoloBranchRecord?> ReadBranchRowAsync(Npgsql.NpgsqlCommand select, CancellationToken ct)
    {
        await using var r = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new SoloBranchRecord(
            new TimelineId(r.GetGuid(0)),
            new TimelineId(r.GetGuid(1)),
            r.GetInt64(2),
            r.GetString(3),
            Enum.Parse<PrincipalKind>(r.GetString(4), ignoreCase: false),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            r.GetInt64(9),
            checked((byte)r.GetInt16(10)),
            Enum.Parse<SoloOriginalStatus>(r.GetString(11), ignoreCase: false),
            r.IsDBNull(12) ? null : r.GetInt64(12),
            r.IsDBNull(13) ? null : r.GetString(13),
            r.IsDBNull(14) ? null : r.GetString(14));
    }
}
