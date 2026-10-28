using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NKeeper.Api.Auditing;
using NKeeper.Sdk.Auditing;
using NKeeper.Api.Configuration;
using NKeeper.Infrastructure.Email;
using NKeeper.Infrastructure.Identity;
using NKeeper.Sdk.Security;

namespace NKeeper.Api.Security;

/// <summary>
/// Single sign-on with an OpenID Connect provider (authorization code flow with PKCE). The
/// provider's answer never becomes a session by itself: <see cref="SingleSignOnEvents"/> maps it to
/// an NKeeper account and signs that in, exactly like a password sign-in.
/// </summary>
internal static class SingleSignOn
{
    public const string Scheme = "SingleSignOn";
    public const string CallbackPath = "/api/auth/sso/callback";
    public const string ReturnUrlKey = "nkeeper.returnUrl";
    public const string RememberMeKey = "nkeeper.rememberMe";

    /// <summary>Back to the sign-in page, which explains the code.</summary>
    public const string FailedRedirect = "/login?sso=failed";
    public const string RefusedRedirect = "/login?sso=refused";
    public const string EmailFailedRedirect = "/login?sso=email";
    public const string TwoFactorRedirect = "/login?twoFactor=1";

    private static readonly string[] StandardScopes = ["openid", "profile", "email"];

    /// <summary>Registers the provider when SingleSignOn:Enabled is set; the options themselves are validated at startup.</summary>
    public static IServiceCollection AddNKeeperSingleSignOn(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>($"{SingleSignOnOptions.SectionName}:{nameof(SingleSignOnOptions.Enabled)}"))
        {
            return services;
        }

        services.AddScoped<SingleSignOnEvents>();
        services.AddAuthentication().AddOpenIdConnect(Scheme, _ => { });
        services.AddOptions<OpenIdConnectOptions>(Scheme)
            .Configure<IOptions<SingleSignOnOptions>>((options, sso) =>
            {
                var settings = sso.Value;
                options.Authority = settings.Authority;
                options.ClientId = settings.ClientId;
                options.ClientSecret = string.IsNullOrEmpty(settings.ClientSecret) ? null : settings.ClientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.CallbackPath = CallbackPath;
                options.SignedOutCallbackPath = "/api/auth/sso/signed-out";
                options.RemoteSignOutPath = "/api/auth/sso/signout";
                options.Scope.Clear();
                foreach (var scope in StandardScopes.Concat(settings.Scopes).Distinct(StringComparer.Ordinal))
                {
                    options.Scope.Add(scope);
                }

                // Claims keep the provider's names (sub, oid, groups), as the settings spell them.
                options.MapInboundClaims = false;
                options.SaveTokens = false;
                // Unused: the events sign in with the application cookie and end the request themselves.
                options.SignInScheme = IdentityConstants.ExternalScheme;
                options.EventsType = typeof(SingleSignOnEvents);
            });
        return services;
    }

    /// <summary>Only in-app paths, never another site (//evil.example, /\evil.example).</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } url && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) ? url : "/";
}

internal sealed class SingleSignOnEvents(
    SingleSignOnAccounts accounts,
    SignInManager<ApplicationUser> signInManager,
    TwoFactorSignIn twoFactor,
    IOptions<SingleSignOnOptions> options,
    IOptions<AuthenticationSettings> authenticationSettings,
    AuditLog audit,
    TimeProvider timeProvider,
    ILogger<SingleSignOnEvents> logger) : OpenIdConnectEvents
{
    public override async Task TicketReceived(TicketReceivedContext context)
    {
        var settings = options.Value;
        var principal = context.Principal!;
        string? Claim(string type) => principal.FindFirst(type)?.Value is { Length: > 0 } value ? value.Trim() : null;

        var subject = Claim(settings.SubjectClaim);
        if (subject is null)
        {
            logger.LogWarning("Single sign-on failed: the identity provider's token has no {Claim} claim.", settings.SubjectClaim);
            Finish(context, SingleSignOn.FailedRedirect);
            return;
        }

        var identity = new SingleSignOnIdentity(
            subject,
            Claim(settings.UserNameClaim),
            Claim(settings.EmailClaim),
            [.. principal.FindAll(settings.GroupsClaim).Select(c => c.Value)]);
        var result = await accounts.SignInAsync(identity, context.HttpContext.RequestAborted);
        if (result.User is not { } user)
        {
            var attempted = identity.UserName ?? identity.Email ?? subject;
            await audit.RecordAsync(new AuditEntry(
                AuditActions.SignInFailed, Detail: $"Single sign-on: {result.Refusal}", UserName: attempted.Length > 256 ? attempted[..256] : attempted));
            Finish(context, SingleSignOn.RefusedRedirect);
            return;
        }

        var items = context.Properties?.Items;
        var rememberMe = items?.TryGetValue(SingleSignOn.RememberMeKey, out var remember) == true && remember == "true";
        if (user.TwoFactorEnabled)
        {
            // The sign-in page picks up the pending sign-in and asks for the emailed code.
            try
            {
                await twoFactor.StartAsync(context.HttpContext, user, rememberMe, "Single sign-on", null, context.HttpContext.RequestAborted);
                Finish(context, SingleSignOn.TwoFactorRedirect);
            }
            catch (EmailException)
            {
                await audit.RecordAsync(new AuditEntry(
                    AuditActions.SignInFailed, Detail: "Single sign-on: the sign-in code couldn't be emailed", UserId: user.Id, UserName: user.UserName));
                Finish(context, SingleSignOn.EmailFailedRedirect);
            }

            return;
        }

        var auth = authenticationSettings.Value;
        await signInManager.SignInAsync(user, new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc = timeProvider.GetUtcNow().Add(rememberMe ? auth.RememberMeDuration : auth.SessionTimeout),
            AllowRefresh = true,
        });
        await audit.RecordAsync(new AuditEntry(AuditActions.SignIn, Detail: "Single sign-on", UserId: user.Id, UserName: user.UserName));
        Finish(context, SingleSignOn.SafeReturnUrl(items?.TryGetValue(SingleSignOn.ReturnUrlKey, out var returnUrl) == true ? returnUrl : null));
    }

    /// <summary>The user cancelled, the provider refused, or the answer didn't check out (state, nonce, signature).</summary>
    public override Task RemoteFailure(RemoteFailureContext context)
    {
        logger.LogWarning(context.Failure, "Single sign-on failed.");
        context.Response.Redirect(SingleSignOn.FailedRedirect);
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private static void Finish(TicketReceivedContext context, string redirect)
    {
        context.Response.Redirect(redirect);
        context.HandleResponse();
    }
}
