namespace Marrow.Kernel.Security;

public enum ResourceOperation { Read, Write }

/// <summary>Why access was refused. Diagnostic only: callers must not reveal it to the client (no existence oracle).</summary>
public enum AccessDenial
{
    None,
    /// <summary>No policy was registered for the resource. The default is deny.</summary>
    NoPolicy,
    Unauthenticated,
    /// <summary>Resource unknown to the owning domain. Reported to the caller exactly like <see cref="NotOwner"/>.</summary>
    UnknownResource,
    NotOwner,
    OwnerKindMismatch,
    TenantMismatch,
    NotMember,
}

public readonly record struct AccessDecision(bool IsAllowed, AccessDenial Denial)
{
    public static AccessDecision Allow { get; } = new(true, AccessDenial.None);

    public static AccessDecision Deny(AccessDenial reason) =>
        reason == AccessDenial.None ? throw new ArgumentOutOfRangeException(nameof(reason)) : new(false, reason);
}

/// <summary>A domain-owned resource, e.g. kind <c>history</c> and its id. Opaque to the shared layer.</summary>
public readonly record struct ResourceRef(string Kind, string Id);

/// <summary>A domain-owned membership scope, e.g. kind <c>room</c> and its id. Opaque to the shared layer.</summary>
public readonly record struct MembershipScope(string Kind, string Id);

/// <summary>
/// Ownership facts for one resource, loaded by the owning domain from its own store. Never built from a request:
/// <paramref name="OwnerSubjectId"/> and <paramref name="TenantId"/> must come from persisted server state.
/// </summary>
public sealed record ResourceOwnership(
    ResourceRef Resource,
    string OwnerSubjectId,
    PrincipalKind OwnerKind,
    string? TenantId);

/// <summary>Implemented by each domain over its own schema. Returns null when the resource does not exist.</summary>
public interface IResourceOwnershipLookup
{
    ValueTask<ResourceOwnership?> FindOwnershipAsync(ResourceRef resource, CancellationToken ct);
}

/// <summary>Implemented by each domain over its own schema (room seats, case assignment, share grants).</summary>
public interface IMembershipLookup
{
    ValueTask<bool> IsMemberAsync(string subjectId, PrincipalKind kind, MembershipScope scope, CancellationToken ct);
}

/// <summary>
/// Port that domain handlers call before reading or writing a resource, e.g. <c>CanReadHistory(actor, history)</c>.
/// The actor is always the verified <see cref="IActorContext"/>; there is no owner parameter by design.
/// The default registration denies everything.
/// </summary>
public interface IResourceAuthorizer
{
    ValueTask<AccessDecision> AuthorizeAsync(IActorContext actor, ResourceRef resource, ResourceOperation operation, CancellationToken ct);
}

/// <summary>Port that domain handlers and hub group subscription call to check room/case membership. Default denies.</summary>
public interface IMembershipAuthorizer
{
    ValueTask<AccessDecision> AuthorizeAsync(IActorContext actor, MembershipScope scope, CancellationToken ct);
}

/// <summary>
/// Pure, domain-neutral decision rules. There is deliberately no role bypass: a supervisor or any other role does
/// not gain access to another subject's resource here. A domain that wants an override must express it in its own
/// <see cref="IResourceAuthorizer"/>.
/// </summary>
public static class ResourceAccessRules
{
    public static AccessDecision EvaluateOwnership(IActorContext actor, ResourceOwnership? ownership)
    {
        if (!actor.IsAuthenticated || string.IsNullOrEmpty(actor.SubjectId))
        {
            return AccessDecision.Deny(AccessDenial.Unauthenticated);
        }

        if (ownership is null)
        {
            return AccessDecision.Deny(AccessDenial.UnknownResource);
        }

        if (ownership.OwnerKind != actor.PrincipalKind)
        {
            return AccessDecision.Deny(AccessDenial.OwnerKindMismatch);
        }

        if (!string.Equals(ownership.TenantId, actor.TenantId, StringComparison.Ordinal))
        {
            return AccessDecision.Deny(AccessDenial.TenantMismatch);
        }

        return string.Equals(ownership.OwnerSubjectId, actor.SubjectId, StringComparison.Ordinal)
            ? AccessDecision.Allow
            : AccessDecision.Deny(AccessDenial.NotOwner);
    }
}
