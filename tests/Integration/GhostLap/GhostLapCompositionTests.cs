extern alias realtime;
using System.Security.Claims;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using Integration.Tests.Support;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Kernel.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using realtime::GhostLapHost;
using Revenant.Payments;
using Xunit;

namespace Integration.Tests.GhostLap;

/// <summary>
/// R10.20 (task 10.1): host-level resource-kind authorization composition of Ghost Lap and Revenant on the one pair of Kernel ports,
/// forged and unknown kinds, no silent services, startup validation. Everything runs through the real Hosts.Realtime Program.
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GhostLapCompositionTests(IdentityDatabase db)
{
    // Registers the Revenant ledger so AddRevenantRealtime installs RevenantStaffAuthorizer (the connection is never opened here).
    private static readonly Dictionary<string, string?> WithRevenant = new()
    {
        ["ConnectionStrings:RevenantLedger"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1",
    };

    private RealtimeHostFactory Host(
        Dictionary<string, string?>? settings = null, Action<IServiceCollection>? customize = null, bool domainPolicies = false) =>
        new(db, IdentityApiFactory.NewMasterSecret(), settings, registerDomainPolicies: domainPolicies, registerProbes: false, customize: customize);

    private static ClaimsActorContext Guest(string? id = null)
    {
        var subject = "guest:" + (id ?? Guid.NewGuid().ToString("D"));
        return Actor(subject, IdentityClaimTypes.KindGuest, IdentityRoles.Guest, IdentitySchemes.GuestSession);
    }

    private static ClaimsActorContext Staff(params string[] roles) =>
        Actor("staff:" + Guid.NewGuid().ToString("D"), IdentityClaimTypes.KindStaff, roles, IdentitySchemes.StaffBearer);

    private static ClaimsActorContext Actor(string subject, string kind, string role, string scheme) => Actor(subject, kind, [role], scheme);

    private static ClaimsActorContext Actor(string subject, string kind, string[] roles, string scheme)
    {
        var claims = new List<Claim>
        {
            new(IdentityClaimTypes.Subject, subject),
            new(IdentityClaimTypes.SessionId, Guid.NewGuid().ToString("D")),
            new(IdentityClaimTypes.Kind, kind),
        };
        claims.AddRange(roles.Select(r => new Claim(IdentityClaimTypes.Role, r)));
        return ClaimsActorContext.From(new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, IdentityClaimTypes.Subject, IdentityClaimTypes.Role)));
    }

    [Fact]
    public async Task Ghost_Lap_kinds_route_to_Ghost_Lap_and_Revenant_kinds_still_reach_the_Revenant_policy()
    {
        await using var host = Host(WithRevenant);
        Assert.Equal(
            ["ghostlap-room", "revenant-run"],
            host.Services.GetServices<Marrow.Kernel.Realtime.IRealtimeStreamSource>().Select(s => s.StreamKind).Order());
        using var scope = host.Services.CreateScope();
        var resources = Assert.IsType<KindRoutingResourceAuthorizer>(scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>());
        var membership = Assert.IsType<KindRoutingMembershipAuthorizer>(scope.ServiceProvider.GetRequiredService<IMembershipAuthorizer>());
        Assert.IsType<GhostLapAuthorizer>(resources.GhostLap);
        Assert.IsType<GhostLapAuthorizer>(membership.GhostLap);
        // Revenant's policy is the fallback of BOTH ports: it was wrapped, not replaced.
        Assert.IsType<RevenantStaffAuthorizer>(resources.Fallback);
        Assert.IsType<RevenantStaffAuthorizer>(membership.Fallback);

        // A real room with a real owner decides Ghost Lap membership.
        var owner = host.Services.GetRequiredService<IActiveRoomOwner>();
        var member = Guest();
        var room = owner.CreateRoom(new RoomCaller(member.SubjectId, "c1"), new CreateRoomRequest("r1")).Value!;
        var roomId = room.View.RoomId;
        var kind = GhostLapKinds.Room;

        Assert.True((await membership.AuthorizeAsync(member, new MembershipScope(kind, roomId), default)).IsAllowed);
        Assert.True((await resources.AuthorizeAsync(member, new ResourceRef(kind, roomId), ResourceOperation.Write, default)).IsAllowed);
        Assert.Equal(AccessDenial.NotMember, (await membership.AuthorizeAsync(Guest(), new MembershipScope(kind, roomId), default)).Denial);

        // Ghost Lap never admits staff, whatever their roles.
        Assert.Equal(AccessDenial.OwnerKindMismatch,
            (await membership.AuthorizeAsync(Staff(IdentityRoles.Supervisor), new MembershipScope(kind, roomId), default)).Denial);

        // A Revenant kind is decided by Revenant's rules, not Ghost Lap's: a guest is a kind mismatch, role-less staff is not a member.
        Assert.Equal(AccessDenial.OwnerKindMismatch,
            (await resources.AuthorizeAsync(member, new ResourceRef("revenant-run", Guid.NewGuid().ToString("N")), ResourceOperation.Read, default)).Denial);
        Assert.Equal(AccessDenial.NotMember,
            (await resources.AuthorizeAsync(Staff(), new ResourceRef("revenant-run", Guid.NewGuid().ToString("N")), ResourceOperation.Read, default)).Denial);
        Assert.Equal(AccessDenial.NotMember,
            (await membership.AuthorizeAsync(Staff(), new MembershipScope("revenant-run", Guid.NewGuid().ToString("N")), default)).Denial);
    }

    [Fact]
    public async Task Unknown_forged_and_miscased_kinds_are_denied_for_every_actor_kind_in_both_ports()
    {
        await using var host = Host(WithRevenant);
        using var scope = host.Services.CreateScope();
        var resources = scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>();
        var membership = scope.ServiceProvider.GetRequiredService<IMembershipAuthorizer>();
        var owner = host.Services.GetRequiredService<IActiveRoomOwner>();
        var player = Guest();
        var created = owner.CreateRoom(new RoomCaller(player.SubjectId, "c1"), new CreateRoomRequest("r1")).Value!;
        var roomId = created.View.RoomId;
        var kinds = new[] { "ghostlap", "ghostlap-admin", "ghostlap-room-x", "GhostLap-Room", "GHOSTLAP-ROOM", "ghostlap:room", "unknown", "history", "", "ghost-lap-room", "revenant", "revenant-run-x" };
        var actors = new IActorContext[] { player, Guest(), Staff(), Staff(IdentityRoles.Operator), Staff(IdentityRoles.Supervisor), ClaimsActorContext.Anonymous };
        foreach (var kind in kinds)
        {
            foreach (var actor in actors)
            {
                // Staff with a role would reach Revenant's ownership lookup for "revenant-run*"; those kinds are not "revenant-run" exactly
                // or are rejected before the lookup, so no database is touched here.
                foreach (var op in new[] { ResourceOperation.Read, ResourceOperation.Write })
                {
                    var r = await resources.AuthorizeAsync(actor, new ResourceRef(kind, roomId), op, default);
                    Assert.False(r.IsAllowed, $"resource kind '{kind}' allowed for {actor.SubjectId} ({op})");
                }
                var m = await membership.AuthorizeAsync(actor, new MembershipScope(kind, roomId), default);
                Assert.False(m.IsAllowed, $"membership kind '{kind}' allowed for {actor.SubjectId}");
            }
        }
        // A forged room id of the right kind: not a GUID, unknown room, and a spectator trying a write.
        Assert.Equal(AccessDenial.UnknownResource, (await resources.AuthorizeAsync(player, new ResourceRef(GhostLapKinds.Room, "../x"), ResourceOperation.Read, default)).Denial);
        Assert.Equal(AccessDenial.NotMember, (await resources.AuthorizeAsync(player, new ResourceRef(GhostLapKinds.Room, Guid.NewGuid().ToString("N")), ResourceOperation.Read, default)).Denial);
        var spectator = Guest();
        owner.JoinRoom(new RoomCaller(spectator.SubjectId, "c2"), new JoinRoomRequest("j1", created.RoomCode, true));
        Assert.True((await resources.AuthorizeAsync(spectator, new ResourceRef(GhostLapKinds.Room, roomId), ResourceOperation.Read, default)).IsAllowed);
        Assert.Equal(AccessDenial.NotMember, (await resources.AuthorizeAsync(spectator, new ResourceRef(GhostLapKinds.Room, roomId), ResourceOperation.Write, default)).Denial);
    }

    [Fact]
    public async Task Without_Revenant_the_fallback_is_the_identity_layers_deny_all()
    {
        await using var host = Host();
        using var scope = host.Services.CreateScope();
        var resources = Assert.IsType<KindRoutingResourceAuthorizer>(scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>());
        Assert.IsType<DenyAllResourceAuthorizer>(resources.Fallback);
        Assert.IsType<DenyAllMembershipAuthorizer>(Assert.IsType<KindRoutingMembershipAuthorizer>(scope.ServiceProvider.GetRequiredService<IMembershipAuthorizer>()).Fallback);
        Assert.Equal(AccessDenial.NoPolicy, (await resources.AuthorizeAsync(Guest(), new ResourceRef("anything", "x"), ResourceOperation.Read, default)).Denial);
    }

    [Fact]
    public async Task A_later_Replace_of_either_authorizer_port_stops_the_host_instead_of_silently_dropping_Ghost_Lap()
    {
        // This is exactly what AddRevenantRealtime does (services.Replace), had it been registered after AddGhostLapRealtime.
        var replaceMembership = Host(customize: s => s.Replace(ServiceDescriptor.Singleton<IMembershipAuthorizer>(new DenyAllMembershipAuthorizer())));
        var e1 = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => replaceMembership.Services));
        Assert.Contains("IMembershipAuthorizer is not the Ghost Lap kind router", Flatten(e1), StringComparison.Ordinal);

        var replaceResource = Host(customize: s => s.Replace(ServiceDescriptor.Singleton<IResourceAuthorizer>(new DenyAllResourceAuthorizer())));
        var e2 = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => replaceResource.Services));
        Assert.Contains("IResourceAuthorizer is not the Ghost Lap kind router", Flatten(e2), StringComparison.Ordinal);

        // The supported way to install a later policy: replace, then route again. Ghost Lap and the new policy both work.
        await using var routed = Host(customize: s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IMembershipAuthorizer>(new MarkerMembership()));
            s.RouteGhostLapAuthorizers();
            s.RouteGhostLapAuthorizers(); // idempotent
        });
        using var scope = routed.Services.CreateScope();
        var router = Assert.IsType<KindRoutingMembershipAuthorizer>(scope.ServiceProvider.GetRequiredService<IMembershipAuthorizer>());
        Assert.IsType<MarkerMembership>(router.Fallback);
        Assert.IsType<GhostLapAuthorizer>(router.GhostLap);
        Assert.Equal(AccessDenial.NotMember, (await router.AuthorizeAsync(Guest(), new MembershipScope("other-domain", "x"), default)).Denial);
        Assert.Equal(AccessDenial.NotMember, (await router.AuthorizeAsync(Guest(), new MembershipScope(GhostLapKinds.Room, Guid.NewGuid().ToString("N")), default)).Denial);
    }

    [Fact]
    public async Task A_missing_background_service_or_invalid_configuration_stops_the_host()
    {
        var withoutPump = Host(customize: s => s.RemoveAll<IHostedService>().AddHostedService<GhostLapCompositionVerifier>());
        var missing = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => withoutPump.Services));
        Assert.Contains("Required hosted service GhostLapPublicationPump is not registered", Flatten(missing), StringComparison.Ordinal);

        var tooManyPlayers = Host(new() { ["GhostLap:Multiplayer:MaxCompetitors"] = "9" });
        var invalid = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => tooManyPlayers.Services));
        Assert.Contains("MaxCompetitors", Flatten(invalid), StringComparison.Ordinal);

        var badOrigin = Host(new() { ["GhostLap:Multiplayer:AllowedOrigins:0"] = "game.example/with/path" });
        var origin = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => badOrigin.Services));
        Assert.Contains("AllowedOrigins", Flatten(origin), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_service_Ghost_Lap_needs_is_registered_and_one_match_registry_owns_admission_ingress_and_presence()
    {
        await using var host = Host();
        var hosted = host.Services.GetServices<IHostedService>().Select(h => h.GetType()).ToHashSet();
        foreach (var type in new[]
                 {
                     typeof(GhostLapSweepService), typeof(GhostLapPublicationPump), typeof(GhostLapNoticePump), typeof(GhostLapAccessRevalidator),
                     typeof(GhostLapCompositionVerifier), typeof(GhostLapGenerationService),
                 })
        {
            Assert.Contains(type, hosted);
        }
        // Task 10.2: the single match registry is the start admission (receipt before running), the gameplay sink and the presence target.
        var registry = host.Services.GetRequiredService<MatchRegistry>();
        Assert.Same(registry, host.Services.GetRequiredService<IMatchStartAdmission>());
        Assert.Same(registry, host.Services.GetRequiredService<IGameplayInputSink>());
        Assert.Same(registry, host.Services.GetRequiredService<IMatchPresence>());
        Assert.Same(registry, host.Services.GetRequiredService<IMatchResync>());
        Assert.IsType<GhostLapSignalRTransport>(host.Services.GetRequiredService<IMatchTransport>());
        Assert.Single(host.Services.GetServices<IMatchStartAdmission>());
        Assert.Single(host.Services.GetServices<IGameplayInputSink>());
        // The process generation is registered durably before the host serves, and the authority reuses the one Marrow guard.
        var authority = host.Services.GetRequiredService<GhostLapRealtimeAuthority>();
        Assert.True(authority.IsValid);
        Assert.Same(authority, host.Services.GetRequiredService<IRealtimeAuthority>());
        Assert.Equal(authority.GenerationId, host.Services.GetRequiredService<GhostLapAuthorityGeneration>().Id);
        Assert.Equal("Active", await db.ScalarAsync<string>(
            "SELECT status FROM ghostlap.realtime_generation WHERE generation_id = @g", ("g", authority.GenerationId)));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.realtime_generation WHERE status = 'Active'"));
        Assert.Single(host.Services.GetServices<Marrow.Infrastructure.Realtime.RealtimeInstanceGuard>());
        // One owner, shared by the hub, the stream source and the authorizer (no competing instance).
        var owner = host.Services.GetRequiredService<IActiveRoomOwner>();
        Assert.Same(owner, host.Services.GetRequiredService<ActiveRoomOwner>());
        Assert.Single(host.Services.GetServices<IActiveRoomOwner>());
        Assert.Equal(["ghostlap-room"], host.Services.GetServices<Marrow.Kernel.Realtime.IRealtimeStreamSource>().Select(s => s.StreamKind));
    }

    private static string Flatten(Exception e) => string.Join(" | ", Chain(e).Select(x => x.Message));

    private static IEnumerable<Exception> Chain(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }

    private sealed class MarkerMembership : IMembershipAuthorizer
    {
        public ValueTask<AccessDecision> AuthorizeAsync(IActorContext actor, MembershipScope scope, CancellationToken ct) =>
            ValueTask.FromResult(AccessDecision.Deny(AccessDenial.NotMember));
    }
}
