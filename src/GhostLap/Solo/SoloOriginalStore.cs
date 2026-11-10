using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Marrow.Timeline;
using Npgsql;

namespace GhostLap.Solo;

public enum SoloOriginalStatus
{
    /// <summary>Ticks are still being appended, or recording stopped before completion. Never presented as an original.</summary>
    Recording,
    /// <summary>All ticks committed, the Timeline history completed to Verified and its replay matched the live run.</summary>
    Complete,
}

/// <summary>
/// The Ghost Lap-owned row of one solo original (<c>ghostlap.solo_originals</c>). The owner fields come from the verified
/// server actor when recording started; the completion fields are set once, after verification.
/// </summary>
public sealed record SoloOriginalRecord(
    TimelineId Timeline,
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
    /// <summary>True when both rows name the same owner and game; lifecycle fields are ignored.</summary>
    public bool SameIdentity(SoloOriginalRecord other) =>
        Timeline == other.Timeline && OwnerSubjectId == other.OwnerSubjectId && OwnerKind == other.OwnerKind
        && TenantId == other.TenantId && OperationKey == other.OperationKey && RulesVersion == other.RulesVersion
        && LayoutVersion == other.LayoutVersion && Seed == other.Seed && MascotId == other.MascotId;
}

/// <summary>
/// PostgreSQL access to <c>ghostlap.solo_originals</c> and (task 6.4) <c>ghostlap.solo_branches</c>, the only tables
/// Ghost Lap writes. The login needs the <c>ghostlap_runtime</c> role (SELECT, INSERT, UPDATE of completion columns). Timestamps come from the database.
/// Driver failures and cancellation are returned as typed <see cref="SoloError"/>s.
/// </summary>
public sealed partial class SoloOriginalStore(NpgsqlDataSource dataSource)
{
    private const string Columns =
        "timeline_id, owner_subject_id, owner_kind, tenant_id, operation_key, rules_version, layout_version, seed, mascot_id, " +
        "status, final_tick, final_state_hash, last_record_hash";

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>
    /// Inserts a Recording row. Registering the same identity again returns the stored row (idempotent retry); a row
    /// with the same timeline id or the same (owner, operation key) but different identity is a Conflict.
    /// </summary>
    public Task<Result<SoloOriginalRecord, SoloError>> RegisterAsync(SoloOriginalRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Status != SoloOriginalStatus.Recording)
        {
            throw new ArgumentException("A solo original is registered as Recording.", nameof(record));
        }

        return SoloResult.GuardAsync(async () =>
        {
            await using (var insert = _dataSource.CreateCommand(
                "INSERT INTO ghostlap.solo_originals (timeline_id, owner_subject_id, owner_kind, tenant_id, operation_key, " +
                "rules_version, layout_version, seed, mascot_id, status) " +
                "VALUES (@id, @owner, @kind, @tenant, @key, @rules, @layout, @seed, @mascot, 'Recording') ON CONFLICT DO NOTHING"))
            {
                insert.Parameters.AddWithValue("id", record.Timeline.Value);
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

            var stored = await ReadAsync(record.Timeline, ct).ConfigureAwait(false);
            return stored is not null && stored.SameIdentity(record)
                ? SoloResult.Ok(stored)
                : SoloResult.Fail<SoloOriginalRecord>(SoloErrorCode.Conflict,
                    "A different solo original already uses this timeline id or operation key.");
        }, ct);
    }

    /// <summary>The stored row, or NotFound.</summary>
    public Task<Result<SoloOriginalRecord, SoloError>> FindAsync(TimelineId timeline, CancellationToken ct) =>
        SoloResult.GuardAsync(async () =>
        {
            var stored = await ReadAsync(timeline, ct).ConfigureAwait(false);
            return stored is null
                ? SoloResult.Fail<SoloOriginalRecord>(SoloErrorCode.NotFound, "No such solo original.")
                : SoloResult.Ok(stored);
        }, ct);

