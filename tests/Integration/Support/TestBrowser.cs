using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Integration.Tests.Support;

public sealed record CookieSeen(string Name, string Value, bool HttpOnly, bool Secure, string? SameSite, string? Path, bool Deleted);

public sealed record ApiResponse(HttpStatusCode Status, string Body, IReadOnlyList<CookieSeen> SetCookies)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string[] Roles => [.. Json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!)];

    public string Code => Json.GetProperty("code").GetString()!;

    public string Str(string property) => Json.GetProperty(property).GetString()!;
}

/// <summary>
/// A hand-rolled cookie jar and request builder so tests control exactly which cookies, headers and origin a "browser" sends.
/// It honors the Secure attribute only by virtue of the https base address; deletion is a past <c>Expires</c>/<c>Max-Age=0</c>.
/// </summary>
public sealed class TestBrowser(HttpClient client, string origin)
{
    public Dictionary<string, string> Cookies { get; } = [];
    public Dictionary<string, CookieSeen> LastSeen { get; } = [];

    public async Task<ApiResponse> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string? bearer = null,
        bool sendOrigin = true,
        string? originOverride = null,
        string? csrfCookie = null,
        string? csrfHeader = null,
        bool useCookies = true,
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (sendOrigin)
        {
            request.Headers.TryAddWithoutValidation("Origin", originOverride ?? origin);
        }

        if (useCookies && Cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        if (csrfHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrfHeader);
        }
        else if (csrfCookie is not null && Cookies.TryGetValue(csrfCookie, out var token))
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-Token", token);
        }

        foreach (var (name, value) in headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(request);
        var seen = new List<CookieSeen>();
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var raw in values)
            {
                var cookie = Parse(raw);
                seen.Add(cookie);
                LastSeen[cookie.Name] = cookie;
                if (cookie.Deleted)
                {
                    Cookies.Remove(cookie.Name);
                }
                else
                {
                    Cookies[cookie.Name] = cookie.Value;
                }
            }
        }

        return new ApiResponse(response.StatusCode, await response.Content.ReadAsStringAsync(), seen);
    }

    public Task<ApiResponse> PostAsync(string path, object? body = null, string? bearer = null, string? csrfCookie = null, string? csrfHeader = null, bool sendOrigin = true, string? originOverride = null) =>
        SendAsync(HttpMethod.Post, path, body ?? new { }, bearer, sendOrigin, originOverride, csrfCookie, csrfHeader);

    public Task<ApiResponse> GetAsync(string path, string? bearer = null, bool useCookies = true, IEnumerable<KeyValuePair<string, string>>? headers = null) =>
        SendAsync(HttpMethod.Get, path, null, bearer, sendOrigin: false, useCookies: useCookies, headers: headers);

    private static CookieSeen Parse(string raw)
    {
        var parts = raw.Split(';', StringSplitOptions.TrimEntries);
        var eq = parts[0].IndexOf('=', StringComparison.Ordinal);
        var name = parts[0][..eq];
        var value = parts[0][(eq + 1)..];
        var attributes = parts.Skip(1).ToArray();
        string? Attr(string key) => attributes.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..];
        var expires = Attr("expires");
        var deleted = value.Length == 0
            || Attr("max-age") == "0"
            || (expires is not null && DateTimeOffset.TryParse(expires, out var at) && at < DateTimeOffset.UtcNow.AddYears(-1));
        return new CookieSeen(
            name, value,
            attributes.Contains("httponly", StringComparer.OrdinalIgnoreCase),
            attributes.Contains("secure", StringComparer.OrdinalIgnoreCase),
            Attr("samesite"), Attr("path"), deleted);
    }
}

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 30 s, 6 digits), implemented independently of the code under test.</summary>
public static class Totp
{
    public static string Code(string base32Key, DateTimeOffset? at = null)
    {
        var key = Base32Decode(base32Key);
        var step = (ulong)((at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30);
        var counter = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return [.. bytes];
    }
}
