using System.Security.Claims;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.Http;

namespace Marrow.Infrastructure.Identity;

/// <summary>
/// Builds the Kernel <see cref="IActorContext"/> from a verified principal only. Request-supplied owner ids are never
/// consulted. It fails closed: no authenticated identity, a missing/unknown kind, a subject that does not match the
/// kind, or identities of different kinds or subjects in one principal all yield an unauthenticated actor.
/// Roles are filtered to the set allowed for the kind, so a guest can never carry a staff role (or the reverse).
/// </summary>
public sealed class ClaimsActorContext : IActorContext
{
    private static readonly string[] None = [];

    private ClaimsActorContext(
        string subjectId, PrincipalKind kind, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> claims, bool authenticated,
        string? tenantId = null)
    {
        SubjectId = subjectId;
        PrincipalKind = kind;
        Roles = roles;
        Claims = claims;
        IsAuthenticated = authenticated;
        TenantId = tenantId;
    }

    /// <summary>
    /// An unauthenticated actor. <see cref="PrincipalKind"/> is the least-privileged value (<c>Guest</c>) only because the
    /// frozen enum has no anonymous member; consumers must check <see cref="IsAuthenticated"/> first.
    /// </summary>
    public static ClaimsActorContext Anonymous { get; } = new(string.Empty, PrincipalKind.Guest, None, None, false);

    public string SubjectId { get; }
    public PrincipalKind PrincipalKind { get; }
    /// <summary>
    /// Staff only: the tenant the server attached after reading the user's active membership for this request
    /// (<see cref="IdentityClaimTypes.Tenant"/>). Null for guests, for staff without an active membership, and when the
    /// principal's identities disagree about the tenant (fail closed).
    /// </summary>
    public string? TenantId { get; }
    public IReadOnlyCollection<string> Roles { get; }
    public IReadOnlyCollection<string> Claims { get; }
    public bool IsAuthenticated { get; }

    public static ClaimsActorContext From(ClaimsPrincipal? principal)
    {
        var identities = principal?.Identities.Where(i => i.IsAuthenticated).ToArray() ?? [];
        if (identities.Length == 0)
        {
            return Anonymous;
        }

        string? kind = null;
        string? subject = null;
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var mfa = false;
        var tenants = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            var identityKind = identity.FindFirst(IdentityClaimTypes.Kind)?.Value;
            var identitySubject = identity.FindFirst(IdentityClaimTypes.Subject)?.Value;
            if (identityKind is null || identitySubject is null
                || (kind is not null && kind != identityKind)
                || (subject is not null && subject != identitySubject))
            {
                return Anonymous;
            }

            kind = identityKind;
            subject = identitySubject;
            foreach (var role in identity.FindAll(IdentityClaimTypes.Role))
            {
                roles.Add(role.Value);
            }

            mfa |= identity.HasClaim(IdentityClaimTypes.Mfa, "true");
            foreach (var tenant in identity.FindAll(IdentityClaimTypes.Tenant))
            {
                tenants.Add(tenant.Value);
            }
        }

        switch (kind)
        {
            case IdentityClaimTypes.KindStaff when subject!.StartsWith("staff:", StringComparison.Ordinal):
                roles.IntersectWith(IdentityRoles.StaffRoles);
                return new ClaimsActorContext(
                    subject, PrincipalKind.Staff, [.. roles.Order(StringComparer.Ordinal)], mfa ? ["mfa"] : None, true,
                    tenants.Count == 1 ? tenants.Single() : null);
            case IdentityClaimTypes.KindGuest when subject!.StartsWith("guest:", StringComparison.Ordinal):
                return new ClaimsActorContext(subject, PrincipalKind.Guest, [IdentityRoles.Guest], None, true);
            default:
                return Anonymous;
        }
    }
}

/// <summary>Scoped Kernel actor resolved from the current request's authenticated principal on every access.</summary>
public sealed class HttpActorContext(IHttpContextAccessor accessor) : IActorContext
{
    private ClaimsActorContext Current => ClaimsActorContext.From(accessor.HttpContext?.User);

    public string SubjectId => Current.SubjectId;
    public PrincipalKind PrincipalKind => Current.PrincipalKind;
    public string? TenantId => Current.TenantId;
    public IReadOnlyCollection<string> Roles => Current.Roles;
    public IReadOnlyCollection<string> Claims => Current.Claims;
    public bool IsAuthenticated => Current.IsAuthenticated;
}