    /// <summary>
    /// The single Recording to Complete transition. Repeating it with the same facts returns the stored row; different facts
    /// for an already Complete row are a Conflict (the database guard also refuses any later change).
    /// </summary>
    public Task<Result<SoloOriginalRecord, SoloError>> MarkCompleteAsync(
        TimelineId timeline, long finalTick, string finalStateHash, string lastRecordHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalStateHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastRecordHash);
        return SoloResult.GuardAsync(async () =>
        {
            await using (var update = _dataSource.CreateCommand(
                "UPDATE ghostlap.solo_originals SET status = 'Complete', final_tick = @tick, final_state_hash = @state, " +
                "last_record_hash = @record, completed_at = now() WHERE timeline_id = @id AND status = 'Recording'"))
            {
                update.Parameters.AddWithValue("id", timeline.Value);
                update.Parameters.AddWithValue("tick", finalTick);
                update.Parameters.AddWithValue("state", finalStateHash);
                update.Parameters.AddWithValue("record", lastRecordHash);
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var stored = await ReadAsync(timeline, ct).ConfigureAwait(false);
            if (stored is null)
            {
                return SoloResult.Fail<SoloOriginalRecord>(SoloErrorCode.NotFound, "No such solo original.");
            }

            return stored.Status == SoloOriginalStatus.Complete && stored.FinalTick == finalTick
                   && stored.FinalStateHash == finalStateHash && stored.LastRecordHash == lastRecordHash
                ? SoloResult.Ok(stored)
                : SoloResult.Fail<SoloOriginalRecord>(SoloErrorCode.Conflict, "The original was already completed with different facts.");
        }, ct);
    }

    /// <summary>The stored row or null. Driver failures propagate as exceptions (callers guard them).</summary>
    internal async Task<SoloOriginalRecord?> ReadAsync(TimelineId timeline, CancellationToken ct)
    {
        await using var select = _dataSource.CreateCommand($"SELECT {Columns} FROM ghostlap.solo_originals WHERE timeline_id = @id");
        select.Parameters.AddWithValue("id", timeline.Value);
        await using var r = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new SoloOriginalRecord(
            new TimelineId(r.GetGuid(0)),
            r.GetString(1),
            Enum.Parse<PrincipalKind>(r.GetString(2), ignoreCase: false),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetInt64(7),
            checked((byte)r.GetInt16(8)),
            Enum.Parse<SoloOriginalStatus>(r.GetString(9), ignoreCase: false),
            r.IsDBNull(10) ? null : r.GetInt64(10),
            r.IsDBNull(11) ? null : r.GetString(11),
            r.IsDBNull(12) ? null : r.GetString(12));
    }
}

/// <summary>
/// Ghost Lap's <see cref="IResourceOwnershipLookup"/> for the Spec 03 owner policy. It answers only for
/// <see cref="ResourceKind"/> resources that exist in <c>ghostlap.solo_originals</c> and <see cref="BranchResourceKind"/>
/// resources that exist in <c>ghostlap.solo_branches</c>; anything else is unknown, which the policy denies exactly like
/// "not the owner". Owner facts always come from the persisted row.
/// </summary>
public sealed class SoloOwnershipLookup(SoloOriginalStore store) : IResourceOwnershipLookup
{
    public const string ResourceKind = "ghost-lap.solo-original";
    public const string BranchResourceKind = "ghost-lap.solo-branch";

    private readonly SoloOriginalStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public static ResourceRef ResourceOf(TimelineId timeline) => new(ResourceKind, timeline.Value.ToString("D"));

    public static ResourceRef BranchResourceOf(TimelineId timeline) => new(BranchResourceKind, timeline.Value.ToString("D"));

    public async ValueTask<ResourceOwnership?> FindOwnershipAsync(ResourceRef resource, CancellationToken ct)
    {
        if (resource.Kind is not (ResourceKind or BranchResourceKind) || !Guid.TryParseExact(resource.Id, "D", out var id))
        {
            return null;
        }

        // A database failure propagates (it is not "no owner"); the calling handler turns it into a typed error.
        if (resource.Kind == BranchResourceKind)
        {
            var branch = await _store.ReadBranchAsync(new TimelineId(id), ct).ConfigureAwait(false);
            return branch is null ? null : new ResourceOwnership(resource, branch.OwnerSubjectId, branch.OwnerKind, branch.TenantId);
        }

        var row = await _store.ReadAsync(new TimelineId(id), ct).ConfigureAwait(false);
        return row is null ? null : new ResourceOwnership(resource, row.OwnerSubjectId, row.OwnerKind, row.TenantId);
    }
}
