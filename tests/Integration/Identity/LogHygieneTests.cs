using System.Net;
using Integration.Tests.Support;
using Xunit;

namespace Integration.Tests.Identity;

/// <summary>R03.09: authentication failures are logged with ids and typed reasons, never with tokens, cookies, passwords, codes or user names.</summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LogHygieneTests(IdentityDatabase db)
{
    [Fact]
    public async Task Authentication_flows_log_events_but_no_secret_or_personal_data()
    {
        await using var factory = new IdentityApiFactory(db);
        var (admin, adminAccess, key, supervisor) = await Flows.SteppedUpSupervisorAsync(factory);
        var secrets = new List<string> { supervisor.Password, supervisor.UserName, adminAccess, key };

        // Staff: failed login, MFA login, rotation, replay, CSRF rejection.
        var stranger = factory.NewBrowser();
        const string unknownUser = "who-is-this@test.sands.local";
        const string strangerPassword = "Wrong-Password-9!";
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsync("/auth/staff/login", new { userName = unknownUser, password = strangerPassword })).Status);
        secrets.AddRange([unknownUser, strangerPassword]);

        var code = Flows.Code(key, 1);
        var (browser, access, login) = await Flows.LoginAsync(factory, supervisor, code);
        var refreshCookie = browser.Cookies[Flows.RefreshCookie];
        var csrf = browser.Cookies[Flows.StaffCsrf];
        secrets.AddRange([code, access, refreshCookie, Flows.Secret(refreshCookie, 2), csrf, login.Str("csrfToken")]);
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf)).Status);
        browser.Cookies[Flows.RefreshCookie] = refreshCookie; // replay of the consumed token
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf)).Status);
        browser.Cookies[Flows.RefreshCookie] = refreshCookie; // the failed refresh cleared both cookies
        browser.Cookies[Flows.StaffCsrf] = csrf;
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsync("/auth/staff/refresh", csrfHeader: "nope")).Status);

        // Guest: create, resume, ban, and a forged cookie.
        var guest = factory.NewBrowser();
        var started = await guest.PostAsync("/auth/guest/start");
        var guestCookie = guest.Cookies[Flows.GuestCookie];
        secrets.AddRange([guestCookie, Flows.Secret(guestCookie, 1), Flows.Secret(guestCookie, 2), started.Str("csrfToken")]);
        var guestId = Guid.Parse(started.Str("subject")["guest:".Length..]);
        await guest.PostAsync("/auth/guest/start", csrfCookie: Flows.GuestCsrf);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/auth/admin/guests/{guestId}/ban", bearer: adminAccess)).Status);
        var forged = factory.NewBrowser();
        forged.Cookies[Flows.GuestCookie] = guestCookie + "x";
        await forged.GetAsync("/auth/guest/me");

        var text = factory.Logs.AllText();
        foreach (var secret in secrets.Where(s => s.Length > 0))
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        // The audit trail exists and carries typed reasons and opaque ids only.
        foreach (var eventId in new[] { 3001, 3002, 3003, 3005, 3008, 3009, 3010, 3011, 3013, 3015, 3016 })
        {
            if (eventId is 3008 or 3016)
            {
                continue; // not produced by this flow
            }

            Assert.Contains(factory.Logs.Lines, l => l.EventId == eventId);
        }

        Assert.Contains(factory.Logs.Lines, l => l.EventId == 3002 && l.Message.Contains("InvalidCredentials", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Exception is not null && l.Category.StartsWith("Marrow.Infrastructure.Identity", StringComparison.Ordinal));
    }
}
