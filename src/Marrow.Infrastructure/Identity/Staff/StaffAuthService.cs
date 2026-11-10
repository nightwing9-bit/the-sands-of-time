using System.Globalization;
using System.Security.Claims;
using Marrow.Kernel.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using NpgsqlTypes;

namespace Marrow.Infrastructure.Identity.Staff;

/// <summary>Credentials and tokens issued to a staff client. The refresh token is a cookie value, never a response body field.</summary>
public sealed record StaffSessionTokens(
    Guid UserId,
    Guid SessionId,
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    bool MfaEnrolled,
    DateTimeOffset? MfaVerifiedUntil,
    IReadOnlyList<string> Roles);

public sealed record MfaEnrollment(string SharedKey, string OtpAuthUri);

/// <summary>Server-side view of a staff session used to authorize every access token (revocation is immediate).</summary>
public sealed record StaffSessionState(bool Active, string? InactiveReason, IReadOnlyList<string> Roles, DateTimeOffset? MfaVerifiedAt);

/// <summary>
/// Staff login, rotating refresh sessions, TOTP enrollment/step-up and server-side revocation. Passwords, lockout and
/// TOTP verification are delegated to ASP.NET Core Identity's <see cref="UserManager{TUser}"/>. Refresh tokens are
/// opaque, stored only as SHA-256 hashes, and rotate on every use; presenting an already-consumed token revokes the
/// whole session (replay detection).
/// </summary>
public sealed class StaffAuthService(
    UserManager<StaffUser> users,
    NpgsqlDataSource dataSource,
    IdentityKeyMaterial keys,
    IOptions<MarrowIdentityOptions> options,
    TimeProvider time,
    IdentityAuditLog audit)
{
    private static readonly TimeSpan TotpReplayWindow = TimeSpan.FromMinutes(3);
    private static readonly StaffUser TimingDummy = new() { UserName = "dummy" };
    private static readonly string TimingDummyHash = new PasswordHasher<StaffUser>().HashPassword(TimingDummy, "timing-dummy-Password-1!");

    private MarrowIdentityOptions Options => options.Value;

    public async Task<Result<StaffSessionTokens, AuthFailure>> LoginAsync(string userName, string password, string? totpCode, CancellationToken ct)
    {
        var user = await users.FindByNameAsync(userName);
        if (user is null || user.DisabledAt is not null)
        {
            // Spend comparable time so account existence is not observable from latency.
            new PasswordHasher<StaffUser>().VerifyHashedPassword(TimingDummy, TimingDummyHash, password);
            audit.StaffLoginFailed(AuthFailure.InvalidCredentials, user?.Id);
            return Failure<StaffSessionTokens>(AuthFailure.InvalidCredentials);
        }

        // A locked-out account and a wrong password are indistinguishable to the client.
        if (await users.IsLockedOutAsync(user))
        {
            audit.StaffLoginFailed(AuthFailure.InvalidCredentials, user.Id);
            return Failure<StaffSessionTokens>(AuthFailure.InvalidCredentials);
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            audit.StaffLoginFailed(AuthFailure.InvalidCredentials, user.Id);
            return Failure<StaffSessionTokens>(AuthFailure.InvalidCredentials);
        }

        var now = time.GetUtcNow();
        DateTimeOffset? mfaAt = null;
        if (!string.IsNullOrEmpty(totpCode))
        {
            var mfa = await CheckTotpAsync(user, totpCode, ct);
            if (mfa.IsFailure)
            {
                return Failure<StaffSessionTokens>(mfa.Error);
            }

            mfaAt = now;
        }

        await users.ResetAccessFailedCountAsync(user);
        var tokens = await CreateSessionAsync(user, mfaAt, ct);
        audit.StaffLoginSucceeded(user.Id, tokens.SessionId, mfaAt is not null);
        return Result<StaffSessionTokens, AuthFailure>.Success(tokens);
    }

    /// <summary>
    /// Rotates a refresh token. Success consumes the presented token and issues a new one for the same session.
    /// Unknown, mismatched, expired, revoked and replayed tokens are all <see cref="AuthFailure.InvalidRefreshToken"/>
    /// to the caller; a replayed (already consumed) token additionally revokes the session.
    /// </summary>
    public async Task<Result<StaffSessionTokens, AuthFailure>> RefreshAsync(string? presentedToken, CancellationToken ct)
    {
        if (!RefreshTokenFormat.TryParse(presentedToken, out var parsed))
        {
            audit.RefreshRejected("malformed", null);
            return Failure<StaffSessionTokens>(AuthFailure.InvalidRefreshToken);
        }

        var now = time.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        // Lock the session row first, then read the token row in a NEW statement. Under READ COMMITTED a single
        // joined SELECT ... FOR UPDATE would re-check only the locked row and could return a stale, unconsumed token row
        // to a request that waited for a concurrent rotation, hiding the replay.
        DateTimeOffset sessionExpires;
        DateTimeOffset? revokedAt;
        Guid userId;
        await using (var lockSession = new NpgsqlCommand(
            "SELECT user_id, expires_at, revoked_at FROM marrow_identity.staff_sessions WHERE id = @sid FOR UPDATE", connection, tx))
        {
            lockSession.Parameters.AddWithValue("sid", parsed.SessionId);
            await using var reader = await lockSession.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                audit.RefreshRejected("unknown", parsed.SessionId);
                return Failure<StaffSessionTokens>(AuthFailure.InvalidRefreshToken);
            }

            userId = reader.GetGuid(0);
            sessionExpires = Utc(reader.GetDateTime(1));
            revokedAt = reader.IsDBNull(2) ? null : Utc(reader.GetDateTime(2));
        }

        byte[] storedHash;
        DateTimeOffset tokenExpires;
        DateTimeOffset? consumedAt;
        await using (var selectToken = new NpgsqlCommand(
            "SELECT token_hash, expires_at, consumed_at FROM marrow_identity.staff_refresh_tokens WHERE id = @tid AND session_id = @sid", connection, tx))
        {
            selectToken.Parameters.AddWithValue("tid", parsed.TokenId);
            selectToken.Parameters.AddWithValue("sid", parsed.SessionId);
            await using var reader = await selectToken.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                audit.RefreshRejected("unknown", parsed.SessionId);
                return Failure<StaffSessionTokens>(AuthFailure.InvalidRefreshToken);
            }

            storedHash = (byte[])reader.GetValue(0);
            tokenExpires = Utc(reader.GetDateTime(1));
            consumedAt = reader.IsDBNull(2) ? null : Utc(reader.GetDateTime(2));
        }

        // Without the secret nothing changes: knowing a token id must not let anyone revoke or rotate.
        if (!SecretTokens.HashMatches(storedHash, parsed.Secret))
        {
            audit.RefreshRejected("secret_mismatch", parsed.SessionId);
            return Failure<StaffSessionTokens>(AuthFailure.InvalidRefreshToken);
        }

        if (revokedAt is not null)
        {
            audit.RefreshRejected("session_revoked", parsed.SessionId);
            return Failure<StaffSessionTokens>(AuthFailure.InvalidRefreshToken);
        }

        if (consumedAt is not null)
        {
            // A valid secret that was already used: the token family is compromised or a client raced itself.
            await RevokeSessionInTransactionAsync(connection, tx, parsed.SessionId, "refresh_replay", now, ct);
            await tx.CommitAsync(ct);
            audit.RefreshReplayRevoked(parsed.SessionId);
            return Failure<StaffSessionTokens>(AuthFailure.InvalidRefreshToken);
        }

        if (tokenExpires <= now || sessionExpires <= now)
        {
            audit.RefreshRejected("expired", parsed.SessionId);
            return Failure<StaffSessionTokens>(AuthFailure.InvalidRefreshToken);
        }

        var user = await users.FindByIdAsync(userId.ToString("D", CultureInfo.InvariantCulture));
        if (user is null || user.DisabledAt is not null)
        {
            await RevokeSessionInTransactionAsync(connection, tx, parsed.SessionId, "user_disabled", now, ct);
            await tx.CommitAsync(ct);
            audit.RefreshRejected("user_disabled", parsed.SessionId);
            return Failure<StaffSessionTokens>(AuthFailure.InvalidRefreshToken);
        }

        await using (var consume = new NpgsqlCommand(
            "UPDATE marrow_identity.staff_refresh_tokens SET consumed_at = @now WHERE id = @tid AND consumed_at IS NULL", connection, tx))
        {
            consume.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            consume.Parameters.AddWithValue("tid", parsed.TokenId);
            if (await consume.ExecuteNonQueryAsync(ct) != 1)
            {
                // Cannot happen while the session row is locked; if it does, treat it as a replay, never as success.
                await RevokeSessionInTransactionAsync(connection, tx, parsed.SessionId, "refresh_replay", now, ct);
                await tx.CommitAsync(ct);
                audit.RefreshReplayRevoked(parsed.SessionId);
                return Failure<StaffSessionTokens>(AuthFailure.InvalidRefreshToken);
            }
        }

        var next = await InsertRefreshTokenAsync(connection, tx, parsed.SessionId, now, sessionExpires, ct);
        await using (var touch = new NpgsqlCommand(
            "UPDATE marrow_identity.staff_sessions SET last_refreshed_at = @now WHERE id = @sid", connection, tx))
        {
            touch.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            touch.Parameters.AddWithValue("sid", parsed.SessionId);
            await touch.ExecuteNonQueryAsync(ct);
        }

        var mfaAt = await ReadMfaAsync(connection, tx, parsed.SessionId, ct);
        await tx.CommitAsync(ct);
        audit.RefreshRotated(userId, parsed.SessionId);
        var roles = await users.GetRolesAsync(user);
        return Result<StaffSessionTokens, AuthFailure>.Success(BuildTokens(user, parsed.SessionId, roles, next.Token, next.ExpiresAt, mfaAt, now));
    }

    /// <summary>Revokes the session that owns a valid refresh token. Idempotent; an invalid token changes nothing.</summary>
    public async Task LogoutAsync(string? presentedToken, CancellationToken ct)
    {
        if (!RefreshTokenFormat.TryParse(presentedToken, out var parsed))
        {
            return;
        }

        await using var select = dataSource.CreateCommand(
            "SELECT token_hash FROM marrow_identity.staff_refresh_tokens WHERE id = @tid AND session_id = @sid");
        select.Parameters.AddWithValue("tid", parsed.TokenId);
        select.Parameters.AddWithValue("sid", parsed.SessionId);
        if (await select.ExecuteScalarAsync(ct) is byte[] hash && SecretTokens.HashMatches(hash, parsed.Secret))
        {
            await RevokeSessionAsync(parsed.SessionId, "logout", ct);
        }
    }

    /// <summary>Server-side revocation. Access tokens of the session stop working on their next request.</summary>
    public async Task<bool> RevokeSessionAsync(Guid sessionId, string reason, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var changed = await RevokeSessionInTransactionAsync(connection, tx, sessionId, reason, time.GetUtcNow(), ct);
        await tx.CommitAsync(ct);
        if (changed)
        {
            audit.StaffSessionRevoked(sessionId, reason);
        }

        return changed;
    }

    public async Task<StaffSessionState> GetSessionStateAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT s.expires_at, s.revoked_at, s.mfa_verified_at, u.disabled_at, " +
            "COALESCE((SELECT array_agg(role ORDER BY role) FROM marrow_identity.staff_user_roles r WHERE r.user_id = u.id), '{}') " +
            "FROM marrow_identity.staff_sessions s JOIN marrow_identity.staff_users u ON u.id = s.user_id WHERE s.id = @sid AND s.user_id = @uid");
        command.Parameters.AddWithValue("sid", sessionId);
        command.Parameters.AddWithValue("uid", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new StaffSessionState(false, "unknown", [], null);
        }

        string? reason = null;
        if (!reader.IsDBNull(1))
        {
            reason = "revoked";
        }
        else if (!reader.IsDBNull(3))
        {
            reason = "user_disabled";
        }
        else if (Utc(reader.GetDateTime(0)) <= time.GetUtcNow())
        {
            reason = "expired";
        }

        return new StaffSessionState(
            reason is null,
            reason,
            reader.GetFieldValue<string[]>(4),
            reader.IsDBNull(2) ? null : Utc(reader.GetDateTime(2)));
    }

    public bool IsMfaFresh(DateTimeOffset? verifiedAt) =>
        verifiedAt is { } at && at + Options.MfaFreshness > time.GetUtcNow() && at <= time.GetUtcNow() + TimeSpan.FromSeconds(5);

    /// <summary>Starts TOTP enrollment. Requires the current password; refuses when MFA is already enabled.</summary>
    public async Task<Result<MfaEnrollment, AuthFailure>> BeginMfaEnrollmentAsync(Guid userId, string currentPassword, CancellationToken ct)
    {
        var user = await FindActiveAsync(userId);
        if (user is null)
        {
            return Failure<MfaEnrollment>(AuthFailure.NotFound);
        }

        if (await users.IsLockedOutAsync(user))
        {
            return Failure<MfaEnrollment>(AuthFailure.InvalidCredentials);
        }

        if (!await users.CheckPasswordAsync(user, currentPassword))
        {
            await users.AccessFailedAsync(user);
            return Failure<MfaEnrollment>(AuthFailure.InvalidCredentials);
        }

        if (user.TwoFactorEnabled)
        {
            return Failure<MfaEnrollment>(AuthFailure.MfaAlreadyEnrolled);
        }

        await users.ResetAuthenticatorKeyAsync(user);
        var key = await users.GetAuthenticatorKeyAsync(user) ?? throw new InvalidOperationException("Authenticator key was not stored.");
        var issuer = Uri.EscapeDataString(Options.TotpIssuer);
        var label = Uri.EscapeDataString(user.UserName);
        return Result<MfaEnrollment, AuthFailure>.Success(
            new MfaEnrollment(key, $"otpauth://totp/{issuer}:{label}?secret={key}&issuer={issuer}&digits=6&period=30"));
    }

    /// <summary>Confirms enrollment with a first valid code, enables MFA and marks the calling session as stepped-up.</summary>
    public async Task<Result<DateTimeOffset, AuthFailure>> ConfirmMfaEnrollmentAsync(Guid userId, Guid sessionId, string code, CancellationToken ct)
    {
        var user = await FindActiveAsync(userId);
        if (user is null)
        {
            return Failure<DateTimeOffset>(AuthFailure.NotFound);
        }

        if (user.TwoFactorEnabled)
        {
            return Failure<DateTimeOffset>(AuthFailure.MfaAlreadyEnrolled);
        }

        if (string.IsNullOrEmpty(await users.GetAuthenticatorKeyAsync(user)))
        {
            return Failure<DateTimeOffset>(AuthFailure.MfaNotEnrolled);
        }

        var check = await CheckTotpAsync(user, code, ct);
        if (check.IsFailure)
        {
            return Failure<DateTimeOffset>(check.Error);
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        audit.MfaEnrolled(userId);
        return await MarkMfaVerifiedAsync(userId, sessionId, ct);
    }

    /// <summary>Step-up: a valid, unused TOTP code makes privileged staff actions available for <see cref="MarrowIdentityOptions.MfaFreshness"/>.</summary>
    public async Task<Result<DateTimeOffset, AuthFailure>> VerifyMfaAsync(Guid userId, Guid sessionId, string code, CancellationToken ct)
    {
        var user = await FindActiveAsync(userId);
        if (user is null)
        {
            return Failure<DateTimeOffset>(AuthFailure.NotFound);
        }

        if (!user.TwoFactorEnabled)
        {
            return Failure<DateTimeOffset>(AuthFailure.MfaNotEnrolled);
        }

        var check = await CheckTotpAsync(user, code, ct);
        if (check.IsFailure)
        {
            return Failure<DateTimeOffset>(check.Error);
        }

        return await MarkMfaVerifiedAsync(userId, sessionId, ct);
    }

    public async Task<IReadOnlyList<string>> GetRolesAsync(Guid userId)
    {
        var user = await FindActiveAsync(userId);
        return user is null ? [] : [.. await users.GetRolesAsync(user)];
    }

    public async Task<bool> IsMfaEnrolledAsync(Guid userId) => (await FindActiveAsync(userId))?.TwoFactorEnabled ?? false;

    private async Task<Result<DateTimeOffset, AuthFailure>> MarkMfaVerifiedAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var command = dataSource.CreateCommand(
            "UPDATE marrow_identity.staff_sessions SET mfa_verified_at = @now WHERE id = @sid AND user_id = @uid AND revoked_at IS NULL AND expires_at > @now");
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
        command.Parameters.AddWithValue("sid", sessionId);
        command.Parameters.AddWithValue("uid", userId);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            return Failure<DateTimeOffset>(AuthFailure.SessionInactive);
        }

        audit.MfaVerified(userId, sessionId);
        return Result<DateTimeOffset, AuthFailure>.Success(now + Options.MfaFreshness);
    }

    /// <summary>
    /// Verifies a TOTP code with Identity's authenticator provider (30 s steps, small drift window), counts failures
    /// toward lockout, and consumes the code atomically so a code can be used once per replay window.
    /// </summary>
    private async Task<Result<bool, AuthFailure>> CheckTotpAsync(StaffUser user, string code, CancellationToken ct)
    {
        var normalized = new string([.. code.Where(char.IsDigit)]);
        if (await users.IsLockedOutAsync(user))
        {
            audit.MfaFailed(AuthFailure.InvalidMfaCode, user.Id);
            return Failure<bool>(AuthFailure.InvalidMfaCode);
        }

        var valid = normalized.Length == 6
            && await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, normalized);
        if (valid)
        {
            var now = time.GetUtcNow();
            await using var consume = dataSource.CreateCommand(
                "UPDATE marrow_identity.staff_users SET totp_last_code = @code, totp_last_accepted_at = @now " +
                "WHERE id = @id AND (totp_last_code IS DISTINCT FROM @code OR totp_last_accepted_at IS NULL OR totp_last_accepted_at <= @cutoff)");
            consume.Parameters.AddWithValue("code", normalized);
            consume.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            consume.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) { Value = (now - TotpReplayWindow).UtcDateTime });
            consume.Parameters.AddWithValue("id", user.Id);
            valid = await consume.ExecuteNonQueryAsync(ct) == 1;
        }

        if (!valid)
        {
            await users.AccessFailedAsync(user);
            audit.MfaFailed(AuthFailure.InvalidMfaCode, user.Id);
            return Failure<bool>(AuthFailure.InvalidMfaCode);
        }

        await users.ResetAccessFailedCountAsync(user);
        return Result<bool, AuthFailure>.Success(true);
    }

    private async Task<StaffSessionTokens> CreateSessionAsync(StaffUser user, DateTimeOffset? mfaAt, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var sessionId = Guid.NewGuid();
        var sessionExpires = now + Options.StaffSessionMaxLifetime;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO marrow_identity.staff_sessions (id, user_id, created_at, last_refreshed_at, expires_at, mfa_verified_at) " +
            "VALUES (@id, @uid, @now, @now, @exp, @mfa)", connection, tx))
        {
            insert.Parameters.AddWithValue("id", sessionId);
            insert.Parameters.AddWithValue("uid", user.Id);
            insert.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            insert.Parameters.Add(new NpgsqlParameter("exp", NpgsqlDbType.TimestampTz) { Value = sessionExpires.UtcDateTime });
            insert.Parameters.Add(new NpgsqlParameter("mfa", NpgsqlDbType.TimestampTz) { Value = mfaAt is { } m ? m.UtcDateTime : DBNull.Value });
            await insert.ExecuteNonQueryAsync(ct);
        }

        var refresh = await InsertRefreshTokenAsync(connection, tx, sessionId, now, sessionExpires, ct);
        await tx.CommitAsync(ct);
        var roles = await users.GetRolesAsync(user);
        return BuildTokens(user, sessionId, roles, refresh.Token, refresh.ExpiresAt, mfaAt, now);
    }

    private async Task<(string Token, DateTimeOffset ExpiresAt)> InsertRefreshTokenAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid sessionId, DateTimeOffset now, DateTimeOffset sessionExpires, CancellationToken ct)
    {
        var tokenId = Guid.NewGuid();
        var secret = SecretTokens.NewSecret();
        var expires = now + Options.RefreshTokenLifetime;
        if (expires > sessionExpires)
        {
            expires = sessionExpires;
        }

        await using var insert = new NpgsqlCommand(
            "INSERT INTO marrow_identity.staff_refresh_tokens (id, session_id, token_hash, issued_at, expires_at) VALUES (@id, @sid, @hash, @now, @exp)",
            connection, tx);
        insert.Parameters.AddWithValue("id", tokenId);
        insert.Parameters.AddWithValue("sid", sessionId);
        insert.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = SecretTokens.Hash(secret) });
        insert.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
        insert.Parameters.Add(new NpgsqlParameter("exp", NpgsqlDbType.TimestampTz) { Value = expires.UtcDateTime });
        await insert.ExecuteNonQueryAsync(ct);
        return (RefreshTokenFormat.Format(sessionId, tokenId, secret), expires);
    }

    private static async Task<DateTimeOffset?> ReadMfaAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid sessionId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT mfa_verified_at FROM marrow_identity.staff_sessions WHERE id = @sid", connection, tx);
        command.Parameters.AddWithValue("sid", sessionId);
        return await command.ExecuteScalarAsync(ct) is DateTime at ? Utc(at) : null;
    }

    private static async Task<bool> RevokeSessionInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid sessionId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE marrow_identity.staff_sessions SET revoked_at = @now, revoke_reason = @reason WHERE id = @sid AND revoked_at IS NULL", connection, tx);
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("sid", sessionId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private StaffSessionTokens BuildTokens(
        StaffUser user, Guid sessionId, IList<string> roles, string refreshToken, DateTimeOffset refreshExpires, DateTimeOffset? mfaAt, DateTimeOffset now)
    {
        var expires = now + Options.AccessTokenLifetime;
        var claims = new List<Claim>
        {
            new(IdentityClaimTypes.Subject, "staff:" + user.Id.ToString("D", CultureInfo.InvariantCulture)),
            new(IdentityClaimTypes.SessionId, sessionId.ToString("D", CultureInfo.InvariantCulture)),
            new(IdentityClaimTypes.Kind, IdentityClaimTypes.KindStaff),
            new("jti", Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(roles.Select(r => new Claim(IdentityClaimTypes.Role, r)));
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = Options.Issuer,
            Audience = Options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(keys.AccessTokenSigningKey(), SecurityAlgorithms.HmacSha256),
        };
        var access = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
        return new StaffSessionTokens(
            user.Id, sessionId, access, expires, refreshToken, refreshExpires, user.TwoFactorEnabled,
            IsMfaFresh(mfaAt) ? mfaAt + Options.MfaFreshness : null, [.. roles]);
    }

    private async Task<StaffUser?> FindActiveAsync(Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString("D", CultureInfo.InvariantCulture));
        return user is { DisabledAt: null } ? user : null;
    }

    private static Result<T, AuthFailure> Failure<T>(AuthFailure failure) => Result<T, AuthFailure>.Failure(failure);

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>Refresh token wire format: <c>{sessionId:N}.{tokenId:N}.{secret}</c>.</summary>
internal readonly record struct RefreshTokenFormat(Guid SessionId, Guid TokenId, string Secret)
{
    public static string Format(Guid sessionId, Guid tokenId, string secret) => $"{sessionId:N}.{tokenId:N}.{secret}";

    public static bool TryParse(string? value, out RefreshTokenFormat parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(value) || value.Length > 256)
        {
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length != 3
            || !Guid.TryParseExact(parts[0], "N", out var sessionId)
            || !Guid.TryParseExact(parts[1], "N", out var tokenId)
            || parts[2].Length is < 16 or > 128)
        {
            return false;
        }

        parsed = new RefreshTokenFormat(sessionId, tokenId, parts[2]);
        return true;
    }
}
