using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Marrow.Infrastructure.Identity.Staff;

/// <summary>A staff account. Guests are a different table and a different type; they are never staff users.</summary>
public sealed class StaffUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserName { get; set; } = "";
    public string NormalizedUserName { get; set; } = "";
    public string? PasswordHash { get; set; }
    public string? SecurityStamp { get; set; }
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");
    public bool LockoutEnabled { get; set; } = true;
    public DateTimeOffset? LockoutEnd { get; set; }
    public int AccessFailedCount { get; set; }
    public bool TwoFactorEnabled { get; set; }

    /// <summary>Base32 TOTP shared key in memory only. Persisted encrypted (AES-GCM).</summary>
    public string? AuthenticatorKey { get; set; }

    public DateTimeOffset? DisabledAt { get; set; }
}

/// <summary>
/// ASP.NET Core Identity store over the <c>marrow_identity</c> schema using Npgsql directly (the repository's
/// persistence convention) instead of an EF Core model. Hashing, lockout and TOTP verification are
/// <see cref="UserManager{TUser}"/> behavior; this class only persists.
/// </summary>
public sealed class PgStaffUserStore(NpgsqlDataSource dataSource, IdentityKeyMaterial keys) :
    IUserStore<StaffUser>,
    IUserPasswordStore<StaffUser>,
    IUserSecurityStampStore<StaffUser>,
    IUserLockoutStore<StaffUser>,
    IUserTwoFactorStore<StaffUser>,
    IUserAuthenticatorKeyStore<StaffUser>,
    IUserRoleStore<StaffUser>
{
    private const string Columns =
        "id, user_name, normalized_user_name, password_hash, security_stamp, concurrency_stamp, lockout_enabled, " +
        "lockout_end, access_failed_count, two_factor_enabled, authenticator_key_enc, disabled_at";

    public void Dispose()
    {
    }

    public Task<string> GetUserIdAsync(StaffUser user, CancellationToken cancellationToken) =>
        Task.FromResult(user.Id.ToString("D", CultureInfo.InvariantCulture));

    public Task<string?> GetUserNameAsync(StaffUser user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.UserName);

    public Task SetUserNameAsync(StaffUser user, string? userName, CancellationToken cancellationToken)
    {
        user.UserName = userName ?? "";
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(StaffUser user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.NormalizedUserName);

    public Task SetNormalizedUserNameAsync(StaffUser user, string? normalizedName, CancellationToken cancellationToken)
    {
        user.NormalizedUserName = normalizedName ?? "";
        return Task.CompletedTask;
    }

    public async Task<IdentityResult> CreateAsync(StaffUser user, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"INSERT INTO marrow_identity.staff_users ({Columns}) VALUES (@id, @name, @nname, @hash, @stamp, @cstamp, @lockout, @lend, @fails, @tf, @key, @disabled)");
        Bind(command, user);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return IdentityResult.Success;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName", Description = "User name is already taken." });
        }
    }

    public async Task<IdentityResult> UpdateAsync(StaffUser user, CancellationToken cancellationToken)
    {
        var previousStamp = user.ConcurrencyStamp;
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        await using var command = dataSource.CreateCommand(
            "UPDATE marrow_identity.staff_users SET user_name=@name, normalized_user_name=@nname, password_hash=@hash, security_stamp=@stamp, " +
            "concurrency_stamp=@cstamp, lockout_enabled=@lockout, lockout_end=@lend, access_failed_count=@fails, two_factor_enabled=@tf, " +
            "authenticator_key_enc=@key, disabled_at=@disabled WHERE id=@id AND concurrency_stamp=@previous");
        Bind(command, user);
        command.Parameters.AddWithValue("previous", previousStamp);
        int rows;
        try
        {
            rows = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            user.ConcurrencyStamp = previousStamp;
            return IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName", Description = "User name is already taken." });
        }

        if (rows == 1)
        {
            return IdentityResult.Success;
        }

        user.ConcurrencyStamp = previousStamp;
        return IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure", Description = "The account was changed concurrently." });
    }

    public Task<IdentityResult> DeleteAsync(StaffUser user, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Staff accounts are disabled (DisabledAt), never deleted.");

    public Task<StaffUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        Guid.TryParse(userId, out var id) ? FindAsync("id = @v", new NpgsqlParameter("v", id), cancellationToken) : Task.FromResult<StaffUser?>(null);

    public Task<StaffUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        FindAsync("normalized_user_name = @v", new NpgsqlParameter("v", normalizedUserName), cancellationToken);

    public Task SetPasswordHashAsync(StaffUser user, string? passwordHash, CancellationToken cancellationToken)
    {
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(StaffUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash);

    public Task<bool> HasPasswordAsync(StaffUser user, CancellationToken cancellationToken) =>
        Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));

    public Task SetSecurityStampAsync(StaffUser user, string stamp, CancellationToken cancellationToken)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(StaffUser user, CancellationToken cancellationToken) => Task.FromResult(user.SecurityStamp);

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(StaffUser user, CancellationToken cancellationToken) => Task.FromResult(user.LockoutEnd);

    public Task SetLockoutEndDateAsync(StaffUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(StaffUser user, CancellationToken cancellationToken) =>
        Task.FromResult(++user.AccessFailedCount);

    public Task ResetAccessFailedCountAsync(StaffUser user, CancellationToken cancellationToken)
    {
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(StaffUser user, CancellationToken cancellationToken) => Task.FromResult(user.AccessFailedCount);

    public Task<bool> GetLockoutEnabledAsync(StaffUser user, CancellationToken cancellationToken) => Task.FromResult(user.LockoutEnabled);

    public Task SetLockoutEnabledAsync(StaffUser user, bool enabled, CancellationToken cancellationToken)
    {
        user.LockoutEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task SetTwoFactorEnabledAsync(StaffUser user, bool enabled, CancellationToken cancellationToken)
    {
        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> GetTwoFactorEnabledAsync(StaffUser user, CancellationToken cancellationToken) => Task.FromResult(user.TwoFactorEnabled);

    public Task SetAuthenticatorKeyAsync(StaffUser user, string key, CancellationToken cancellationToken)
    {
        user.AuthenticatorKey = key;
        return Task.CompletedTask;
    }

    public Task<string?> GetAuthenticatorKeyAsync(StaffUser user, CancellationToken cancellationToken) => Task.FromResult(user.AuthenticatorKey);

    public async Task AddToRoleAsync(StaffUser user, string roleName, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO marrow_identity.staff_user_roles (user_id, role) VALUES (@u, @r) ON CONFLICT DO NOTHING");
        command.Parameters.AddWithValue("u", user.Id);
        command.Parameters.AddWithValue("r", roleName.ToLowerInvariant());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveFromRoleAsync(StaffUser user, string roleName, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM marrow_identity.staff_user_roles WHERE user_id=@u AND role=@r");
        command.Parameters.AddWithValue("u", user.Id);
        command.Parameters.AddWithValue("r", roleName.ToLowerInvariant());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IList<string>> GetRolesAsync(StaffUser user, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT role FROM marrow_identity.staff_user_roles WHERE user_id=@u ORDER BY role");
        command.Parameters.AddWithValue("u", user.Id);
        var roles = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(reader.GetString(0));
        }

        return roles;
    }

    public async Task<bool> IsInRoleAsync(StaffUser user, string roleName, CancellationToken cancellationToken) =>
        (await GetRolesAsync(user, cancellationToken)).Contains(roleName.ToLowerInvariant());

    public async Task<IList<StaffUser>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {Columns} FROM marrow_identity.staff_users u WHERE id IN (SELECT user_id FROM marrow_identity.staff_user_roles WHERE role=@r)");
        command.Parameters.AddWithValue("r", roleName.ToLowerInvariant());
        var users = new List<StaffUser>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(Read(reader));
        }

        return users;
    }

    private async Task<StaffUser?> FindAsync(string where, NpgsqlParameter parameter, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"SELECT {Columns} FROM marrow_identity.staff_users WHERE {where}");
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private StaffUser Read(NpgsqlDataReader r)
    {
        var id = r.GetGuid(0);
        string? key = null;
        if (!r.IsDBNull(10))
        {
            key = Encoding.UTF8.GetString(keys.Unprotect((byte[])r.GetValue(10), id.ToByteArray()));
        }

        return new StaffUser
        {
            Id = id,
            UserName = r.GetString(1),
            NormalizedUserName = r.GetString(2),
            PasswordHash = r.IsDBNull(3) ? null : r.GetString(3),
            SecurityStamp = r.IsDBNull(4) ? null : r.GetString(4),
            ConcurrencyStamp = r.GetString(5),
            LockoutEnabled = r.GetBoolean(6),
            LockoutEnd = r.IsDBNull(7) ? null : new DateTimeOffset(r.GetDateTime(7), TimeSpan.Zero),
            AccessFailedCount = r.GetInt32(8),
            TwoFactorEnabled = r.GetBoolean(9),
            AuthenticatorKey = key,
            DisabledAt = r.IsDBNull(11) ? null : new DateTimeOffset(r.GetDateTime(11), TimeSpan.Zero),
        };
    }

    private void Bind(NpgsqlCommand command, StaffUser user)
    {
        var p = command.Parameters;
        p.AddWithValue("id", user.Id);
        p.AddWithValue("name", user.UserName);
        p.AddWithValue("nname", user.NormalizedUserName);
        p.Add(new NpgsqlParameter("hash", NpgsqlDbType.Text) { Value = (object?)user.PasswordHash ?? DBNull.Value });
        p.Add(new NpgsqlParameter("stamp", NpgsqlDbType.Text) { Value = (object?)user.SecurityStamp ?? DBNull.Value });
        p.AddWithValue("cstamp", user.ConcurrencyStamp);
        p.AddWithValue("lockout", user.LockoutEnabled);
        p.Add(new NpgsqlParameter("lend", NpgsqlDbType.TimestampTz) { Value = user.LockoutEnd is { } end ? end.UtcDateTime : DBNull.Value });
        p.AddWithValue("fails", user.AccessFailedCount);
        p.AddWithValue("tf", user.TwoFactorEnabled);
        p.Add(new NpgsqlParameter("key", NpgsqlDbType.Bytea)
        {
            Value = user.AuthenticatorKey is null
                ? DBNull.Value
                : keys.Protect(Encoding.UTF8.GetBytes(user.AuthenticatorKey), user.Id.ToByteArray()),
        });
        p.Add(new NpgsqlParameter("disabled", NpgsqlDbType.TimestampTz) { Value = user.DisabledAt is { } d ? d.UtcDateTime : DBNull.Value });
    }
}
