using Marrow.Kernel.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Marrow.Infrastructure.Identity.Policy;

/// <summary>Default <see cref="IResourceAuthorizer"/>: denies every request until a domain registers its own policy.</summary>
public sealed class DenyAllResourceAuthorizer : IResourceAuthorizer
{
    public ValueTask<AccessDecision> AuthorizeAsync(IActorContext actor, ResourceRef resource, ResourceOperation operation, CancellationToken ct) =>
        ValueTask.FromResult(AccessDecision.Deny(AccessDenial.NoPolicy));
}

/// <summary>Default <see cref="IMembershipAuthorizer"/>: denies every request until a domain registers its own policy.</summary>
public sealed class DenyAllMembershipAuthorizer : IMembershipAuthorizer
{
    public ValueTask<AccessDecision> AuthorizeAsync(IActorContext actor, MembershipScope scope, CancellationToken ct) =>
        ValueTask.FromResult(AccessDecision.Deny(AccessDenial.NoPolicy));
}

/// <summary>
/// Domain-neutral owner-match policy over a domain-supplied <see cref="IResourceOwnershipLookup"/>. The owner and
/// tenant come from the domain's persisted state; the only request-derived input is the resource reference, and the
/// actor is the verified <see cref="IActorContext"/>. Read and write both require the owner (no role bypass).
/// </summary>
public sealed class OwnerResourceAuthorizer(IResourceOwnershipLookup lookup) : IResourceAuthorizer
{
    public async ValueTask<AccessDecision> AuthorizeAsync(IActorContext actor, ResourceRef resource, ResourceOperation operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAuthenticated || string.IsNullOrEmpty(actor.SubjectId))
        {
            return AccessDecision.Deny(AccessDenial.Unauthenticated);
        }

        if (string.IsNullOrEmpty(resource.Kind) || string.IsNullOrEmpty(resource.Id))
        {
            return AccessDecision.Deny(AccessDenial.UnknownResource);
        }

        var ownership = await lookup.FindOwnershipAsync(resource, ct).ConfigureAwait(false);
        // A lookup that answers for a different resource than the one asked about is treated as unknown (fail closed).
        if (ownership is not null && ownership.Resource != resource)
        {
            ownership = null;
        }

        return ResourceAccessRules.EvaluateOwnership(actor, ownership);
    }
}

/// <summary>Domain-neutral membership policy over a domain-supplied <see cref="IMembershipLookup"/>. No role bypass.</summary>
public sealed class LookupMembershipAuthorizer(IMembershipLookup lookup) : IMembershipAuthorizer
{
    public async ValueTask<AccessDecision> AuthorizeAsync(IActorContext actor, MembershipScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAuthenticated || string.IsNullOrEmpty(actor.SubjectId))
        {
            return AccessDecision.Deny(AccessDenial.Unauthenticated);
        }

        if (string.IsNullOrEmpty(scope.Kind) || string.IsNullOrEmpty(scope.Id))
        {
            return AccessDecision.Deny(AccessDenial.NotMember);
        }

        return await lookup.IsMemberAsync(actor.SubjectId, actor.PrincipalKind, scope, ct).ConfigureAwait(false)
            ? AccessDecision.Allow
            : AccessDecision.Deny(AccessDenial.NotMember);
    }
}

public static class DomainPolicyServiceCollectionExtensions
{
    /// <summary>
    /// Registers deny-all defaults for <see cref="IResourceAuthorizer"/> and <see cref="IMembershipAuthorizer"/> unless a
    /// domain already registered its own. Safe to call more than once. Called by <c>AddMarrowIdentity</c>.
    /// </summary>
    public static IServiceCollection AddMarrowDomainPolicyDefaults(this IServiceCollection services)
    {
        services.TryAddSingleton<IResourceAuthorizer, DenyAllResourceAuthorizer>();
        services.TryAddSingleton<IMembershipAuthorizer, DenyAllMembershipAuthorizer>();
        return services;
    }

    /// <summary>Replaces the default with the owner-match policy over the domain's own ownership lookup.</summary>
    public static IServiceCollection AddMarrowResourceOwnership<TLookup>(this IServiceCollection services)
        where TLookup : class, IResourceOwnershipLookup
    {
        services.Replace(ServiceDescriptor.Scoped<IResourceOwnershipLookup, TLookup>());
        services.Replace(ServiceDescriptor.Scoped<IResourceAuthorizer, OwnerResourceAuthorizer>());
        return services;
    }

    /// <summary>Replaces the default with the membership policy over the domain's own membership lookup.</summary>
    public static IServiceCollection AddMarrowMembership<TLookup>(this IServiceCollection services)
        where TLookup : class, IMembershipLookup
    {
        services.Replace(ServiceDescriptor.Scoped<IMembershipLookup, TLookup>());
        services.Replace(ServiceDescriptor.Scoped<IMembershipAuthorizer, LookupMembershipAuthorizer>());
        return services;
    }
}
