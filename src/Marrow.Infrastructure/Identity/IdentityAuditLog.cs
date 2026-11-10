using Microsoft.Extensions.Logging;

namespace Marrow.Infrastructure.Identity;

/// <summary>
/// The only way identity code logs. Every method accepts just typed, non-secret values (opaque GUIDs and enums), so a
/// token, cookie, password, TOTP code, user name or e-mail address cannot reach a log line. Correlation comes from the
/// ambient trace scope configured by the hosts.
/// </summary>
public sealed partial class IdentityAuditLog(ILogger<IdentityAuditLog> logger)
{
    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "identity.staff.login.succeeded user={UserId} session={SessionId} mfa={Mfa}")]
    public partial void StaffLoginSucceeded(Guid userId, Guid sessionId, bool mfa);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "identity.staff.login.failed reason={Failure} user={UserId}")]
    public partial void StaffLoginFailed(AuthFailure failure, Guid? userId);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Information, Message = "identity.staff.refresh.rotated user={UserId} session={SessionId}")]
    public partial void RefreshRotated(Guid userId, Guid sessionId);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Warning, Message = "identity.staff.refresh.rejected reason={Reason} session={SessionId}")]
    public partial void RefreshRejected(string reason, Guid? sessionId);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "identity.staff.refresh.replay.session_revoked session={SessionId}")]
    public partial void RefreshReplayRevoked(Guid sessionId);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Information, Message = "identity.staff.session.revoked session={SessionId} reason={Reason}")]
    public partial void StaffSessionRevoked(Guid sessionId, string reason);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Information, Message = "identity.staff.mfa.verified user={UserId} session={SessionId}")]
    public partial void MfaVerified(Guid userId, Guid sessionId);

    [LoggerMessage(EventId = 3008, Level = LogLevel.Warning, Message = "identity.staff.mfa.failed reason={Failure} user={UserId}")]
    public partial void MfaFailed(AuthFailure failure, Guid userId);

    [LoggerMessage(EventId = 3009, Level = LogLevel.Information, Message = "identity.staff.mfa.enrolled user={UserId}")]
    public partial void MfaEnrolled(Guid userId);

    [LoggerMessage(EventId = 3010, Level = LogLevel.Information, Message = "identity.guest.session.created guest={GuestId} session={SessionId}")]
    public partial void GuestCreated(Guid guestId, Guid sessionId);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Information, Message = "identity.guest.session.resumed guest={GuestId} session={SessionId}")]
    public partial void GuestResumed(Guid guestId, Guid sessionId);

    [LoggerMessage(EventId = 3012, Level = LogLevel.Warning, Message = "identity.guest.session.rejected reason={Reason}")]
    public partial void GuestRejected(string reason);

    [LoggerMessage(EventId = 3013, Level = LogLevel.Warning, Message = "identity.guest.banned guest={GuestId}")]
    public partial void GuestBanned(Guid guestId);

    [LoggerMessage(EventId = 3014, Level = LogLevel.Information, Message = "identity.guest.session.revoked session={SessionId} reason={Reason}")]
    public partial void GuestSessionRevoked(Guid sessionId, string reason);

    [LoggerMessage(EventId = 3015, Level = LogLevel.Warning, Message = "identity.csrf.rejected reason={Failure} path={Path}")]
    public partial void CsrfRejected(AuthFailure failure, string path);

    [LoggerMessage(EventId = 3016, Level = LogLevel.Warning, Message = "identity.access.session_inactive reason={Reason}")]
    public partial void AccessSessionInactive(string reason);

    [LoggerMessage(EventId = 3017, Level = LogLevel.Information, Message = "identity.dev_seed {Outcome} role={Role}")]
    public partial void DevSeed(string outcome, string role);

    [LoggerMessage(EventId = 3018, Level = LogLevel.Warning, Message = "identity.master_secret.ephemeral Development run generated a process-local key; sessions do not survive a restart")]
    public partial void EphemeralKey();
}
