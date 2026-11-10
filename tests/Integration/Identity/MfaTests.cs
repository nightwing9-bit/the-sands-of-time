using System.Net;
using System.Text;
using Integration.Tests.Support;
using Marrow.Infrastructure.Identity;
using Xunit;

namespace Integration.Tests.Identity;

/// <summary>R03.01: TOTP is required for privileged staff actions; step-up is per session, single-use and time-limited.</summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MfaTests(IdentityDatabase db)
{
    private static string BanPath(Guid id) => $"/auth/admin/guests/{id}/ban";

    [Fact]
    public async Task Privileged_action_without_a_totp_step_up_is_denied_with_mfa_required()
    {
        await using var factory = new IdentityApiFactory(db);
        var supervisor = await factory.CreateStaffAsync(IdentityRoles.Supervisor);
        var (browser, access, _) = await Flows.LoginAsync(factory, supervisor);

        var denied = await browser.PostAsync(BanPath(Guid.NewGuid()), bearer: access);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("mfa_required", denied.Code);

        // Plain staff endpoints stay available without MFA.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/auth/staff/me", bearer: access)).Status);
        var verify = await browser.PostAsync("/auth/staff/mfa/verify", new { code = "123456" }, bearer: access);
        Assert.Equal("mfa_not_enrolled", verify.Code);
    }

    [Fact]
    public async Task Enrollment_needs_the_password_and_a_valid_code_and_the_key_is_encrypted_at_rest()
    {
        await using var factory = new IdentityApiFactory(db);
        var supervisor = await factory.CreateStaffAsync(IdentityRoles.Supervisor);
        var (browser, access, _) = await Flows.LoginAsync(factory, supervisor);

        var badPassword = await browser.PostAsync("/auth/staff/mfa/enroll", new { currentPassword = "Wrong-password-1!" }, bearer: access);
        Assert.Equal(HttpStatusCode.Unauthorized, badPassword.Status);
        Assert.Equal("invalid_credentials", badPassword.Code);

        var begin = await browser.PostAsync("/auth/staff/mfa/enroll", new { currentPassword = supervisor.Password }, bearer: access);
        Assert.Equal(HttpStatusCode.OK, begin.Status);
        var key = begin.Str("sharedKey");
        Assert.StartsWith("otpauth://totp/", begin.Str("otpAuthUri"), StringComparison.Ordinal);
        Assert.Contains("secret=" + key, begin.Str("otpAuthUri"), StringComparison.Ordinal);

        // Not enabled until a valid code proves the authenticator has the key.
        var wrong = await browser.PostAsync("/auth/staff/mfa/confirm", new { code = Flows.Code(key, -300) }, bearer: access);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.Status);
        Assert.Equal("invalid_mfa_code", wrong.Code);
        Assert.False(await db.ScalarAsync<bool>("SELECT two_factor_enabled FROM marrow_identity.staff_users WHERE id = @id", ("id", supervisor.Id)));

        var confirmed = await browser.PostAsync("/auth/staff/mfa/confirm", new { code = Flows.Code(key, 0) }, bearer: access);
        Assert.Equal(HttpStatusCode.OK, confirmed.Status);
        Assert.True(await db.ScalarAsync<bool>("SELECT two_factor_enabled FROM marrow_identity.staff_users WHERE id = @id", ("id", supervisor.Id)));

        var again = await browser.PostAsync("/auth/staff/mfa/enroll", new { currentPassword = supervisor.Password }, bearer: access);
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal("mfa_already_enrolled", again.Code);

        var blob = await db.ScalarAsync<byte[]>("SELECT authenticator_key_enc FROM marrow_identity.staff_users WHERE id = @id", ("id", supervisor.Id));
        Assert.NotNull(blob);
        Assert.DoesNotContain(key, Encoding.Latin1.GetString(blob!), StringComparison.Ordinal);
        Assert.True(blob!.Length > key.Length);
    }

    [Fact]
    public async Task A_stepped_up_supervisor_reaches_the_privileged_handler_and_a_code_cannot_be_reused()
    {
        await using var factory = new IdentityApiFactory(db);
        var (browser, access, key, _) = await Flows.SteppedUpSupervisorAsync(factory);

        var reached = await browser.PostAsync(BanPath(Guid.NewGuid()), bearer: access);
        Assert.Equal(HttpStatusCode.NotFound, reached.Status); // authorized; the unknown guest is the only problem
        Assert.Equal("not_found", reached.Code);

        var replay = await browser.PostAsync("/auth/staff/mfa/verify", new { code = Flows.Code(key, 0) }, bearer: access);
        Assert.Equal(HttpStatusCode.Forbidden, replay.Status);
        Assert.Equal("invalid_mfa_code", replay.Code);

        var fresh = await browser.PostAsync("/auth/staff/mfa/verify", new { code = Flows.Code(key, 1) }, bearer: access);
        Assert.Equal(HttpStatusCode.OK, fresh.Status);
    }

    [Fact]
    public async Task Step_up_belongs_to_one_session_and_expires()
    {
        await using var factory = new IdentityApiFactory(db);
        var (browser, access, key, supervisor) = await Flows.SteppedUpSupervisorAsync(factory);

        // A new login does not inherit the other session's step-up.
        var (second, secondAccess, _) = await Flows.LoginAsync(factory, supervisor);
        Assert.Equal("mfa_required", (await second.PostAsync(BanPath(Guid.NewGuid()), bearer: secondAccess)).Code);

        // Login can carry the code and step up in one call (distinct code from the enrollment one).
        var (third, thirdAccess, loginResponse) = await Flows.LoginAsync(factory, supervisor, Flows.Code(key, 1));
        Assert.True(loginResponse.Json.GetProperty("mfaEnrolled").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await third.PostAsync(BanPath(Guid.NewGuid()), bearer: thirdAccess)).Status);

        // Step-up lapses after the freshness window; refreshing the access token does not renew it.
        factory.Time.Advance(TimeSpan.FromMinutes(16));
        var refreshed = await browser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        var access2 = refreshed.Str("accessToken");
        Assert.Equal("mfa_required", (await browser.PostAsync(BanPath(Guid.NewGuid()), bearer: access2)).Code);
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/auth/staff/mfa/verify", new { code = Flows.Code(key, 2) }, bearer: access2)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync(BanPath(Guid.NewGuid()), bearer: access2)).Status);
        _ = access;
    }

    [Fact]
    public async Task Wrong_totp_codes_count_toward_lockout()
    {
        await using var factory = new IdentityApiFactory(db);
        var (_, _, key, supervisor) = await Flows.SteppedUpSupervisorAsync(factory);
        var browser = factory.NewBrowser();
        for (var i = 0; i < 5; i++)
        {
            var bad = await browser.PostAsync("/auth/staff/login", new { userName = supervisor.UserName, password = supervisor.Password, totpCode = Flows.Code(key, -300 - i) });
            Assert.Equal("invalid_mfa_code", bad.Code);
            Assert.Empty(browser.Cookies); // a failed second factor creates no session
        }

        var locked = await browser.PostAsync("/auth/staff/login", new { userName = supervisor.UserName, password = supervisor.Password, totpCode = Flows.Code(key, 1) });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.Status);
    }

    [Fact]
    public async Task A_valid_mfa_does_not_turn_an_operator_into_a_supervisor_and_revocation_applies_immediately()
    {
        await using var factory = new IdentityApiFactory(db);
        var op = await factory.CreateStaffAsync(IdentityRoles.Operator);
        var (opBrowser, opAccess, _) = await Flows.LoginAsync(factory, op);
        var key = await Flows.EnrollAsync(opBrowser, opAccess, op);

        var forbidden = await opBrowser.PostAsync(BanPath(Guid.NewGuid()), bearer: opAccess);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
        Assert.Equal("forbidden", forbidden.Code);
        _ = key;

        var (supBrowser, supAccess, _, _) = await Flows.SteppedUpSupervisorAsync(factory);
        var revoke = await supBrowser.PostAsync($"/auth/admin/staff-sessions/{Flows.SessionId(opAccess)}/revoke", bearer: supAccess);
        Assert.Equal(HttpStatusCode.NoContent, revoke.Status);

        // The operator's still-unexpired access token and refresh token are dead on the next request.
        Assert.Equal(HttpStatusCode.Unauthorized, (await opBrowser.GetAsync("/auth/staff/me", bearer: opAccess)).Status);
        var refresh = await opBrowser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf);
        Assert.Equal("invalid_refresh_token", refresh.Code);
        Assert.Equal("admin_revoked", await db.ScalarAsync<string>("SELECT revoke_reason FROM marrow_identity.staff_sessions WHERE id = @s", ("s", Flows.SessionId(opAccess))));
    }
}
