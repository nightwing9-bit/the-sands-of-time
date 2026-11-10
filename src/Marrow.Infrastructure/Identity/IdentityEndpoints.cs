using System.Globalization;
using System.Security.Claims;
using Marrow.Infrastructure.Contracts;
using Marrow.Infrastructure.Identity.Csrf;
using Marrow.Infrastructure.Identity.Guest;
using Marrow.Infrastructure.Identity.Staff;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Marrow.Infrastructure.Identity;

public sealed record StaffLoginRequest(string? UserName, string? Password, string? TotpCode);

public sealed record EnrollMfaRequest(string? CurrentPassword);

public sealed record MfaCodeRequest(string? Code);

/// <summary>Access-token response. The refresh token is only ever a cookie.</summary>
public sealed record StaffSessionResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset AccessExpiresAtUtc,
    string Subject,
    IReadOnlyList<string> Roles,
    bool MfaEnrolled,
    DateTimeOffset? MfaVerifiedUntilUtc,
    string CsrfToken);

public sealed record GuestSessionResponse(string Subject, DateTimeOffset ExpiresAtUtc, bool Resumed, string CsrfToken);

public sealed record ActorResponse(string Subject, string Kind, IReadOnlyCollection<string> Roles, IReadOnlyCollection<string> Claims);
public sealed record MfaVerifiedResponse(DateTimeOffset MfaVerifiedUntilUtc);

public static class IdentityEndpoints
{
    private const int MaxFieldLength = 256;

    /// <summary>Documents the typed failure body every identity endpoint can return.</summary>
    private static RouteHandlerBuilder WithProblems(this RouteHandlerBuilder builder) => builder
        .Produces<ApiProblem>(StatusCodes.Status400BadRequest)
        .Produces<ApiProblem>(StatusCodes.Status401Unauthorized)
        .Produces<ApiProblem>(StatusCodes.Status403Forbidden)
        .Produces<ApiProblem>(StatusCodes.Status404NotFound)
        .Produces<ApiProblem>(StatusCodes.Status409Conflict);

