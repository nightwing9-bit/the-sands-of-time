using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Marrow.Infrastructure.Identity.Csrf;

/// <summary>
/// Signed double-submit CSRF token bound to a session: <c>{random}.{HMAC(key, "csrf|sessionId|random")}</c>. It is
/// readable by the SPA (non-HttpOnly cookie) and must be echoed in the <c>X-CSRF-Token</c> header. Because it is bound
/// to the session by a server-held key, a cookie planted by another origin or subdomain does not validate.
/// </summary>
public sealed class CsrfTokens(IdentityKeyMaterial keys)
{
    public string Create(Guid sessionId)
    {
        var random = SecretTokens.NewSecret();
        return random + "." + Sign(sessionId, random);
    }

    public bool IsValid(Guid sessionId, string? cookieToken, string? headerToken)
    {
        if (string.IsNullOrEmpty(cookieToken) || string.IsNullOrEmpty(headerToken) || cookieToken.Length > 200 || headerToken.Length > 200)
        {
            return false;
        }

        if (!SecretTokens.FixedTimeEquals(cookieToken, headerToken))
        {
            return false;
        }

        var dot = cookieToken.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && SecretTokens.FixedTimeEquals(Sign(sessionId, cookieToken[..dot]), cookieToken[(dot + 1)..]);
    }

    private string Sign(Guid sessionId, string random) =>
        SecretTokens.Base64Url(keys.CsrfMac($"csrf|{sessionId:N}|{random}"));
}

/// <summary>
/// Origin and CSRF checks for state-changing requests that the browser authenticates with a cookie.
/// <list type="bullet">
/// <item>Unsafe method plus a guest cookie, or a staff refresh cookie without a bearer token: <c>Origin</c> must be present and
/// allowed, and the signed CSRF token must be echoed in <c>X-CSRF-Token</c>.</item>
/// <item>Any other unsafe request that carries an <c>Origin</c> header, and the login and guest-start endpoints: the origin must be allowed.</item>
/// </list>
/// Allowed means the request's own origin or an entry of <see cref="MarrowIdentityOptions.AllowedOrigins"/>.
/// Requests authenticated only by <c>Authorization: Bearer</c> are not cookie-authenticated and are not CSRF-able.
/// </summary>
public sealed class CsrfOriginMiddleware(RequestDelegate next, CsrfTokens tokens, IOptions<MarrowIdentityOptions> options, IdentityAuditLog audit)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        {
            await next(context);
            return;
        }

        var guestCookie = request.Cookies[IdentityCookies.GuestSession];
        var refreshCookie = request.Cookies[IdentityCookies.StaffRefresh];
        var hasBearer = request.Headers.Authorization.Count > 0;
        string? sessionSegment = null;
        string? csrfCookieName = null;
        if (!string.IsNullOrEmpty(guestCookie))
        {
            sessionSegment = guestCookie;
            csrfCookieName = IdentityCookies.GuestCsrf;
        }
        else if (!string.IsNullOrEmpty(refreshCookie) && !hasBearer)
        {
            sessionSegment = refreshCookie;
            csrfCookieName = IdentityCookies.StaffCsrf;
        }

        var origin = request.Headers.Origin.ToString();
        var hasOrigin = request.Headers.Origin.Count > 0;
        if (sessionSegment is null || IsTokenExempt(request.Path))
        {
            if (hasOrigin && !IsAllowedOrigin(request, origin))
            {
                await Reject(context, AuthFailure.OriginRejected);
                return;
            }

            await next(context);
            return;
        }

        if (!hasOrigin || !IsAllowedOrigin(request, origin))
        {
            await Reject(context, AuthFailure.OriginRejected);
            return;
        }

        var first = sessionSegment.Split('.')[0];
        var csrfCookie = request.Cookies[csrfCookieName!];
        var header = request.Headers[IdentityCookies.CsrfHeader].ToString();
        if (!Guid.TryParseExact(first, "N", out var sessionId) || !tokens.IsValid(sessionId, csrfCookie, header))
        {
            await Reject(context, AuthFailure.CsrfRejected);
            return;
        }

        await next(context);
    }

    /// <summary>
    /// Credential-presenting and session-creating endpoints. They are unusable with a stale or broken cookie otherwise, and
    /// they return nothing a cross-site page could read; the Origin check above still applies to them.
    /// </summary>
    private static bool IsTokenExempt(PathString path) =>
        path.Equals("/auth/staff/login", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/auth/guest/start", StringComparison.OrdinalIgnoreCase);

    private bool IsAllowedOrigin(HttpRequest request, string origin) => IsAllowedOrigin(request, origin, options.Value);

    /// <summary>The request's own origin or an entry of <see cref="MarrowIdentityOptions.AllowedOrigins"/>; never empty or <c>null</c>.</summary>
    internal static bool IsAllowedOrigin(HttpRequest request, string origin, MarrowIdentityOptions options)
    {
        if (string.IsNullOrEmpty(origin) || origin == "null")
        {
            return false;
        }

        var own = $"{request.Scheme}://{request.Host.Value}";
        return string.Equals(origin, own, StringComparison.OrdinalIgnoreCase)
            || options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }

    private async Task Reject(HttpContext context, AuthFailure failure)
    {
        audit.CsrfRejected(failure, context.Request.Path.Value is { Length: <= 128 } path ? path : "(long)");
        await AuthProblem.WriteAsync(context, StatusCodes.Status403Forbidden, failure);
    }
}

/// <summary>Typed JSON failures: <c>{ "code": "snake_case", "status": 403, "traceId": "..." }</c>. No internal detail.</summary>
public static class AuthProblem
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Code(AuthFailure failure) => failure switch
    {
        AuthFailure.InvalidRequest => "invalid_request",
        AuthFailure.InvalidCredentials => "invalid_credentials",
        AuthFailure.MfaRequired => "mfa_required",
        AuthFailure.InvalidMfaCode => "invalid_mfa_code",
        AuthFailure.MfaNotEnrolled => "mfa_not_enrolled",
        AuthFailure.MfaAlreadyEnrolled => "mfa_already_enrolled",
        AuthFailure.InvalidRefreshToken => "invalid_refresh_token",
        AuthFailure.SessionInactive => "session_inactive",
        AuthFailure.GuestBanned => "guest_banned",
        AuthFailure.NotFound => "not_found",
        AuthFailure.CsrfRejected => "csrf_rejected",
        AuthFailure.OriginRejected => "origin_rejected",
        _ => "error",
    };

    public static int Status(AuthFailure failure) => failure switch
    {
        AuthFailure.InvalidRequest => StatusCodes.Status400BadRequest,
        AuthFailure.InvalidCredentials or AuthFailure.InvalidRefreshToken or AuthFailure.SessionInactive => StatusCodes.Status401Unauthorized,
        AuthFailure.MfaRequired or AuthFailure.InvalidMfaCode or AuthFailure.GuestBanned or AuthFailure.CsrfRejected or AuthFailure.OriginRejected => StatusCodes.Status403Forbidden,
        AuthFailure.MfaNotEnrolled or AuthFailure.MfaAlreadyEnrolled => StatusCodes.Status409Conflict,
        AuthFailure.NotFound => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static Task WriteAsync(HttpContext context, int status, AuthFailure failure)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(
            JsonSerializer.Serialize(new { code = Code(failure), status, traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier }, Json));
    }

    public static IResult ToResult(AuthFailure failure) =>
        Results.Json(
            new { code = Code(failure), status = Status(failure), traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() },
            Json,
            statusCode: Status(failure));
}
