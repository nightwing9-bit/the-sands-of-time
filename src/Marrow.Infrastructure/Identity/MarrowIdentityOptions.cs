namespace Marrow.Infrastructure.Identity;

/// <summary>
/// Identity configuration (section <c>Identity</c>). There is no default secret: the master secret must come
/// from the environment, user secrets or a vault, never from a committed file.
/// </summary>
public sealed class MarrowIdentityOptions
{
    public const string SectionName = "Identity";

    /// <summary>
    /// Master secret (at least 32 characters, not a placeholder). Per-purpose keys (access JWT, guest cookie MAC,
    /// CSRF MAC, TOTP-at-rest encryption) are derived from it with HKDF. Only <c>Development</c> may omit it,
    /// in which case an ephemeral random key is generated per process.
    /// </summary>
    public string? MasterSecret { get; set; }

    public string Issuer { get; set; } = "the-sands-of-time";
    public string Audience { get; set; } = "the-sands-of-time-api";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Lifetime of one refresh token. Every refresh rotates it.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Absolute cap of a staff session regardless of rotation.</summary>
    public TimeSpan StaffSessionMaxLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How long a successful TOTP step-up keeps privileged staff actions available.</summary>
    public TimeSpan MfaFreshness { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Sliding lifetime of a remembered guest session.</summary>
    public TimeSpan GuestSessionLifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Extra browser origins (besides the request's own origin) allowed to make cookie-authenticated writes.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    public string TotpIssuer { get; set; } = "The Sands Of Time";

    /// <summary>Development-only staff seeding. Passwords must come from user secrets or the environment.</summary>
    public DevSeedOptions DevSeed { get; set; } = new();
}

public sealed class DevSeedOptions
{
    public string OperatorUserName { get; set; } = "operator@dev.sands.local";
    public string? OperatorPassword { get; set; }
    public string SupervisorUserName { get; set; } = "supervisor@dev.sands.local";
    public string? SupervisorPassword { get; set; }
}
