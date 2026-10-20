using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// A stand-in OpenID Connect provider: it answers the API's token request with an id_token it signs
/// for whoever <see cref="SignInAs"/> named, and the test plays the browser between the two.
/// </summary>
public sealed class FakeIdentityProvider : HttpMessageHandler
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "nkeeper-test";

    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "test-key" };
    private readonly Dictionary<string, (Dictionary<string, object> Claims, string Nonce)> _codes = [];
    private Dictionary<string, object> _next = [];

    public static Dictionary<string, string> Settings(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string>
        {
            ["SingleSignOn:Enabled"] = "true",
            ["SingleSignOn:DisplayName"] = "Contoso",
            ["SingleSignOn:Authority"] = Issuer,
            ["SingleSignOn:ClientId"] = ClientId,
            ["SingleSignOn:ClientSecret"] = "test-secret",
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>Points the API's handler at this provider instead of fetching a discovery document.</summary>
    public void Configure(IServiceCollection services) =>
        services.PostConfigure<OpenIdConnectOptions>("SingleSignOn", options =>
        {
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = Issuer,
                AuthorizationEndpoint = $"{Issuer}/authorize",
                TokenEndpoint = $"{Issuer}/token",
            };
            configuration.SigningKeys.Add(_key);
            options.Configuration = configuration;
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            options.Backchannel = new HttpClient(this);
        });

    /// <summary>The claims of the next sign-in.</summary>
    public FakeIdentityProvider SignInAs(string subject, string? userName = null, string? email = null, params string[] groups)
    {
        _next = new Dictionary<string, object> { ["sub"] = subject };
        if (userName is not null)
        {
            _next["preferred_username"] = userName;
        }

        if (email is not null)
        {
            _next["email"] = email;
        }

        if (groups.Length > 0)
        {
            _next["groups"] = groups;
        }

        return this;
    }

    /// <summary>
    /// What the provider's login page does: takes the authorization request the API redirected to and
    /// answers with the form the browser posts back to the callback.
    /// </summary>
    public FormUrlEncodedContent Authorize(Uri authorizationRequest)
    {
        Assert.StartsWith($"{Issuer}/authorize", authorizationRequest.ToString(), StringComparison.Ordinal);
        var query = HttpUtility.ParseQueryString(authorizationRequest.Query);
        Assert.Equal(ClientId, query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);

        var code = Guid.NewGuid().ToString("N");
        _codes[code] = (_next, query["nonce"]!);
        return new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["state"] = query["state"]! });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal($"{Issuer}/token", request.RequestUri!.ToString());
        var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
        Assert.NotNull(form["code_verifier"]);
        if (!_codes.Remove(form["code"]!, out var issued))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json") };
        }

        var accessToken = Guid.NewGuid().ToString("N");
        var claims = new Dictionary<string, object>(issued.Claims)
        {
            ["nonce"] = issued.Nonce,
            ["at_hash"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken))[..16]),
        };
        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            Claims = claims,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
        var body = $$"""{"id_token":"{{idToken}}","access_token":"{{accessToken}}","token_type":"Bearer","expires_in":300}""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _key.Rsa.Dispose();
        }

        base.Dispose(disposing);
    }
}
