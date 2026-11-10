namespace Marrow.Infrastructure.Identity;

/// <summary>Authentication scheme names. Staff and guest never share a scheme.</summary>
public static class IdentitySchemes
{
    public const string StaffBearer = "MarrowStaffBearer";
    public const string GuestSession = "MarrowGuestSession";
}

/// <summary>Authorization policy names. Domains attach these (or stricter ones) to their own endpoints and hubs.</summary>
public static class IdentityPolicies
{
    /// <summary>A verified staff access token for a live server-side session. No MFA needed.</summary>
    public const string Staff = "marrow.staff";

    /// <summary>Staff whose session passed a fresh TOTP step-up. Required for privileged staff actions.</summary>
    public const string StaffPrivileged = "marrow.staff.privileged";

    /// <summary>Privileged staff holding the supervisor role.</summary>
    public const string StaffSupervisor = "marrow.staff.supervisor";

    /// <summary>A live remembered guest session. Never satisfied by a staff principal.</summary>
    public const string Guest = "marrow.guest";
}

public static class IdentityRoles
{
    public const string Operator = "operator";
    public const string Supervisor = "supervisor";
    public const string Guest = "guest";

    public static readonly IReadOnlyList<string> StaffRoles = [Operator, Supervisor];
}

public static class IdentityClaimTypes
{
    public const string Subject = "sub";
    public const string Role = "role";
    public const string SessionId = "sid";
    public const string Kind = "marrow_kind";
    public const string Mfa = "marrow_mfa";

    public const string KindStaff = "staff";
    public const string KindGuest = "guest";
}

public static class IdentityCookies
{
    public const string GuestSession = "__Host-sands_guest";
    public const string GuestCsrf = "__Host-sands_csrf_guest";
    public const string StaffCsrf = "__Host-sands_csrf_staff";
    public const string StaffRefresh = "__Secure-sands_refresh";
    public const string StaffRefreshPath = "/auth/staff";
    public const string CsrfHeader = "X-CSRF-Token";
}

/// <summary>Typed failures. Wire codes are the snake_case names; callers never see internal reasons.</summary>
public enum AuthFailure
{
    InvalidRequest,
    InvalidCredentials,
    MfaRequired,
    InvalidMfaCode,
    MfaNotEnrolled,
    MfaAlreadyEnrolled,
    InvalidRefreshToken,
    SessionInactive,
    GuestBanned,
    NotFound,
    CsrfRejected,
    OriginRejected,
}
