using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Club.Server.Auth;

public sealed class AuthOptions
{
    /// <summary>Ключ клуба для <c>POST /agents/register</c> (<c>X-Club-Key</c>, <c>agent.json → server.clubApiKey</c>).</summary>
    public string ClubApiKey { get; set; } = "";

    /// <summary>Идентификатор клуба в claim <c>club</c>.</summary>
    public string ClubId { get; set; } = "club";

    /// <summary>PEM с RSA-ключом подписи JWT; создаётся при первом запуске, если файла нет.</summary>
    public string SigningKeyPath { get; set; } = "data/jwt-signing-key.pem";

    public string Issuer { get; set; } = "club-server";

    public int AccessTokenMinutes { get; set; } = 60;

    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>Окно подписи запросов, секунды (контракт: ±300).</summary>
    public int SignatureWindowSec { get; set; } = 300;

    /// <summary>Новый ПК без одобрения администратора получает pcId сразу (для стенда). В клубе — false.</summary>
    public bool AutoApprovePcs { get; set; }
}

/// <summary>Claims токена агента, нужные серверу.</summary>
public sealed record AgentPrincipal(Guid PcId, string Hwid, int CredentialsVersion, DateTimeOffset ExpiresAt);

/// <summary>Выпуск и проверка JWT агента (RS256). Ключ хранится в PEM-файле и переживает перезапуск.</summary>
public sealed class TokenService
{
    private const string Audience = "club-agent";
    private readonly AuthOptions _options;
    private readonly TimeProvider _clock;
    private readonly RsaSecurityKey _key;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public TokenService(AuthOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
        _key = LoadOrCreateKey(options.SigningKeyPath);
    }

    public (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(Guid pcId, string hwid, int credentialsVersion)
    {
        var now = _clock.GetUtcNow();
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = pcId.ToString(),
                ["club"] = _options.ClubId,
                ["hwid"] = hwid,
                ["role"] = "agent",
                ["cv"] = credentialsVersion,
                ["jti"] = Guid.NewGuid().ToString(),
            },
        });
        return (token, expires);
    }

    /// <summary>Проверяет токен; <c>null</c> и причина (<c>expired</c> или <c>invalid</c>) при отказе.</summary>
    public async Task<(AgentPrincipal? Principal, string? Reason)> ValidateAsync(string token)
    {
        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = _options.Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = _key,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = _clock.GetUtcNow().UtcDateTime;
                return (notBefore is null || notBefore <= now.AddSeconds(30)) && expires > now;
            },
        });

        if (!result.IsValid)
        {
            return (null, result.Exception is SecurityTokenInvalidLifetimeException or SecurityTokenExpiredException ? "expired" : "invalid");
        }

        var claims = result.ClaimsIdentity;
        if (claims.FindFirst("role")?.Value != "agent"
            || !Guid.TryParse(claims.FindFirst("sub")?.Value, out var pcId)
            || !int.TryParse(claims.FindFirst("cv")?.Value, out var cv)
            || result.SecurityToken is not JsonWebToken jwt)
        {
            return (null, "invalid");
        }

        return (new AgentPrincipal(pcId, claims.FindFirst("hwid")?.Value ?? "", cv, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero)), null);
    }

    private static RsaSecurityKey LoadOrCreateKey(string path)
    {
        var rsa = RSA.Create();
        if (File.Exists(path))
        {
            rsa.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            rsa.KeySize = 3072;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var pem = rsa.ExportPkcs8PrivateKeyPem();
            File.WriteAllText(path, pem);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        var kid = Convert.ToHexStringLower(SHA256.HashData(parameters.Modulus!))[..16];
        return new RsaSecurityKey(rsa) { KeyId = kid };
    }
}

public static class Secrets
{
    public static byte[] Random(int bytes) => RandomNumberGenerator.GetBytes(bytes);

    public static string NewRefreshToken() => Base64UrlEncoder.Encode(Random(32));

    public static byte[] HashToken(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    /// <summary>Сравнение секретов без утечки длины и содержимого по времени.</summary>
    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(HashToken(a), HashToken(b));
}
