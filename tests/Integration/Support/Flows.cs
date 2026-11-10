using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Integration.Tests.Support;

internal static class Flows
{
    public const string RefreshCookie = "__Secure-sands_refresh";
    public const string StaffCsrf = "__Host-sands_csrf_staff";
    public const string GuestCookie = "__Host-sands_guest";
    public const string GuestCsrf = "__Host-sands_csrf_guest";

    public static async Task<(TestBrowser Browser, string Access, ApiResponse Response)> LoginAsync(
        IdentityApiFactory factory, TestStaff staff, string? totpCode = null)
    {
        var browser = factory.NewBrowser();
        var response = await browser.PostAsync("/auth/staff/login", new { userName = staff.UserName, password = staff.Password, totpCode });
        Assert.Equal(HttpStatusCode.OK, response.Status);
        return (browser, response.Str("accessToken"), response);
    }

    /// <summary>Enrolls TOTP through the real endpoints and confirms with the step-0 code. Returns the shared key.</summary>
    public static async Task<string> EnrollAsync(TestBrowser browser, string access, TestStaff staff)
    {
        var begin = await browser.PostAsync("/auth/staff/mfa/enroll", new { currentPassword = staff.Password }, bearer: access);
        Assert.Equal(HttpStatusCode.OK, begin.Status);
        var key = begin.Str("sharedKey");
        var confirm = await browser.PostAsync("/auth/staff/mfa/confirm", new { code = Code(key, 0) }, bearer: access);
        Assert.Equal(HttpStatusCode.OK, confirm.Status);
        return key;
    }

    /// <summary>A supervisor session that has completed TOTP enrollment (and so is stepped up).</summary>
    public static async Task<(TestBrowser Browser, string Access, string Key, TestStaff Staff)> SteppedUpSupervisorAsync(IdentityApiFactory factory)
    {
        var staff = await factory.CreateStaffAsync(Marrow.Infrastructure.Identity.IdentityRoles.Supervisor);
        var (browser, access, _) = await LoginAsync(factory, staff);
        var key = await EnrollAsync(browser, access, staff);
        return (browser, access, key, staff);
    }

    /// <summary>
    /// A TOTP code for the 30-second step <paramref name="stepOffset"/> away from now. Identity accepts +-2 steps of drift,
    /// so tests use distinct offsets in -1..2 to obtain distinct valid codes without waiting.
    /// </summary>
    public static string Code(string key, int stepOffset) => Totp.Code(key, DateTimeOffset.UtcNow.AddSeconds(30 * stepOffset));

    public static Guid SessionId(string jwt) => Guid.Parse(Claim(jwt, "sid"));

    public static string Claim(string jwt, string name)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement.GetProperty(name).GetString()!;
    }

    public static string Secret(string cookieValue, int segment) => cookieValue.Split('.')[segment];
}
