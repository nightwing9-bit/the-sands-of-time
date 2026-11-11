using Marrow.Kernel.Security;
using Npgsql;
using Revenant.Contracts;

namespace Revenant.Payments;

/// <summary>Resource and membership kinds the Revenant domain registers with the shared authorization ports.</summary>
public static class RevenantResources
{
    /// <summary>A seeded run (the ledger namespace). Id is the <c>ScenarioRunId</c> in canonical <c>N</c> form.</summary>
    public const string RunKind = "revenant-run";

    public static ResourceRef Run(ScenarioRunId runId) => new(RunKind, runId.ToString());

    public static MembershipScope RunStaff(ScenarioRunId runId) => new(RunKind, runId.ToString());
}

/// <summary>
/// Revenant's implementation of the shared <see cref="IResourceOwnershipLookup"/> and <see cref="IMembershipLookup"/> ports over
/// the run directory (<c>revenant.scenario_run</c>, <c>scenario_run_staff</c>). Both answer only from persisted server state.
/// The directory is the one deliberate non-RLS read: it must be consulted to learn WHICH tenant context to set, and it holds
/// only ids, tenant, owner and staff subjects.
/// </summary>
public sealed class RevenantRunDirectory(NpgsqlDataSource dataSource) : IResourceOwnershipLookup, IMembershipLookup
{
    public async ValueTask<ResourceOwnership?> FindOwnershipAsync(ResourceRef resource, CancellationToken ct)
    {
        if (resource.Kind != RevenantResources.RunKind || !Guid.TryParseExact(resource.Id, "N", out var runId))
        {
            return null;
        }
        await using var cmd = dataSource.CreateCommand("SELECT owner_subject_id, tenant_id FROM revenant.scenario_run WHERE id = @id");
        cmd.Parameters.AddWithValue("id", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ResourceOwnership(resource, reader.GetString(0), PrincipalKind.Staff, reader.GetString(1))
            : null;
    }

    public async ValueTask<bool> IsMemberAsync(string subjectId, PrincipalKind kind, MembershipScope scope, CancellationToken ct)
    {
        if (kind != PrincipalKind.Staff || scope.Kind != RevenantResources.RunKind || !Guid.TryParseExact(scope.Id, "N", out var runId)
            || string.IsNullOrEmpty(subjectId))
        {
            return false;
        }
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT 1 FROM revenant.scenario_run_staff s
             WHERE s.scenario_run_id = @run AND s.subject_id = @subject
               AND NOT EXISTS (SELECT 1 FROM revenant.scenario_run_staff_revocation r
                                WHERE r.scenario_run_id = s.scenario_run_id AND r.subject_id = s.subject_id)
            """);
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.AddWithValue("subject", subjectId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}

/// <summary>
/// Staff workflow authorization for Revenant runs, composed from the shared ports instead of a generic owner-only check.
/// All of these must hold: an authenticated Staff principal with the operator or supervisor role; a tenant on the actor
/// (the identity layer re-reads the user's active tenant membership on every request and hub check, so a revoked membership
/// leaves no tenant and is denied here); the run exists in the ownership lookup; the actor's tenant equals the run's persisted
/// tenant; and the subject is a current (non-revoked) member of the run's staff scope (the owner is enrolled at creation).
/// Reads and writes both need membership; there is no role bypass, so a supervisor of one run gains nothing on another.
/// Every refusal is reported to the caller identically (no existence oracle); the <see cref="AccessDenial"/> is diagnostic only.
/// Implements both shared ports, so API handlers and realtime hub subscriptions (and their periodic revalidation) use one policy.
/// </summary>
public sealed class RevenantStaffAuthorizer(IResourceOwnershipLookup ownership, IMembershipLookup membership)
    : IResourceAuthorizer, IMembershipAuthorizer
{
    /// <summary>Run staff scope (<see cref="RevenantResources.RunStaff"/>), e.g. a hub stream: the same rules as reading the run.</summary>
    public ValueTask<AccessDecision> AuthorizeAsync(IActorContext actor, MembershipScope scope, CancellationToken ct) =>
        AuthorizeAsync(actor, new ResourceRef(scope.Kind, scope.Id), ResourceOperation.Read, ct);

    public async ValueTask<AccessDecision> AuthorizeAsync(
        IActorContext actor, ResourceRef resource, ResourceOperation operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAuthenticated || string.IsNullOrEmpty(actor.SubjectId))
        {
            return AccessDecision.Deny(AccessDenial.Unauthenticated);
        }
        if (actor.PrincipalKind != PrincipalKind.Staff)
        {
            return AccessDecision.Deny(AccessDenial.OwnerKindMismatch);
        }
        if (resource.Kind != RevenantResources.RunKind)
        {
            return AccessDecision.Deny(AccessDenial.NoPolicy);
        }
        if (!HasStaffRole(actor))
        {
            return AccessDecision.Deny(AccessDenial.NotMember);
        }
        var found = await ownership.FindOwnershipAsync(resource, ct).ConfigureAwait(false);
        if (found is null || found.Resource != resource)
        {
            return AccessDecision.Deny(AccessDenial.UnknownResource);
        }
        if (actor.TenantId is null || !string.Equals(actor.TenantId, found.TenantId, StringComparison.Ordinal))
        {
            return AccessDecision.Deny(AccessDenial.TenantMismatch);
        }
        return await membership.IsMemberAsync(
                actor.SubjectId, actor.PrincipalKind, new MembershipScope(resource.Kind, resource.Id), ct).ConfigureAwait(false)
            ? AccessDecision.Allow
            : AccessDecision.Deny(AccessDenial.NotMember);
    }

    internal static bool HasStaffRole(IActorContext actor) =>
        actor.Roles.Contains("operator", StringComparer.Ordinal) || actor.Roles.Contains("supervisor", StringComparer.Ordinal);
}
