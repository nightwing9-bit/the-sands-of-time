using Marrow.Infrastructure.Identity.Csrf;
using Marrow.Infrastructure.Identity.Guest;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Infrastructure.Identity.Staff;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Marrow.Infrastructure.Identity;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers staff identity (ASP.NET Core Identity, access JWT, rotating refresh sessions, TOTP), remembered guest
    /// sessions, the Kernel <see cref="IActorContext"/> implementation and the authorization policies. Requires
    /// <c>AddMarrowDependencies</c> (the pooled <c>NpgsqlDataSource</c>). Throws at startup when the master secret is
    /// missing, short or a placeholder outside Development.
    /// </summary>
    public static IServiceCollection AddMarrowIdentity(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(MarrowIdentityOptions.SectionName);
        var bound = section.Get<MarrowIdentityOptions>() ?? new MarrowIdentityOptions();
        var keys = IdentityKeyMaterial.Create(bound.MasterSecret, environment.IsDevelopment());
        services.Configure<MarrowIdentityOptions>(section);
        services.AddSingleton(keys);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddSingleton<IdentityAuditLog>();
        services.AddSingleton<CsrfTokens>();

        services.AddIdentityCore<StaffUser>(o =>
            {
                o.Password.RequiredLength = 12;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = true;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.User.RequireUniqueEmail = false;
                o.Tokens.AuthenticatorTokenProvider = TokenOptions.DefaultAuthenticatorProvider;
            })
            .AddUserStore<PgStaffUserStore>()
            .AddTokenProvider<AuthenticatorTokenProvider<StaffUser>>(TokenOptions.DefaultAuthenticatorProvider);
        services.AddScoped<StaffAuthService>();
        services.AddScoped<GuestSessionService>();

        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, StaffBearerConfiguration>();
        services.AddAuthentication()
            .AddJwtBearer(IdentitySchemes.StaffBearer, _ => { })
            .AddScheme<AuthenticationSchemeOptions, GuestSessionAuthenticationHandler>(IdentitySchemes.GuestSession, _ => { });

        services.AddSingleton<IAuthorizationHandler, MfaRequirementHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, TypedAuthorizationResultHandler>();
        services.AddAuthorization(o =>
        {
            o.AddPolicy(IdentityPolicies.Staff, p => p
                .AddAuthenticationSchemes(IdentitySchemes.StaffBearer)
                .RequireAuthenticatedUser()
                .RequireClaim(IdentityClaimTypes.Kind, IdentityClaimTypes.KindStaff)
                .RequireRole(IdentityRoles.StaffRoles));
            o.AddPolicy(IdentityPolicies.StaffPrivileged, p => p
                .AddAuthenticationSchemes(IdentitySchemes.StaffBearer)
                .RequireAuthenticatedUser()
                .RequireClaim(IdentityClaimTypes.Kind, IdentityClaimTypes.KindStaff)
                .RequireRole(IdentityRoles.StaffRoles)
                .AddRequirements(new MfaRequirement()));
            o.AddPolicy(IdentityPolicies.StaffSupervisor, p => p
                .AddAuthenticationSchemes(IdentitySchemes.StaffBearer)
                .RequireAuthenticatedUser()
                .RequireClaim(IdentityClaimTypes.Kind, IdentityClaimTypes.KindStaff)
                .RequireRole(IdentityRoles.Supervisor)
                .AddRequirements(new MfaRequirement()));
            o.AddPolicy(IdentityPolicies.Guest, p => p
                .AddAuthenticationSchemes(IdentitySchemes.GuestSession)
                .RequireAuthenticatedUser()
                .RequireClaim(IdentityClaimTypes.Kind, IdentityClaimTypes.KindGuest)
                .RequireRole(IdentityRoles.Guest));
        });

        services.AddScoped<IActorContext, HttpActorContext>();
        services.AddMarrowDomainPolicyDefaults();
        services.AddHostedService<IdentityStartup>();
        return services;
    }

    /// <summary>CSRF/origin checks, then authentication and authorization. Call before mapping endpoints.</summary>
    public static IApplicationBuilder UseMarrowIdentity(this IApplicationBuilder app)
    {
        app.UseMiddleware<CsrfOriginMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}
