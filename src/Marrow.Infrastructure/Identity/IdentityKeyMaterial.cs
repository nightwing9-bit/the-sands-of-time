using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Marrow.Infrastructure.Identity;

/// <summary>
/// Per-purpose keys derived from the single configured master secret (HKDF-SHA256, distinct <c>info</c> per purpose).
/// Rotating the master secret invalidates every access token, guest cookie and stored TOTP key; that is documented
/// behavior, not a silent fallback.
/// </summary>
public sealed class IdentityKeyMaterial
{
    private const int MinimumSecretLength = 32;
    private const int MinimumDistinctCharacters = 8;
    private static readonly string[] PlaceholderMarkers = ["REPLACE_ME", "CHANGEME", "CHANGE-ME", "CHANGE_ME", "DEFAULT", "PASSWORD", "SECRET-KEY", "YOUR-SECRET"];

    private readonly byte[] _jwt;
    private readonly byte[] _guestMac;
    private readonly byte[] _csrfMac;
    private readonly byte[] _totpProtection;

    private IdentityKeyMaterial(byte[] master, bool ephemeral)
    {
        _jwt = Derive(master, "marrow.identity.access-jwt.v1");
        _guestMac = Derive(master, "marrow.identity.guest-cookie.v1");
        _csrfMac = Derive(master, "marrow.identity.csrf.v1");
        _totpProtection = Derive(master, "marrow.identity.totp-at-rest.v1");
        IsEphemeral = ephemeral;
    }

    /// <summary>True when the key was generated for this process only (Development without a configured secret).</summary>
    public bool IsEphemeral { get; }

    /// <summary>
    /// Builds the key material. Outside Development a missing, short, low-variety or placeholder secret throws;
    /// the message never echoes the value.
    /// </summary>
    public static IdentityKeyMaterial Create(string? configuredSecret, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(configuredSecret))
        {
            if (isDevelopment)
            {
                return new IdentityKeyMaterial(RandomNumberGenerator.GetBytes(32), ephemeral: true);
            }

            throw new InvalidOperationException(
                "Configuration 'Identity:MasterSecret' is required outside Development (environment variable 'Identity__MasterSecret'); there is no default.");
        }

        var secret = configuredSecret.Trim();
        var upper = secret.ToUpperInvariant();
        if (PlaceholderMarkers.Any(upper.Contains))
        {
            throw new InvalidOperationException("Configuration 'Identity:MasterSecret' is a placeholder or default value and must be replaced.");
        }

        if (secret.Length < MinimumSecretLength || secret.Distinct().Count() < MinimumDistinctCharacters)
        {
            throw new InvalidOperationException(
                $"Configuration 'Identity:MasterSecret' must be at least {MinimumSecretLength} characters with enough variety (for example `openssl rand -hex 32`).");
        }

        return new IdentityKeyMaterial(Encoding.UTF8.GetBytes(secret), ephemeral: false);
    }

    public SymmetricSecurityKey AccessTokenSigningKey() => new(_jwt) { KeyId = "marrow-access-v1" };

    public byte[] GuestCookieMac(string data) => HMACSHA256.HashData(_guestMac, Encoding.UTF8.GetBytes(data));

    public byte[] CsrfMac(string data) => HMACSHA256.HashData(_csrfMac, Encoding.UTF8.GetBytes(data));

    /// <summary>AES-256-GCM; layout nonce(12) | tag(16) | ciphertext. <paramref name="context"/> binds the blob to its owner row.</summary>
    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> context)
    {
        var output = new byte[12 + 16 + plaintext.Length];
        var nonce = output.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_totpProtection, 16);
        aes.Encrypt(nonce, plaintext, output.AsSpan(28), output.AsSpan(12, 16), context);
        return output;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> context)
    {
        if (blob.Length < 28)
        {
            throw new CryptographicException("Protected value is truncated.");
        }

        var plaintext = new byte[blob.Length - 28];
        using var aes = new AesGcm(_totpProtection, 16);
        aes.Decrypt(blob[..12], blob[28..], blob.Slice(12, 16), plaintext, context);
        return plaintext;
    }

    private static byte[] Derive(byte[] master, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: null, info: Encoding.UTF8.GetBytes(info));
}

/// <summary>Opaque secret generation and comparison helpers.</summary>
internal static class SecretTokens
{
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    public static bool HashMatches(byte[] stored, string presentedSecret) =>
        CryptographicOperations.FixedTimeEquals(stored, Hash(presentedSecret));

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string Base64Url(ReadOnlySpan<byte> bytes) => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(bytes);
}
