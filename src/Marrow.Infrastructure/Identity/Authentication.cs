using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Marrow.Infrastructure.Identity.Csrf;
using Marrow.Infrastructure.Identity.Guest;
using Marrow.Infrastructure.Identity.Staff;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Marrow.Infrastructure.Identity;

/// <summary>
/// Authenticates the remembered-guest cookie. The produced principal carries kind <c>guest</c>, role <c>guest</c> and
/// nothing else, so it can never satisfy a staff policy.
/// </summary>
public sealed class GuestSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    GuestSessionService guests) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cookie = Request.Cookies[IdentityCookies.GuestSession];
        if (string.IsNullOrEmpty(cookie))
        {
            return AuthenticateResult.NoResult();
        }

        var result = await guests.AuthenticateAsync(cookie, Context.RequestAborted);
        if (result.TryGetError(out var error))
        {
            return AuthenticateResult.Fail(AuthProblem.Code(error));
        }

        var guest = result.Value;
        var identity = new ClaimsIdentity(
            [
                new Claim(IdentityClaimTypes.Subject, guest.SubjectId),
                new Claim(IdentityClaimTypes.SessionId, guest.SessionId.ToString("D", CultureInfo.InvariantCulture)),
                new Claim(IdentityClaimTypes.Kind, IdentityClaimTypes.KindGuest),
                new Claim(IdentityClaimTypes.Role, IdentityRoles.Guest),
            ],
            IdentitySchemes.GuestSession,
            IdentityClaimTypes.Subject,
            IdentityClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), IdentitySchemes.GuestSession));
    }
}

/// <summary>Configures access-token validation and the per-request server-side session check.</summary>
internal sealed class StaffBearerConfiguration(IdentityKeyMaterial keys, IOptions<MarrowIdentityOptions> identity, TimeProvider time)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != IdentitySchemes.StaffBearer)
        {
            return;
        }

        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
        options.SaveToken = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = identity.Value.Issuer,
            ValidateAudience = true,
            ValidAudience = identity.Value.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = keys.AccessTokenSigningKey(),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(10),
            NameClaimType = IdentityClaimTypes.Subject,
            RoleClaimType = IdentityClaimTypes.Role,
            LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return expires is not null
                    && now <= expires.Value + parameters.ClockSkew
                    && (notBefore is null || now >= notBefore.Value - parameters.ClockSkew);
            },
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ValidateSessionAsync,
        };
    }

    public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

    /// <summary>
    /// The access JWT proves who the caller was when it was minted. Authority stays on the server: the session must still
    /// be live, and roles and the MFA step-up state are read from the database, not from the token. Revoking a session or a
    /// role therefore takes effect on the next request.
    /// </summary>
    private static async Task ValidateSessionAsync(TokenValidatedContext context)
    {
        var services = context.HttpContext.RequestServices;
        var audit = services.GetRequiredService<IdentityAuditLog>();
        var principal = context.Principal;
        var kind = principal?.FindFirst(IdentityClaimTypes.Kind)?.Value;
        var subject = principal?.FindFirst(IdentityClaimTypes.Subject)?.Value;
        var sid = principal?.FindFirst(IdentityClaimTypes.SessionId)?.Value;
        if (principal?.Identity is not ClaimsIdentity identity
            || kind != IdentityClaimTypes.KindStaff
            || subject is null || !subject.StartsWith("staff:", StringComparison.Ordinal)
            || !Guid.TryParse(subject.AsSpan(6), out var userId)
            || !Guid.TryParse(sid, out var sessionId))
        {
            audit.AccessSessionInactive("malformed_claims");
            context.Fail("malformed_claims");
            return;
        }

        var staff = services.GetRequiredService<StaffAuthService>();
        var state = await staff.GetSessionStateAsync(sessionId, userId, context.HttpContext.RequestAborted);
        if (!state.Active)
        {
            audit.AccessSessionInactive(state.InactiveReason ?? "inactive");
            context.Fail("session_inactive");
            return;
        }

        // Roles, tenant and MFA are server state: whatever the token carries is discarded and re-read from the database.
        foreach (var claim in identity.Claims
                     .Where(c => c.Type is IdentityClaimTypes.Role or IdentityClaimTypes.Tenant or IdentityClaimTypes.Mfa).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        foreach (var role in state.Roles)
        {
            identity.AddClaim(new Claim(IdentityClaimTypes.Role, role));
        }

        if (state.TenantId is { } tenant)
        {
            identity.AddClaim(new Claim(IdentityClaimTypes.Tenant, tenant));
        }

        if (staff.IsMfaFresh(state.MfaVerifiedAt))
        {
            identity.AddClaim(new Claim(IdentityClaimTypes.Mfa, "true"));
        }
    }
}

/// <summary>Requires the TOTP step-up claim that <see cref="StaffBearerConfiguration"/> adds only for a fresh, server-verified step-up.</summary>
public sealed class MfaRequirement : IAuthorizationRequirement;

public sealed class MfaRequirementHandler : AuthorizationHandler<MfaRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MfaRequirement requirement)
    {
        if (context.User.HasClaim(IdentityClaimTypes.Mfa, "true"))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Writes typed JSON for 401 and 403 authorization outcomes (including <c>mfa_required</c>).</summary>
public sealed class TypedAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
        {
            await next(context);
            return;
        }

        if (authorizeResult.Challenged)
        {
            await AuthProblem.WriteAsync(context, StatusCodes.Status401Unauthorized, AuthFailure.SessionInactive);
            return;
        }

        if (authorizeResult.Forbidden)
        {
            if (authorizeResult.AuthorizationFailure?.FailedRequirements.Any(r => r is MfaRequirement) == true)
            {
                await AuthProblem.WriteAsync(context, StatusCodes.Status403Forbidden, AuthFailure.MfaRequired);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"code\":\"forbidden\",\"status\":403}");
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