    /// <summary>Maps the shared staff/guest authentication endpoints. Host composition only; no domain rules.</summary>
    public static IEndpointRouteBuilder MapMarrowIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        // Produces<T> metadata only feeds the OpenAPI document that generates the TypeScript client; it changes no behavior.
        var staff = app.MapGroup("/auth/staff");
        staff.MapPost("/login", StaffLogin).WithName("StaffLogin").Produces<StaffSessionResponse>().WithProblems();
        staff.MapPost("/refresh", StaffRefresh).WithName("StaffRefresh").Produces<StaffSessionResponse>().WithProblems();
        staff.MapPost("/logout", StaffLogout).WithName("StaffLogout").Produces(StatusCodes.Status204NoContent).WithProblems();
        staff.MapGet("/me", Me).RequireAuthorization(IdentityPolicies.Staff).WithName("StaffMe").Produces<ActorResponse>().WithProblems();
        staff.MapPost("/mfa/enroll", BeginEnrollment).RequireAuthorization(IdentityPolicies.Staff).WithName("StaffMfaEnroll").Produces<MfaEnrollment>().WithProblems();
        staff.MapPost("/mfa/confirm", ConfirmEnrollment).RequireAuthorization(IdentityPolicies.Staff).WithName("StaffMfaConfirm").Produces<MfaVerifiedResponse>().WithProblems();
        staff.MapPost("/mfa/verify", VerifyMfa).RequireAuthorization(IdentityPolicies.Staff).WithName("StaffMfaVerify").Produces<MfaVerifiedResponse>().WithProblems();
        var guest = app.MapGroup("/auth/guest");
        guest.MapPost("/start", GuestStart).WithName("GuestStart").Produces<GuestSessionResponse>().WithProblems();
        guest.MapPost("/signout", GuestSignOut).WithName("GuestSignOut").Produces(StatusCodes.Status204NoContent).WithProblems();
        guest.MapGet("/me", Me).RequireAuthorization(IdentityPolicies.Guest).WithName("GuestMe").Produces<ActorResponse>().WithProblems();
        // The privileged staff actions owned by identity itself: revoking sessions and banning guests.
        var admin = app.MapGroup("/auth/admin").RequireAuthorization(IdentityPolicies.StaffSupervisor);
        admin.MapPost("/staff-sessions/{sessionId:guid}/revoke", RevokeStaffSession).WithName("RevokeStaffSession").Produces(StatusCodes.Status204NoContent).WithProblems();
        admin.MapPost("/guests/{guestId:guid}/ban", BanGuest).WithName("BanGuest").Produces(StatusCodes.Status204NoContent).WithProblems();
        return app;
    }

    private static async Task<IResult> StaffLogin(
        StaffLoginRequest? body, HttpContext http, StaffAuthService staff, CsrfTokens csrf, CancellationToken ct)
    {
        if (!Valid(body?.UserName, MaxFieldLength) || !Valid(body?.Password, MaxFieldLength) || (body!.TotpCode is { } c && c.Length > 16))
        {
            return AuthProblem.ToResult(AuthFailure.InvalidRequest);
        }

        var result = await staff.LoginAsync(body.UserName!, body.Password!, body.TotpCode, ct);
        return StaffTokenResponse(http, csrf, result);
    }

    private static async Task<IResult> StaffRefresh(HttpContext http, StaffAuthService staff, CsrfTokens csrf, CancellationToken ct)
    {
        var result = await staff.RefreshAsync(http.Request.Cookies[IdentityCookies.StaffRefresh], ct);
        if (result.IsFailure)
        {
            ClearStaffCookies(http);
        }

        return StaffTokenResponse(http, csrf, result);
    }

    private static async Task<IResult> StaffLogout(HttpContext http, StaffAuthService staff, CancellationToken ct)
    {
        await staff.LogoutAsync(http.Request.Cookies[IdentityCookies.StaffRefresh], ct);
        ClearStaffCookies(http);
        return Results.NoContent();
    }

    private static IResult Me(IActorContext actor) =>
        actor.IsAuthenticated
            ? Results.Ok(new ActorResponse(actor.SubjectId, actor.PrincipalKind.ToString(), actor.Roles, actor.Claims))
            : AuthProblem.ToResult(AuthFailure.SessionInactive);

    private static async Task<IResult> BeginEnrollment(
        EnrollMfaRequest? body, HttpContext http, StaffAuthService staff, CancellationToken ct)
    {
        if (!Valid(body?.CurrentPassword, MaxFieldLength) || !TryCaller(http.User, out var caller))
        {
            return AuthProblem.ToResult(AuthFailure.InvalidRequest);
        }

        NoStore(http);
        var result = await staff.BeginMfaEnrollmentAsync(caller.UserId, body!.CurrentPassword!, ct);
        return result.Match(v => Results.Ok(v), AuthProblem.ToResult);
    }

    private static Task<IResult> ConfirmEnrollment(MfaCodeRequest? body, HttpContext http, StaffAuthService staff, CancellationToken ct) =>
        MfaStep(body, http, (c, code) => staff.ConfirmMfaEnrollmentAsync(c.UserId, c.SessionId, code, ct));

    private static Task<IResult> VerifyMfa(MfaCodeRequest? body, HttpContext http, StaffAuthService staff, CancellationToken ct) =>
        MfaStep(body, http, (c, code) => staff.VerifyMfaAsync(c.UserId, c.SessionId, code, ct));

    private static async Task<IResult> MfaStep(
        MfaCodeRequest? body, HttpContext http, Func<Caller, string, Task<Marrow.Kernel.Results.Result<DateTimeOffset, AuthFailure>>> step)
    {
        if (!Valid(body?.Code, 16) || !TryCaller(http.User, out var caller))
        {
            return AuthProblem.ToResult(AuthFailure.InvalidRequest);
        }

        var result = await step(caller, body!.Code!);
        return result.Match(until => Results.Ok(new MfaVerifiedResponse(until)), AuthProblem.ToResult);
    }

    private static async Task<IResult> RevokeStaffSession(Guid sessionId, StaffAuthService staff, CancellationToken ct) =>
        await staff.RevokeSessionAsync(sessionId, "admin_revoked", ct) ? Results.NoContent() : AuthProblem.ToResult(AuthFailure.NotFound);

    private static async Task<IResult> BanGuest(Guid guestId, GuestSessionService guests, CancellationToken ct) =>
        await guests.BanAsync(guestId, "admin_action", ct) ? Results.NoContent() : AuthProblem.ToResult(AuthFailure.NotFound);

    private static async Task<IResult> GuestStart(HttpContext http, GuestSessionService guests, CsrfTokens csrf, CancellationToken ct)
    {
        var result = await guests.StartOrResumeAsync(http.Request.Cookies[IdentityCookies.GuestSession], ct);
        if (result.TryGetError(out var failure))
        {
            return AuthProblem.ToResult(failure);
        }

        var issued = result.Value;
        NoStore(http);
        http.Response.Cookies.Append(IdentityCookies.GuestSession, issued.CookieValue, CookieFor(issued.ExpiresAt, "/", SameSiteMode.Lax, httpOnly: true));
        var token = csrf.Create(issued.Identity.SessionId);
        http.Response.Cookies.Append(IdentityCookies.GuestCsrf, token, CookieFor(issued.ExpiresAt, "/", SameSiteMode.Lax, httpOnly: false));
        return Results.Ok(new GuestSessionResponse(issued.Identity.SubjectId, issued.ExpiresAt, issued.Resumed, token));
    }

    private static async Task<IResult> GuestSignOut(HttpContext http, GuestSessionService guests, CancellationToken ct)
    {
        await guests.SignOutAsync(http.Request.Cookies[IdentityCookies.GuestSession], ct);
        http.Response.Cookies.Delete(IdentityCookies.GuestSession, CookieFor(null, "/", SameSiteMode.Lax, httpOnly: true));
        http.Response.Cookies.Delete(IdentityCookies.GuestCsrf, CookieFor(null, "/", SameSiteMode.Lax, httpOnly: false));
        return Results.NoContent();
    }

    private static IResult StaffTokenResponse(
        HttpContext http, CsrfTokens csrf, Marrow.Kernel.Results.Result<StaffSessionTokens, AuthFailure> result)
    {
        if (result.TryGetError(out var failure))
        {
            return AuthProblem.ToResult(failure);
        }

        var tokens = result.Value;
        NoStore(http);
        http.Response.Cookies.Append(
            IdentityCookies.StaffRefresh, tokens.RefreshToken,
            CookieFor(tokens.RefreshExpiresAt, IdentityCookies.StaffRefreshPath, SameSiteMode.Strict, httpOnly: true));
        var token = csrf.Create(tokens.SessionId);
        http.Response.Cookies.Append(
            IdentityCookies.StaffCsrf, token, CookieFor(tokens.RefreshExpiresAt, "/", SameSiteMode.Strict, httpOnly: false));
        return Results.Ok(new StaffSessionResponse(
            tokens.AccessToken, "Bearer", tokens.AccessExpiresAt, "staff:" + tokens.UserId.ToString("D", CultureInfo.InvariantCulture),
            tokens.Roles, tokens.MfaEnrolled, tokens.MfaVerifiedUntil, token));
    }

    private static void ClearStaffCookies(HttpContext http)
    {
        http.Response.Cookies.Delete(IdentityCookies.StaffRefresh, CookieFor(null, IdentityCookies.StaffRefreshPath, SameSiteMode.Strict, httpOnly: true));
        http.Response.Cookies.Delete(IdentityCookies.StaffCsrf, CookieFor(null, "/", SameSiteMode.Strict, httpOnly: false));
    }

    private static CookieOptions CookieFor(DateTimeOffset? expires, string path, SameSiteMode sameSite, bool httpOnly) => new()
    {
        HttpOnly = httpOnly,
        Secure = true,
        SameSite = sameSite,
        Path = path,
        Expires = expires,
        IsEssential = true,
    };

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";

    private static bool Valid(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;

    private static bool TryCaller(ClaimsPrincipal user, out Caller caller)
    {
        caller = default;
        var subject = user.FindFirst(IdentityClaimTypes.Subject)?.Value;
        if (subject is null || !subject.StartsWith("staff:", StringComparison.Ordinal)
            || !Guid.TryParse(subject.AsSpan(6), out var userId)
            || !Guid.TryParse(user.FindFirst(IdentityClaimTypes.SessionId)?.Value, out var sessionId))
        {
            return false;
        }

        caller = new Caller(userId, sessionId);
        return true;
    }

    private readonly record struct Caller(Guid UserId, Guid SessionId);
}
