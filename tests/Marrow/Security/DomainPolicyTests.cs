using System.Reflection;
using System.Security.Claims;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Kernel.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Marrow.Tests.Security;

/// <summary>
/// Task 3.2: resource ownership / membership ports and the default-deny registration. Actors come from the real
/// <see cref="ClaimsActorContext"/>; the lookups are in-memory stores standing in for a domain's own schema.
/// </summary>
public sealed class DomainPolicyTests
{
    private static readonly ResourceRef History1 = new("history", "h-1");
    private static readonly ResourceRef History2 = new("history", "h-2");

    private sealed class MemoryOwnership(params ResourceOwnership[] rows) : IResourceOwnershipLookup
    {
        private readonly Dictionary<ResourceRef, ResourceOwnership> _rows = rows.ToDictionary(r => r.Resource);
        public ValueTask<ResourceOwnership?> FindOwnershipAsync(ResourceRef resource, CancellationToken ct) =>
            ValueTask.FromResult(_rows.GetValueOrDefault(resource));
    }

    private sealed class MemoryMembership(params (string Subject, PrincipalKind Kind, MembershipScope Scope)[] rows) : IMembershipLookup
    {
        public ValueTask<bool> IsMemberAsync(string subjectId, PrincipalKind kind, MembershipScope scope, CancellationToken ct) =>
            ValueTask.FromResult(rows.Any(r => r.Subject == subjectId && r.Kind == kind && r.Scope == scope));
    }

    private sealed class FixedActor(string subject, PrincipalKind kind, string? tenant) : IActorContext
    {
        public string SubjectId => subject;
        public PrincipalKind PrincipalKind => kind;
        public string? TenantId => tenant;
        public IReadOnlyCollection<string> Roles { get; } = [];
        public IReadOnlyCollection<string> Claims { get; } = [];
        public bool IsAuthenticated => true;
    }

