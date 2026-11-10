using System.Net;
using System.Security.Claims;
using System.Text;
using Integration.Tests.Support;
using Marrow.Infrastructure.Identity;
using Marrow.Kernel.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Integration.Tests.Identity;

/// <summary>R03.08 and R03.10: access tokens are verified and re-authorized server side; the Kernel actor is built only from verified claims.</summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ForgedTokenTests(IdentityDatabase db)
{
    private static string Mint(SecurityKey key, DateTimeOffset now, IEnumerable<Claim> claims, string issuer = "the-sands-of-time", string audience = "the-sands-of-time-api") =>
        new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(5).UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });

    private static Claim[] StaffClaims(Guid userId, Guid sessionId, string kind = "staff", params string[] roles) =>
    [
        new("sub", "staff:" + userId), new("sid", sessionId.ToString()), new("marrow_kind", kind),
        .. roles.Select(r => new Claim("role", r)),
    ];

    [Fact]
    public async Task Tokens_that_are_not_signed_by_this_server_for_a_live_session_are_rejected_and_roles_come_from_the_database()
    {
        await using var factory = new IdentityApiFactory(db);
        var staff = await factory.CreateStaffAsync(IdentityRoles.Operator);
        var other = await factory.CreateStaffAsync(IdentityRoles.Operator);
        var (browser, access, _) = await Flows.LoginAsync(factory, staff);
        var sid = Flows.SessionId(access);
        var keys = factory.Services.GetRequiredService<IdentityKeyMaterial>();
        var now = factory.Time.GetUtcNow();
        var good = keys.AccessTokenSigningKey();
        var foreignKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(IdentityApiFactory.NewMasterSecret())) { KeyId = "marrow-access-v1" };

        async Task<HttpStatusCode> Me(string token) => (await browser.GetAsync("/auth/staff/me", bearer: token, useCookies: false)).Status;

        Assert.Equal(HttpStatusCode.OK, await Me(Mint(good, now, StaffClaims(staff.Id, sid, "staff", "operator"))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Mint(foreignKey, now, StaffClaims(staff.Id, sid, "staff", "operator"))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Mint(good, now, StaffClaims(staff.Id, sid, "guest", "operator"))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Mint(good, now, StaffClaims(staff.Id, Guid.NewGuid(), "staff", "operator"))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Mint(good, now, StaffClaims(other.Id, sid, "staff", "operator")))); // someone else's session
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Mint(good, now, StaffClaims(staff.Id, sid), "the-sands-of-time", "someone-else")));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Mint(good, now, StaffClaims(staff.Id, sid), "evil-issuer")));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(Mint(good, now.AddHours(-1), StaffClaims(staff.Id, sid))));

        var unsigned = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}""")).TrimEnd('=') + "."
            + Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"sub":"staff:{{staff.Id}}","sid":"{{sid}}","marrow_kind":"staff","role":"supervisor","iss":"the-sands-of-time","aud":"the-sands-of-time-api","exp":{{now.AddMinutes(5).ToUnixTimeSeconds()}}}""")).TrimEnd('=') + ".";
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(unsigned));

        // A correctly signed token that claims "supervisor" for an operator: the database wins.
        var inflated = Mint(good, now, StaffClaims(staff.Id, sid, "staff", "operator", "supervisor"));
        var me = await browser.GetAsync("/auth/staff/me", bearer: inflated, useCookies: false);
        Assert.Equal(["operator"], me.Roles);
        var ban = await browser.PostAsync($"/auth/admin/guests/{Guid.NewGuid()}/ban", bearer: inflated);
        Assert.Equal(HttpStatusCode.Forbidden, ban.Status);
    }
}

/// <summary>The Kernel <see cref="IActorContext"/> implementation fails closed and keeps the two principal types apart.</summary>
public sealed class ActorContextTests
{
    private static ClaimsPrincipal Principal(params (string Kind, string Subject, string[] Roles, bool Mfa)[] identities) =>
        new(identities.Select(i => new ClaimsIdentity(
            [
                .. new[] { new Claim("sub", i.Subject), new Claim("marrow_kind", i.Kind) },
                .. i.Roles.Select(r => new Claim("role", r)),
                .. i.Mfa ? new[] { new Claim("marrow_mfa", "true") } : [],
            ],
            "test")));

    [Fact]
    public void Staff_actor_keeps_only_staff_roles_and_reports_mfa()
    {
        var actor = ClaimsActorContext.From(Principal(("staff", "staff:1", ["operator", "guest", "admin"], true)));
        Assert.True(actor.IsAuthenticated);
        Assert.Equal(PrincipalKind.Staff, actor.PrincipalKind);
        Assert.Equal("staff:1", actor.SubjectId);
        Assert.Equal(["operator"], actor.Roles);
        Assert.Equal(["mfa"], actor.Claims);
        Assert.Null(actor.TenantId);
    }

    [Fact]
    public void Guest_actor_never_carries_staff_roles_or_mfa()
    {
        var actor = ClaimsActorContext.From(Principal(("guest", "guest:1", ["supervisor", "operator"], true)));
        Assert.Equal(PrincipalKind.Guest, actor.PrincipalKind);
        Assert.Equal(["guest"], actor.Roles);
        Assert.Empty(actor.Claims);
    }

    [Fact]
    public void Ambiguous_or_incomplete_principals_are_unauthenticated()
    {
        Assert.False(ClaimsActorContext.From(null).IsAuthenticated);
        Assert.False(ClaimsActorContext.From(new ClaimsPrincipal(new ClaimsIdentity())).IsAuthenticated);
        Assert.False(ClaimsActorContext.From(Principal(("staff", "staff:1", ["operator"], false), ("guest", "guest:2", ["guest"], false))).IsAuthenticated);
        Assert.False(ClaimsActorContext.From(Principal(("staff", "staff:1", ["operator"], false), ("staff", "staff:2", ["operator"], false))).IsAuthenticated);
        Assert.False(ClaimsActorContext.From(Principal(("staff", "guest:1", ["operator"], false))).IsAuthenticated);
        Assert.False(ClaimsActorContext.From(Principal(("guest", "staff:1", ["guest"], false))).IsAuthenticated);
        Assert.False(ClaimsActorContext.From(Principal(("robot", "robot:1", [], false))).IsAuthenticated);
        var anonymous = ClaimsActorContext.From(null);
        Assert.Empty(anonymous.Roles);
        Assert.Empty(anonymous.Claims);
    }

    [Fact]
    public void The_same_principal_through_two_schemes_is_one_actor()
    {
        var actor = ClaimsActorContext.From(Principal(("staff", "staff:1", ["operator"], false), ("staff", "staff:1", ["supervisor"], true)));
        Assert.True(actor.IsAuthenticated);
        Assert.Equal(["operator", "supervisor"], actor.Roles);
        Assert.Equal(["mfa"], actor.Claims);
    }
}
