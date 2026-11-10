using Marrow.Kernel.Security;
using Npgsql;
namespace Sands.E2E.Backend;
/// <summary>One persisted history manifest row, as read back from PostgreSQL.</summary>
public sealed record HistoryRow(Guid Id, string DomainId, string OwnerId, Guid? ParentId, long? ForkPosition, Guid RootId);
/// <summary>
/// Reads manifest facts (domain, owner, parent, root) of fixture histories from the database through the API's own runtime
/// login (which has SELECT on <c>marrow_timeline.histories</c>). The owner is therefore always the persisted
/// <c>owner_id</c> that the engine wrote from the verified actor, never a request-supplied value.
/// </summary>
public sealed class FixtureCatalog(NpgsqlDataSource dataSource)
{
    private const string Sql = """
        WITH RECURSIVE chain AS (
          SELECT timeline_id, parent_timeline_id, 0 AS depth
          FROM marrow_timeline.histories WHERE timeline_id = @id
          UNION ALL
          SELECT h.timeline_id, h.parent_timeline_id, c.depth + 1
          FROM marrow_timeline.histories h JOIN chain c ON h.timeline_id = c.parent_timeline_id
        )
        SELECT h.timeline_id, h.domain_id, h.owner_id, h.parent_timeline_id, h.fork_position,
               (SELECT timeline_id FROM chain ORDER BY depth DESC LIMIT 1) AS root_id
        FROM marrow_timeline.histories h WHERE h.timeline_id = @id
        """;
    public async Task<HistoryRow?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new HistoryRow(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetGuid(5));
    }
}
/// <summary>
/// The domain's ownership lookup the Spec 03 <see cref="OwnerResourceAuthorizer"/> consumes. It answers only for histories whose
/// domain is one of the fixture domains; every other history (and every unknown id) is reported as unknown, which the
/// authorizer turns into the same denial as "not the owner" (no existence oracle). This is what scopes the test routes to the
/// fixture.
/// </summary>
public sealed class FixtureOwnershipLookup(FixtureCatalog catalog, FixtureDomains domains) : IResourceOwnershipLookup
{
    public const string HistoryKind = "history";
    public async ValueTask<ResourceOwnership?> FindOwnershipAsync(ResourceRef resource, CancellationToken ct)
    {
        if (resource.Kind != HistoryKind || !Guid.TryParse(resource.Id, out var id))
        {
            return null;
        }
        var row = await catalog.FindAsync(id, ct);
        if (row is null || (domains.Find(row.DomainId) is null && row.DomainId != Revenant.Timeline.FinanceTimelineVersions.DomainId))
        {
            return null;
        }
        var kind = row.OwnerId.StartsWith("staff:", StringComparison.Ordinal) ? PrincipalKind.Staff : PrincipalKind.Guest;
        return new ResourceOwnership(resource, row.OwnerId, kind, TenantId: null);
    }
}