    private static ClaimsActorContext Actor(string kind, string subject, params string[] roles)
    {
        var claims = new List<Claim> { new(IdentityClaimTypes.Kind, kind), new(IdentityClaimTypes.Subject, subject) };
        claims.AddRange(roles.Select(r => new Claim(IdentityClaimTypes.Role, r)));
        return ClaimsActorContext.From(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
    }

    private static ClaimsActorContext Guest(string id) => Actor(IdentityClaimTypes.KindGuest, $"guest:{id}", IdentityRoles.Guest);
    private static ClaimsActorContext Staff(string id, string role = IdentityRoles.Operator) => Actor(IdentityClaimTypes.KindStaff, $"staff:{id}", role);

    private static ResourceOwnership Owned(ResourceRef r, IActorContext owner, string? tenant = null) =>
        new(r, owner.SubjectId, owner.PrincipalKind, tenant);

    [Fact]
    public async Task Two_owners_each_read_and_write_only_their_own_history()
    {
        var a = Guest("a");
        var b = Guest("b");
        var authorizer = new OwnerResourceAuthorizer(new MemoryOwnership(Owned(History1, a), Owned(History2, b)));

        Assert.True((await authorizer.AuthorizeAsync(a, History1, ResourceOperation.Read, default)).IsAllowed);
        Assert.True((await authorizer.AuthorizeAsync(a, History1, ResourceOperation.Write, default)).IsAllowed);
        Assert.True((await authorizer.AuthorizeAsync(b, History2, ResourceOperation.Read, default)).IsAllowed);

        foreach (var op in new[] { ResourceOperation.Read, ResourceOperation.Write })
        {
            Assert.Equal(AccessDecision.Deny(AccessDenial.NotOwner), await authorizer.AuthorizeAsync(b, History1, op, default));
            Assert.Equal(AccessDecision.Deny(AccessDenial.NotOwner), await authorizer.AuthorizeAsync(a, History2, op, default));
        }
    }

    [Fact]
    public async Task Guessing_an_unknown_id_is_denied_and_the_actor_is_never_taken_from_the_request()
    {
        var authorizer = new OwnerResourceAuthorizer(new MemoryOwnership(Owned(History1, Guest("a"))));

        var decision = await authorizer.AuthorizeAsync(Guest("b"), new ResourceRef("history", "h-guess"), ResourceOperation.Read, default);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AccessDenial.UnknownResource, decision.Denial);

        // The port exposes no client-suppliable owner: the only string-bearing inputs are the opaque resource/scope refs.
        foreach (var method in new[] { typeof(IResourceAuthorizer), typeof(IMembershipAuthorizer) }.Select(t => t.GetMethod("AuthorizeAsync")!))
        {
            Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(string));
            Assert.Contains(method.GetParameters(), p => p.ParameterType == typeof(IActorContext));
        }
    }

    [Fact]
    public async Task Cross_tenant_access_is_denied_even_for_the_same_subject()
    {
        var owner = new FixedActor("staff:s1", PrincipalKind.Staff, "tenant-1");
        var sameSubjectOtherTenant = new FixedActor("staff:s1", PrincipalKind.Staff, "tenant-2");
        var noTenant = new FixedActor("staff:s1", PrincipalKind.Staff, null);
        var authorizer = new OwnerResourceAuthorizer(new MemoryOwnership(Owned(History1, owner, "tenant-1")));

        Assert.True((await authorizer.AuthorizeAsync(owner, History1, ResourceOperation.Read, default)).IsAllowed);
        Assert.Equal(AccessDenial.TenantMismatch, (await authorizer.AuthorizeAsync(sameSubjectOtherTenant, History1, ResourceOperation.Read, default)).Denial);
        Assert.Equal(AccessDenial.TenantMismatch, (await authorizer.AuthorizeAsync(noTenant, History1, ResourceOperation.Write, default)).Denial);
    }

    [Fact]
    public async Task A_tenant_actor_cannot_reach_an_untenanted_resource()
    {
        var actor = new FixedActor("staff:s1", PrincipalKind.Staff, "tenant-1");
        var authorizer = new OwnerResourceAuthorizer(new MemoryOwnership(Owned(History1, actor, tenant: null)));

        Assert.Equal(AccessDenial.TenantMismatch, (await authorizer.AuthorizeAsync(actor, History1, ResourceOperation.Read, default)).Denial);
    }

    [Fact]
    public async Task No_role_or_principal_kind_bypasses_ownership()
    {
        var guestOwner = Guest("a");
        var staffOwner = Staff("s1");
        var authorizer = new OwnerResourceAuthorizer(new MemoryOwnership(Owned(History1, guestOwner), Owned(History2, staffOwner)));

        // Supervisor does not read a guest's history; a guest does not read a staff-owned one.
        Assert.Equal(AccessDenial.OwnerKindMismatch, (await authorizer.AuthorizeAsync(Staff("s2", IdentityRoles.Supervisor), History1, ResourceOperation.Read, default)).Denial);
        Assert.Equal(AccessDenial.OwnerKindMismatch, (await authorizer.AuthorizeAsync(guestOwner, History2, ResourceOperation.Read, default)).Denial);
        // Another supervisor is still not the owner of a staff resource.
        Assert.Equal(AccessDenial.NotOwner, (await authorizer.AuthorizeAsync(Staff("s2", IdentityRoles.Supervisor), History2, ResourceOperation.Read, default)).Denial);
        // Same subject string under the other kind does not match.
        var impostor = new FixedActor(guestOwner.SubjectId, PrincipalKind.Staff, null);
        Assert.Equal(AccessDenial.OwnerKindMismatch, (await authorizer.AuthorizeAsync(impostor, History1, ResourceOperation.Read, default)).Denial);
    }

    [Fact]
    public async Task Unauthenticated_and_inconsistent_principals_are_denied_before_any_lookup()
    {
        var authorizer = new OwnerResourceAuthorizer(new MemoryOwnership(Owned(History1, Guest("a"))));
        var mixedKinds = ClaimsActorContext.From(new ClaimsPrincipal(
        [
            new ClaimsIdentity([new Claim(IdentityClaimTypes.Kind, IdentityClaimTypes.KindGuest), new Claim(IdentityClaimTypes.Subject, "guest:a")], "t"),
            new ClaimsIdentity([new Claim(IdentityClaimTypes.Kind, IdentityClaimTypes.KindStaff), new Claim(IdentityClaimTypes.Subject, "guest:a")], "t"),
        ]));

        Assert.Equal(AccessDenial.Unauthenticated, (await authorizer.AuthorizeAsync(ClaimsActorContext.Anonymous, History1, ResourceOperation.Read, default)).Denial);
        Assert.Equal(AccessDenial.Unauthenticated, (await authorizer.AuthorizeAsync(mixedKinds, History1, ResourceOperation.Read, default)).Denial);
    }

    [Fact]
    public async Task A_lookup_that_answers_for_a_different_resource_fails_closed()
    {
        var a = Guest("a");
        var authorizer = new OwnerResourceAuthorizer(new WrongResourceLookup(Owned(History1, a)));

        Assert.Equal(AccessDenial.UnknownResource, (await authorizer.AuthorizeAsync(a, History2, ResourceOperation.Read, default)).Denial);
    }

    private sealed class WrongResourceLookup(ResourceOwnership row) : IResourceOwnershipLookup
    {
        public ValueTask<ResourceOwnership?> FindOwnershipAsync(ResourceRef resource, CancellationToken ct) => ValueTask.FromResult<ResourceOwnership?>(row);
    }

    [Fact]
    public async Task Membership_is_per_scope_and_per_principal_kind_with_no_role_bypass()
    {
        var room = new MembershipScope("room", "r-1");
        var other = new MembershipScope("room", "r-2");
        var member = Guest("a");
        var authorizer = new LookupMembershipAuthorizer(new MemoryMembership((member.SubjectId, PrincipalKind.Guest, room)));

        Assert.True((await authorizer.AuthorizeAsync(member, room, default)).IsAllowed);
        Assert.Equal(AccessDenial.NotMember, (await authorizer.AuthorizeAsync(member, other, default)).Denial);
        Assert.Equal(AccessDenial.NotMember, (await authorizer.AuthorizeAsync(Guest("b"), room, default)).Denial);
        Assert.Equal(AccessDenial.NotMember, (await authorizer.AuthorizeAsync(Staff("s1", IdentityRoles.Supervisor), room, default)).Denial);
        Assert.Equal(AccessDenial.Unauthenticated, (await authorizer.AuthorizeAsync(ClaimsActorContext.Anonymous, room, default)).Denial);
    }

    [Fact]
    public async Task Defaults_deny_everything_including_the_would_be_owner()
    {
        using var provider = new ServiceCollection().AddMarrowDomainPolicyDefaults().BuildServiceProvider(validateScopes: true);
        var a = Guest("a");

        var decision = await provider.GetRequiredService<IResourceAuthorizer>().AuthorizeAsync(a, History1, ResourceOperation.Read, default);
        var membership = await provider.GetRequiredService<IMembershipAuthorizer>().AuthorizeAsync(a, new MembershipScope("room", "r-1"), default);

        Assert.Equal(AccessDecision.Deny(AccessDenial.NoPolicy), decision);
        Assert.Equal(AccessDecision.Deny(AccessDenial.NoPolicy), membership);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_domain_policy_replaces_the_default_regardless_of_registration_order(bool defaultsFirst)
    {
        var services = new ServiceCollection();
        var a = Guest("a");
        if (defaultsFirst)
        {
            services.AddMarrowDomainPolicyDefaults();
        }

        services.AddSingleton<IResourceOwnershipLookup>(new MemoryOwnership(Owned(History1, a)));
        services.AddSingleton<IMembershipLookup>(new MemoryMembership((a.SubjectId, PrincipalKind.Guest, new MembershipScope("room", "r-1"))));
        services.Replace(ServiceDescriptor.Scoped<IResourceAuthorizer, OwnerResourceAuthorizer>());
        services.Replace(ServiceDescriptor.Scoped<IMembershipAuthorizer, LookupMembershipAuthorizer>());
        if (!defaultsFirst)
        {
            services.AddMarrowDomainPolicyDefaults();
        }

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.True((await scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>().AuthorizeAsync(a, History1, ResourceOperation.Read, default)).IsAllowed);
        Assert.True((await scope.ServiceProvider.GetRequiredService<IMembershipAuthorizer>().AuthorizeAsync(a, new MembershipScope("room", "r-1"), default)).IsAllowed);
        Assert.False((await scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>().AuthorizeAsync(Guest("b"), History1, ResourceOperation.Read, default)).IsAllowed);
    }

    [Fact]
    public async Task Domain_registration_helpers_wire_the_owner_policy_over_the_domain_lookup()
    {
        var services = new ServiceCollection()
            .AddMarrowDomainPolicyDefaults()
            .AddMarrowResourceOwnership<StaticOwnership>()
            .AddMarrowMembership<StaticMembership>();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var resources = scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>();
        Assert.IsType<OwnerResourceAuthorizer>(resources);
        Assert.True((await resources.AuthorizeAsync(Guest("a"), History1, ResourceOperation.Read, default)).IsAllowed);
        Assert.False((await resources.AuthorizeAsync(Guest("b"), History1, ResourceOperation.Read, default)).IsAllowed);
        Assert.IsType<LookupMembershipAuthorizer>(scope.ServiceProvider.GetRequiredService<IMembershipAuthorizer>());
    }

    private sealed class StaticOwnership : IResourceOwnershipLookup
    {
        public ValueTask<ResourceOwnership?> FindOwnershipAsync(ResourceRef resource, CancellationToken ct) =>
            ValueTask.FromResult<ResourceOwnership?>(resource == History1 ? new ResourceOwnership(History1, "guest:a", PrincipalKind.Guest, null) : null);
    }

    private sealed class StaticMembership : IMembershipLookup
    {
        public ValueTask<bool> IsMemberAsync(string subjectId, PrincipalKind kind, MembershipScope scope, CancellationToken ct) => ValueTask.FromResult(false);
    }
}
