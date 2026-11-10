using System.Net;
using System.Security.Cryptography;
using System.Text;
using Integration.Tests.Support;
using Xunit;

namespace Integration.Tests.Identity;

/// <summary>R03.01: staff login, rotating refresh, replay/expiry/revocation, CSRF. Real API host + real PostgreSQL.</summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StaffAuthTests(IdentityDatabase db)
{
    [Fact]
    public async Task Login_issues_a_short_access_token_and_a_hardened_refresh_cookie_that_is_never_in_the_body()
    {
        await using var factory = new IdentityApiFactory(db);
        var staff = await factory.CreateStaffAsync();
        var (browser, access, login) = await Flows.LoginAsync(factory, staff);

        var refresh = browser.LastSeen[Flows.RefreshCookie];
        Assert.True(refresh.HttpOnly);
        Assert.True(refresh.Secure);
        Assert.Equal("Strict", refresh.SameSite, ignoreCase: true);
        Assert.Equal("/auth/staff", refresh.Path);
        var csrf = browser.LastSeen[Flows.StaffCsrf];
        Assert.False(csrf.HttpOnly);
        Assert.True(csrf.Secure);
        Assert.DoesNotContain(refresh.Value, login.Body);
        Assert.DoesNotContain("refreshToken", login.Body, StringComparison.OrdinalIgnoreCase);

        var me = await browser.GetAsync("/auth/staff/me", bearer: access);
        Assert.Equal(HttpStatusCode.OK, me.Status);
        Assert.Equal("staff:" + staff.Id, me.Str("subject"));
        Assert.Equal("Staff", me.Str("kind"));
        Assert.Equal(["operator"], me.Roles);
        Assert.Equal(0, me.Json.GetProperty("claims").GetArrayLength());

        var anonymous = await browser.GetAsync("/auth/staff/me", useCookies: false);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);

        // Only hashes are stored: neither the refresh secret nor the password appears in any row.
        var secret = Flows.Secret(refresh.Value, 2);
        var sha = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_identity.staff_refresh_tokens WHERE token_hash = @h", ("h", sha)));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_identity.staff_refresh_tokens t WHERE t::text LIKE '%' || @s || '%'", ("s", secret)));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_identity.staff_users u WHERE u::text LIKE '%' || @p || '%'", ("p", staff.Password)));
    }

    [Fact]
    public async Task Wrong_password_unknown_user_and_lockout_all_look_the_same()
    {
        await using var factory = new IdentityApiFactory(db);
        var staff = await factory.CreateStaffAsync();
        var browser = factory.NewBrowser();

        var unknown = await browser.PostAsync("/auth/staff/login", new { userName = "nobody-" + Guid.NewGuid().ToString("N") + "@test.sands.local", password = "Wrong-password-1!" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Status);
        Assert.Equal("invalid_credentials", unknown.Code);

        for (var i = 0; i < 5; i++)
        {
            var wrong = await browser.PostAsync("/auth/staff/login", new { userName = staff.UserName, password = "Wrong-password-1!" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
            Assert.Equal("invalid_credentials", wrong.Code);
        }

        // The right password is refused while locked out, with the same typed failure.
        var locked = await browser.PostAsync("/auth/staff/login", new { userName = staff.UserName, password = staff.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.Status);
        Assert.Equal("invalid_credentials", locked.Code);

        var invalid = await browser.PostAsync("/auth/staff/login", new { userName = "", password = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
        Assert.Equal("invalid_request", invalid.Code);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_a_replayed_token_revokes_the_whole_session()
    {
        await using var factory = new IdentityApiFactory(db);
        var staff = await factory.CreateStaffAsync();
        var (browser, access1, _) = await Flows.LoginAsync(factory, staff);
        var oldRefresh = browser.Cookies[Flows.RefreshCookie];
        var sessionId = Flows.SessionId(access1);

        var rotated = await browser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf);
        Assert.Equal(HttpStatusCode.OK, rotated.Status);
        var newRefresh = browser.Cookies[Flows.RefreshCookie];
        var access2 = rotated.Str("accessToken");
        Assert.NotEqual(oldRefresh, newRefresh);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/auth/staff/me", bearer: access2)).Status);
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_identity.staff_refresh_tokens WHERE session_id = @s", ("s", sessionId)));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_identity.staff_refresh_tokens WHERE session_id = @s AND consumed_at IS NOT NULL", ("s", sessionId)));

        // An attacker (or a stale tab) presents the already-used token.
        var csrf = browser.Cookies[Flows.StaffCsrf];
        browser.Cookies[Flows.RefreshCookie] = oldRefresh;
        var replay = await browser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.Status);
        Assert.Equal("invalid_refresh_token", replay.Code);

        // The legitimately rotated token and the access token of that session die with it.
        browser.Cookies[Flows.RefreshCookie] = newRefresh; // a failed refresh clears the cookies, so put the pair back
        browser.Cookies[Flows.StaffCsrf] = csrf;
        var afterReplay = await browser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/staff/me", bearer: access2)).Status);
        Assert.Equal("refresh_replay", await db.ScalarAsync<string>("SELECT revoke_reason FROM marrow_identity.staff_sessions WHERE id = @s", ("s", sessionId)));
        Assert.Contains(factory.Logs.Lines, l => l.EventId == 3005);
    }

    [Fact]
    public async Task Expired_refresh_token_and_expired_access_token_are_rejected()
    {
        await using var factory = new IdentityApiFactory(db);
        var staff = await factory.CreateStaffAsync();
        var (browser, access, _) = await Flows.LoginAsync(factory, staff);

        factory.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/staff/me", bearer: access)).Status);

        factory.Time.Advance(TimeSpan.FromDays(8));
        var expired = await browser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.Status);
        Assert.Equal("invalid_refresh_token", expired.Code);
        Assert.Contains(expired.SetCookies, c => c.Name == Flows.RefreshCookie && c.Deleted);
        Assert.Contains(factory.Logs.Lines, l => l.EventId == 3004 && l.Message.Contains("expired", StringComparison.Ordinal));

        // A fresh login on the same host still works: expiry is per token, not a broken service.
        var (_, access2, _) = await Flows.LoginAsync(factory, staff);
        Assert.NotEmpty(access2);
    }

    [Fact]
    public async Task Concurrent_refreshes_with_one_token_succeed_exactly_once()
    {
        await using var factory = new IdentityApiFactory(db);
        var staff = await factory.CreateStaffAsync();
        var (browser, access, _) = await Flows.LoginAsync(factory, staff);
        var attempts = Enumerable.Range(0, 8).Select(_ =>
        {
            var copy = factory.NewBrowser();
            foreach (var (name, value) in browser.Cookies)
            {
                copy.Cookies[name] = value;
            }

            return copy.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf);
        }).ToArray();

        var results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(r => r.Status == HttpStatusCode.Unauthorized));
        // The seven losers each presented a consumed token, which revokes the session.
        Assert.Equal("refresh_replay", await db.ScalarAsync<string>("SELECT revoke_reason FROM marrow_identity.staff_sessions WHERE id = @s", ("s", Flows.SessionId(access))));
    }

    [Fact]
    public async Task Cookie_authenticated_refresh_needs_an_allowed_origin_and_a_session_bound_csrf_token()
    {
        await using var factory = new IdentityApiFactory(db, extraSettings: new() { ["Identity:AllowedOrigins:0"] = "https://app.sands.example" });
        var staff = await factory.CreateStaffAsync();
        var (browser, _, _) = await Flows.LoginAsync(factory, staff);
        var (otherBrowser, _, _) = await Flows.LoginAsync(factory, staff);
        var foreignSessionToken = otherBrowser.Cookies[Flows.StaffCsrf];

        var noHeader = await browser.PostAsync("/auth/staff/refresh");
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.Status);
        Assert.Equal("csrf_rejected", noHeader.Code);
        Assert.Equal("csrf_rejected", (await browser.PostAsync("/auth/staff/refresh", csrfHeader: "not-a-token")).Code);
        Assert.Equal("csrf_rejected", (await browser.PostAsync("/auth/staff/refresh", csrfHeader: foreignSessionToken)).Code);

        // A cookie/header pair chosen by someone else (double-submit with a forged value) fails the session binding.
        var planted = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var attacker = factory.NewBrowser();
        attacker.Cookies[Flows.RefreshCookie] = browser.Cookies[Flows.RefreshCookie];
        attacker.Cookies[Flows.StaffCsrf] = planted;
        Assert.Equal("csrf_rejected", (await attacker.PostAsync("/auth/staff/refresh", csrfHeader: planted)).Code);

        Assert.Equal("origin_rejected", (await browser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf, sendOrigin: false)).Code);
        Assert.Equal("origin_rejected", (await browser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf, originOverride: "https://evil.example")).Code);

        // None of the rejected attempts consumed the refresh token.
        var good = await browser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf);
        Assert.Equal(HttpStatusCode.OK, good.Status);

        var configured = await browser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf, originOverride: "https://app.sands.example");
        Assert.Equal(HttpStatusCode.OK, configured.Status);

        // Login CSRF: a foreign origin may not even start a session; a non-browser client without Origin may.
        var fresh = factory.NewBrowser();
        var foreignLogin = await fresh.PostAsync("/auth/staff/login", new { userName = staff.UserName, password = staff.Password }, originOverride: "https://evil.example");
        Assert.Equal("origin_rejected", foreignLogin.Code);
        var scripted = await fresh.PostAsync("/auth/staff/login", new { userName = staff.UserName, password = staff.Password }, sendOrigin: false);
        Assert.Equal(HttpStatusCode.OK, scripted.Status);
    }

    [Fact]
    public async Task Logout_revokes_the_session_so_old_access_and_refresh_tokens_stop_working()
    {
        await using var factory = new IdentityApiFactory(db);
        var staff = await factory.CreateStaffAsync();
        var (browser, access, _) = await Flows.LoginAsync(factory, staff);
        var refresh = browser.Cookies[Flows.RefreshCookie];
        var csrfBefore = browser.Cookies[Flows.StaffCsrf];

        var logout = await browser.PostAsync("/auth/staff/logout", csrfCookie: Flows.StaffCsrf);
        Assert.Equal(HttpStatusCode.NoContent, logout.Status);
        Assert.Contains(logout.SetCookies, c => c.Name == Flows.RefreshCookie && c.Deleted);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/staff/me", bearer: access)).Status);
        var stolen = factory.NewBrowser();
        stolen.Cookies[Flows.RefreshCookie] = refresh;
        stolen.Cookies[Flows.StaffCsrf] = csrfBefore;
        var reuse = await stolen.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.Status);
        Assert.Equal("invalid_refresh_token", reuse.Code);
        Assert.Equal("logout", await db.ScalarAsync<string>("SELECT revoke_reason FROM marrow_identity.staff_sessions WHERE id = @s", ("s", Flows.SessionId(access))));
    }
}
