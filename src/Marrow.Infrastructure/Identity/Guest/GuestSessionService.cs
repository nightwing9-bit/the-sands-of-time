using System.Globalization;
using Marrow.Kernel.Results;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Marrow.Infrastructure.Identity.Guest;

/// <summary>A verified guest. <see cref="SubjectId"/> is the stable actor subject (<c>guest:{guid}</c>).</summary>
public sealed record GuestIdentity(Guid GuestId, Guid SessionId)
{
    public string SubjectId => "guest:" + GuestId.ToString("D", CultureInfo.InvariantCulture);
}

public sealed record GuestSessionIssued(GuestIdentity Identity, string CookieValue, DateTimeOffset ExpiresAt, bool Resumed);

/// <summary>
/// Remembered guest sessions: a random guest principal plus a server-side session. The cookie is
/// <c>{sessionId:N}.{secret}.{mac}</c>; the HMAC is checked before any database access, the secret is stored only as a
/// SHA-256 hash, and the session can be revoked or its principal banned on the server at any time. No account,
/// password or staff credential is involved, and nothing here ever reads a client-submitted owner id.
/// </summary>
public sealed class GuestSessionService(
    NpgsqlDataSource dataSource,
    IdentityKeyMaterial keys,
    IOptions<MarrowIdentityOptions> options,
    TimeProvider time,
    IdentityAuditLog audit)
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    private MarrowIdentityOptions Options => options.Value;

    /// <summary>
    /// Resumes the session named by a valid cookie, or creates a new guest. A banned guest is refused (it is not given a
    /// fresh identity); an expired or revoked session simply starts a new guest.
    /// </summary>
    public async Task<Result<GuestSessionIssued, AuthFailure>> StartOrResumeAsync(string? cookieValue, CancellationToken ct)
    {
        if (GuestCookie.TryParse(cookieValue, keys, out var parsed))
        {
            var lookup = await LoadAsync(parsed.SessionId, ct);
            if (lookup is not null && SecretTokens.HashMatches(lookup.SecretHash, parsed.Secret))
            {
                if (lookup.BannedAt is not null)
                {
                    audit.GuestRejected("banned");
                    return Result<GuestSessionIssued, AuthFailure>.Failure(AuthFailure.GuestBanned);
                }

                var now = time.GetUtcNow();
                if (lookup.RevokedAt is null && lookup.ExpiresAt > now)
                {
                    var expires = now + Options.GuestSessionLifetime;
                    await TouchAsync(parsed.SessionId, lookup.GuestId, now, expires, ct);
                    audit.GuestResumed(lookup.GuestId, parsed.SessionId);
                    return Result<GuestSessionIssued, AuthFailure>.Success(
                        new GuestSessionIssued(new GuestIdentity(lookup.GuestId, parsed.SessionId), cookieValue!, expires, Resumed: true));
                }

                audit.GuestRejected(lookup.RevokedAt is not null ? "revoked" : "expired");
            }
        }

        return Result<GuestSessionIssued, AuthFailure>.Success(await CreateAsync(ct));
    }

    /// <summary>Verifies a guest cookie for a request. Returns a typed failure for anything but a live, unbanned session.</summary>
    public async Task<Result<GuestIdentity, AuthFailure>> AuthenticateAsync(string? cookieValue, CancellationToken ct)
    {
        if (!GuestCookie.TryParse(cookieValue, keys, out var parsed))
        {
            return Result<GuestIdentity, AuthFailure>.Failure(AuthFailure.SessionInactive);
        }

        var lookup = await LoadAsync(parsed.SessionId, ct);
        var now = time.GetUtcNow();
        if (lookup is null || !SecretTokens.HashMatches(lookup.SecretHash, parsed.Secret))
        {
            return Result<GuestIdentity, AuthFailure>.Failure(AuthFailure.SessionInactive);
        }

        if (lookup.BannedAt is not null)
        {
            return Result<GuestIdentity, AuthFailure>.Failure(AuthFailure.GuestBanned);
        }

        if (lookup.RevokedAt is not null || lookup.ExpiresAt <= now)
        {
            return Result<GuestIdentity, AuthFailure>.Failure(AuthFailure.SessionInactive);
        }

        if (now - lookup.LastSeenAt > TouchInterval)
        {
            await TouchAsync(parsed.SessionId, lookup.GuestId, now, now + Options.GuestSessionLifetime, ct);
        }

        return Result<GuestIdentity, AuthFailure>.Success(new GuestIdentity(lookup.GuestId, parsed.SessionId));
    }

    /// <summary>
    /// Re-checks a session that was already authenticated with its cookie (for example an open hub connection): true only
    /// while it exists for this guest, is not revoked or expired, and the guest is not banned. Read-only; never extends it.
    /// </summary>
    public async Task<bool> IsSessionLiveAsync(Guid sessionId, Guid guestId, CancellationToken ct)
    {
        var lookup = await LoadAsync(sessionId, ct);
        return lookup is not null
            && lookup.GuestId == guestId
            && lookup.BannedAt is null
            && lookup.RevokedAt is null
            && lookup.ExpiresAt > time.GetUtcNow();
    }

    /// <summary>Signs out: revokes the session named by a valid cookie. Idempotent.</summary>
    public async Task SignOutAsync(string? cookieValue, CancellationToken ct)
    {
        if (GuestCookie.TryParse(cookieValue, keys, out var parsed)
            && await LoadAsync(parsed.SessionId, ct) is { } lookup
            && SecretTokens.HashMatches(lookup.SecretHash, parsed.Secret))
        {
            await RevokeSessionAsync(parsed.SessionId, "signed_out", ct);
        }
    }

    public async Task<bool> RevokeSessionAsync(Guid sessionId, string reason, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE marrow_identity.guest_sessions SET revoked_at = @now, revoke_reason = @reason WHERE id = @id AND revoked_at IS NULL");
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = time.GetUtcNow().UtcDateTime });
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("id", sessionId);
        var changed = await command.ExecuteNonQueryAsync(ct) == 1;
        if (changed)
        {
            audit.GuestSessionRevoked(sessionId, reason);
        }

        return changed;
    }

    /// <summary>Bans the guest principal and revokes all of its sessions in one transaction.</summary>
    public async Task<bool> BanAsync(Guid guestId, string reason, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var ban = new NpgsqlCommand(
            "UPDATE marrow_identity.guest_principals SET banned_at = @now, ban_reason = @reason WHERE id = @id AND banned_at IS NULL", connection, tx))
        {
            ban.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
            ban.Parameters.AddWithValue("reason", reason);
            ban.Parameters.AddWithValue("id", guestId);
            if (await ban.ExecuteNonQueryAsync(ct) != 1)
            {
                return false;
            }
        }

        await using (var revoke = new NpgsqlCommand(
            "UPDATE marrow_identity.guest_sessions SET revoked_at = @now, revoke_reason = 'guest_banned' WHERE guest_id = @id AND revoked_at IS NULL", connection, tx))
        {
            revoke.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
            revoke.Parameters.AddWithValue("id", guestId);
            await revoke.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        audit.GuestBanned(guestId);
        return true;
    }

    private async Task<GuestSessionIssued> CreateAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var expires = now + Options.GuestSessionLifetime;
        var guestId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var secret = SecretTokens.NewSecret();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var principal = new NpgsqlCommand(
            "INSERT INTO marrow_identity.guest_principals (id, created_at, last_seen_at) VALUES (@id, @now, @now)", connection, tx))
        {
            principal.Parameters.AddWithValue("id", guestId);
            principal.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            await principal.ExecuteNonQueryAsync(ct);
        }

        await using (var session = new NpgsqlCommand(
            "INSERT INTO marrow_identity.guest_sessions (id, guest_id, secret_hash, created_at, last_seen_at, expires_at) VALUES (@id, @gid, @hash, @now, @now, @exp)",
            connection, tx))
        {
            session.Parameters.AddWithValue("id", sessionId);
            session.Parameters.AddWithValue("gid", guestId);
            session.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = SecretTokens.Hash(secret) });
            session.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            session.Parameters.Add(new NpgsqlParameter("exp", NpgsqlDbType.TimestampTz) { Value = expires.UtcDateTime });
            await session.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        audit.GuestCreated(guestId, sessionId);
        return new GuestSessionIssued(new GuestIdentity(guestId, sessionId), GuestCookie.Format(sessionId, secret, keys), expires, Resumed: false);
    }

    private async Task TouchAsync(Guid sessionId, Guid guestId, DateTimeOffset now, DateTimeOffset expires, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var session = new NpgsqlCommand(
            "UPDATE marrow_identity.guest_sessions SET last_seen_at = @now, expires_at = @exp WHERE id = @id AND revoked_at IS NULL", connection, tx))
        {
            session.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            session.Parameters.Add(new NpgsqlParameter("exp", NpgsqlDbType.TimestampTz) { Value = expires.UtcDateTime });
            session.Parameters.AddWithValue("id", sessionId);
            await session.ExecuteNonQueryAsync(ct);
        }

        await using (var principal = new NpgsqlCommand(
            "UPDATE marrow_identity.guest_principals SET last_seen_at = @now WHERE id = @id", connection, tx))
        {
            principal.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            principal.Parameters.AddWithValue("id", guestId);
            await principal.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private async Task<SessionRow?> LoadAsync(Guid sessionId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT s.guest_id, s.secret_hash, s.last_seen_at, s.expires_at, s.revoked_at, p.banned_at " +
            "FROM marrow_identity.guest_sessions s JOIN marrow_identity.guest_principals p ON p.id = s.guest_id WHERE s.id = @id");
        command.Parameters.AddWithValue("id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new SessionRow(
            reader.GetGuid(0),
            (byte[])reader.GetValue(1),
            Utc(reader.GetDateTime(2)),
            Utc(reader.GetDateTime(3)),
            reader.IsDBNull(4) ? null : Utc(reader.GetDateTime(4)),
            reader.IsDBNull(5) ? null : Utc(reader.GetDateTime(5)));
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record SessionRow(
        Guid GuestId, byte[] SecretHash, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? BannedAt);
}

/// <summary>Signed cookie value <c>{sessionId:N}.{secret}.{mac}</c>.</summary>
internal readonly record struct GuestCookie(Guid SessionId, string Secret)
{
    public static string Format(Guid sessionId, string secret, IdentityKeyMaterial keys)
    {
        var body = $"{sessionId:N}.{secret}";
        return body + "." + SecretTokens.Base64Url(keys.GuestCookieMac("guest|" + body));
    }

    /// <summary>Parses and verifies the MAC in constant time. A forged or edited value never reaches the database.</summary>
    public static bool TryParse(string? value, IdentityKeyMaterial keys, out GuestCookie parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(value) || value.Length > 256)
        {
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var sessionId) || parts[1].Length is < 16 or > 128)
        {
            return false;
        }

        var expected = SecretTokens.Base64Url(keys.GuestCookieMac($"guest|{parts[0]}.{parts[1]}"));
        if (!SecretTokens.FixedTimeEquals(expected, parts[2]))
        {
            return false;
        }

        parsed = new GuestCookie(sessionId, parts[1]);
        return true;
    }
}
