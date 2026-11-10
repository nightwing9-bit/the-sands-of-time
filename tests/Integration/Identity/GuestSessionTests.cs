using System.Net;
using System.Security.Cryptography;
using System.Text;
using Integration.Tests.Support;
using Xunit;

namespace Integration.Tests.Identity;

/// <summary>R03.02 and R03.08: remembered guest sessions that need no account, and strict separation from staff.</summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GuestSessionTests(IdentityDatabase db)
{
    [Fact]
    public async Task A_new_visitor_gets_a_signed_hardened_cookie_and_resumes_the_same_guest_without_any_credentials()
    {
        await using var factory = new IdentityApiFactory(db);
        var browser = factory.NewBrowser();

        var start = await browser.PostAsync("/auth/guest/start");
        Assert.Equal(HttpStatusCode.OK, start.Status);
        Assert.False(start.Json.GetProperty("resumed").GetBoolean());
        var subject = start.Str("subject");
        Assert.StartsWith("guest:", subject, StringComparison.Ordinal);

        var cookie = browser.LastSeen[Flows.GuestCookie];
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal("Lax", cookie.SameSite, ignoreCase: true);
        Assert.Equal("/", cookie.Path);
        Assert.False(browser.LastSeen[Flows.GuestCsrf].HttpOnly);

        var me = await browser.GetAsync("/auth/guest/me");
        Assert.Equal(HttpStatusCode.OK, me.Status);
        Assert.Equal(subject, me.Str("subject"));
        Assert.Equal("Guest", me.Str("kind"));
        Assert.Equal(["guest"], me.Roles);

        var resume = await browser.PostAsync("/auth/guest/start", csrfCookie: Flows.GuestCsrf);
        Assert.Equal(subject, resume.Str("subject"));
        Assert.True(resume.Json.GetProperty("resumed").GetBoolean());
        Assert.Equal(cookie.Value, browser.Cookies[Flows.GuestCookie]);

        // Only a hash of the secret is stored.
        var secret = Flows.Secret(cookie.Value, 1);
        Assert.Equal(1L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM marrow_identity.guest_sessions WHERE secret_hash = @h", ("h", SHA256.HashData(Encoding.UTF8.GetBytes(secret)))));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_identity.guest_sessions s WHERE s::text LIKE '%' || @s || '%'", ("s", secret)));
    }

    [Fact]
    public async Task The_remembered_guest_survives_a_host_restart_but_not_a_different_signing_secret()
    {
        var secret = IdentityApiFactory.NewMasterSecret();
        string cookie, subject;
        await using (var first = new IdentityApiFactory(db, secret))
        {
            var browser = first.NewBrowser();
            subject = (await browser.PostAsync("/auth/guest/start")).Str("subject");
            cookie = browser.Cookies[Flows.GuestCookie];
        }

        await using (var restarted = new IdentityApiFactory(db, secret))
        {
            var browser = restarted.NewBrowser();
            browser.Cookies[Flows.GuestCookie] = cookie;
            var me = await browser.GetAsync("/auth/guest/me");
            Assert.Equal(subject, me.Str("subject"));
        }

        await using var otherSecret = new IdentityApiFactory(db);
        var foreign = otherSecret.NewBrowser();
        foreign.Cookies[Flows.GuestCookie] = cookie;
        Assert.Equal(HttpStatusCode.Unauthorized, (await foreign.GetAsync("/auth/guest/me")).Status);
        Assert.NotEqual(subject, (await foreign.PostAsync("/auth/guest/start")).Str("subject"));
    }

    [Fact]
    public async Task Edited_or_forged_cookies_never_resume_an_identity()
    {
        await using var factory = new IdentityApiFactory(db);
        var browser = factory.NewBrowser();
        var subject = (await browser.PostAsync("/auth/guest/start")).Str("subject");
        var cookie = browser.Cookies[Flows.GuestCookie];
        var parts = cookie.Split('.');
        var tampered = new[]
        {
            $"{parts[0]}.{parts[1]}.{Flip(parts[2])}",
            $"{parts[0]}.{Flip(parts[1])}.{parts[2]}",
            $"{Guid.NewGuid():N}.{parts[1]}.{parts[2]}",
            $"{parts[0]}.{parts[1]}",
            "garbage",
        };
        foreach (var value in tampered)
        {
            var attacker = factory.NewBrowser();
            attacker.Cookies[Flows.GuestCookie] = value;
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.GetAsync("/auth/guest/me")).Status);
            Assert.NotEqual(subject, (await attacker.PostAsync("/auth/guest/start")).Str("subject"));
        }
    }

    [Fact]
    public async Task A_client_supplied_owner_id_never_changes_the_actor()
    {
        await using var factory = new IdentityApiFactory(db);
        var victim = factory.NewBrowser();
        var victimSubject = (await victim.PostAsync("/auth/guest/start")).Str("subject");
        var attacker = factory.NewBrowser();
        var attackerSubject = (await attacker.PostAsync("/auth/guest/start")).Str("subject");

        var me = await attacker.GetAsync(
            "/auth/guest/me?ownerId=" + Uri.EscapeDataString(victimSubject) + "&subject=" + Uri.EscapeDataString(victimSubject),
            headers: [new("X-Owner-Id", victimSubject), new("X-Subject-Id", victimSubject)]);
        Assert.Equal(attackerSubject, me.Str("subject"));
        Assert.NotEqual(victimSubject, me.Str("subject"));
    }

    [Fact]
    public async Task Sign_out_needs_csrf_and_the_old_cookie_is_dead_afterwards()
    {
        await using var factory = new IdentityApiFactory(db);
        var browser = factory.NewBrowser();
        var subject = (await browser.PostAsync("/auth/guest/start")).Str("subject");
        var cookie = browser.Cookies[Flows.GuestCookie];

        var noCsrf = await browser.PostAsync("/auth/guest/signout");
        Assert.Equal("csrf_rejected", noCsrf.Code);
        var badOrigin = await browser.PostAsync("/auth/guest/signout", csrfCookie: Flows.GuestCsrf, originOverride: "https://evil.example");
        Assert.Equal("origin_rejected", badOrigin.Code);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/auth/guest/me")).Status); // rejected attempts changed nothing

        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/guest/signout", csrfCookie: Flows.GuestCsrf)).Status);
        var replay = factory.NewBrowser();
        replay.Cookies[Flows.GuestCookie] = cookie;
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/auth/guest/me")).Status);
        Assert.NotEqual(subject, (await replay.PostAsync("/auth/guest/start")).Str("subject"));
    }

    [Fact]
    public async Task An_expired_guest_session_is_not_resumed()
    {
        await using var factory = new IdentityApiFactory(db);
        var browser = factory.NewBrowser();
        var subject = (await browser.PostAsync("/auth/guest/start")).Str("subject");

        factory.Time.Advance(TimeSpan.FromDays(91));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/guest/me")).Status);
        var restart = await browser.PostAsync("/auth/guest/start", csrfCookie: Flows.GuestCsrf);
        Assert.Equal(HttpStatusCode.OK, restart.Status);
        Assert.NotEqual(subject, restart.Str("subject"));
        Assert.False(restart.Json.GetProperty("resumed").GetBoolean());
    }

    [Fact]
    public async Task A_supervisor_with_totp_can_ban_a_guest_and_the_ban_is_not_escaped_by_the_old_cookie()
    {
        await using var factory = new IdentityApiFactory(db);
        var guest = factory.NewBrowser();
        var subject = (await guest.PostAsync("/auth/guest/start")).Str("subject");
        var guestId = Guid.Parse(subject["guest:".Length..]);
        var (admin, adminAccess, _, _) = await Flows.SteppedUpSupervisorAsync(factory);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/auth/admin/guests/{guestId}/ban", bearer: adminAccess)).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/auth/guest/me")).Status);
        var again = await guest.PostAsync("/auth/guest/start", csrfCookie: Flows.GuestCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, again.Status);
        Assert.Equal("guest_banned", again.Code);
        Assert.True(await db.ScalarAsync<bool>("SELECT banned_at IS NOT NULL FROM marrow_identity.guest_principals WHERE id = @id", ("id", guestId)));
        Assert.Equal("guest_banned", await db.ScalarAsync<string>("SELECT revoke_reason FROM marrow_identity.guest_sessions WHERE guest_id = @id", ("id", guestId)));
    }

    [Fact]
    public async Task Guest_and_staff_credentials_never_satisfy_each_others_policies()
    {
        await using var factory = new IdentityApiFactory(db);
        var guestBrowser = factory.NewBrowser();
        await guestBrowser.PostAsync("/auth/guest/start");
        var staff = await factory.CreateStaffAsync(Marrow.Infrastructure.Identity.IdentityRoles.Supervisor);
        var (staffBrowser, access, _) = await Flows.LoginAsync(factory, staff);

        // Guest cookie against staff and admin endpoints.
        Assert.Equal(HttpStatusCode.Unauthorized, (await guestBrowser.GetAsync("/auth/staff/me")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guestBrowser.PostAsync($"/auth/admin/guests/{Guid.NewGuid()}/ban", csrfCookie: Flows.GuestCsrf)).Status);

        // Staff bearer against the guest endpoint: the staff principal is not a guest.
        Assert.Equal(HttpStatusCode.Unauthorized, (await staffBrowser.GetAsync("/auth/guest/me", bearer: access, useCookies: false)).Status);

        // A browser holding both: each endpoint family sees only its own principal.
        staffBrowser.Cookies[Flows.GuestCookie] = guestBrowser.Cookies[Flows.GuestCookie];
        var asGuest = await staffBrowser.GetAsync("/auth/guest/me", bearer: access);
        Assert.Equal("Guest", asGuest.Str("kind"));
        var asStaff = await staffBrowser.GetAsync("/auth/staff/me", bearer: access);
        Assert.Equal("Staff", asStaff.Str("kind"));
    }

    private static string Flip(string value) => (value[0] == 'A' ? 'B' : 'A') + value[1..];
}
