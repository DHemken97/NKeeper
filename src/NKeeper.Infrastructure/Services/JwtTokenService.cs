using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NKeeper.Core.Entities;
using NKeeper.Core.Interfaces;
using NKeeper.Core.Security;
using NKeeper.Infrastructure.Options;

namespace NKeeper.Infrastructure.Services;

public sealed record IssuedToken(string Token, DateTime ExpiresAtUtc);

public sealed record MfaPendingSession(Guid UserId, AuthDriverType Driver);

/// <summary>
/// Issues and validates the two token kinds used by the login flow:
/// short-lived <c>mfa_pending</c> tokens (MFA audience) and full session tokens (API audience).
/// </summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    public const string TokenUseClaim = "token_use";
    public const string TokenUseSession = "session";
    public const string TokenUseMfaPending = "mfa_pending";
    public const string AuthSourceClaim = "auth_source";
    public const string RoleClaim = "role";
    public const string NameClaim = "name";
    public const string AdminRole = NKeeperRoles.Admin;

    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public static SymmetricSecurityKey CreateSigningKey(JwtOptions jwt) =>
        new(Encoding.UTF8.GetBytes(jwt.SigningKey));

    /// <summary>Validation parameters for session tokens, shared with the JwtBearer handler.</summary>
    public static TokenValidationParameters CreateSessionValidationParameters(JwtOptions jwt) => new()
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = CreateSigningKey(jwt),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        NameClaimType = NameClaim,
        RoleClaimType = RoleClaim,
        ClockSkew = TimeSpan.FromSeconds(30)
    };

    public IssuedToken CreateSessionToken(User user, IEnumerable<string> authenticationMethods)
    {
        var jwt = options.Value;
        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
            [NameClaim] = user.Username,
            [TokenUseClaim] = TokenUseSession,
            [AuthSourceClaim] = user.AuthSource.ToString(),
            [JwtRegisteredClaimNames.Amr] = authenticationMethods.ToArray()
        };

        if (!string.IsNullOrEmpty(user.DisplayName))
        {
            claims["display_name"] = user.DisplayName;
        }

        if (user.IsAdmin)
        {
            claims[RoleClaim] = new[] { AdminRole };
        }

        return Create(jwt.Audience, TimeSpan.FromMinutes(jwt.SessionLifetimeMinutes), claims);
    }

    public IssuedToken CreateMfaPendingToken(User user, AuthDriverType driver)
    {
        var jwt = options.Value;
        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
            [TokenUseClaim] = TokenUseMfaPending,
            [AuthSourceClaim] = driver.ToString()
        };

        return Create(jwt.MfaAudience, TimeSpan.FromMinutes(jwt.MfaPendingLifetimeMinutes), claims);
    }

    /// <summary>Returns the pending session, or null if the token is invalid, expired, or not an mfa_pending token.</summary>
    public async Task<MfaPendingSession?> ValidateMfaPendingTokenAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var jwt = options.Value;
        var parameters = CreateSessionValidationParameters(jwt);
        parameters.ValidAudience = jwt.MfaAudience;

        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid)
        {
            return null;
        }

        var identity = result.ClaimsIdentity;
        if (identity.FindFirst(TokenUseClaim)?.Value != TokenUseMfaPending ||
            !Guid.TryParse(identity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId) ||
            !Enum.TryParse<AuthDriverType>(identity.FindFirst(AuthSourceClaim)?.Value, out var driver))
        {
            return null;
        }

        return new MfaPendingSession(userId, driver);
    }

    public static Guid? GetUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var id) ? id : null;

    private IssuedToken Create(string audience, TimeSpan lifetime, Dictionary<string, object> claims)
    {
        var jwt = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expires = now.Add(lifetime);

        claims[JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N");

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(CreateSigningKey(jwt), SecurityAlgorithms.HmacSha256)
        };

        return new IssuedToken(_handler.CreateToken(descriptor), expires);
    }
}
