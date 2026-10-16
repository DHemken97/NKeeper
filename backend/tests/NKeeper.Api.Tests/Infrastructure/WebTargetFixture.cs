using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

[assembly: AssemblyFixture(typeof(NKeeper.Api.Tests.Infrastructure.WebTargetFixture))]

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// A small web application on 127.0.0.1, over HTTP and over HTTPS with a self-signed certificate,
/// standing in for a device's web interface behind NKeeper.
/// </summary>
public sealed class WebTargetFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public int HttpPort { get; private set; }

    public int HttpsPort { get; private set; }

    /// <summary>SHA-256 of the HTTPS certificate, as colon-separated upper-case hex.</summary>
    public string CertificateFingerprint { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        using var certificate = CreateSelfSignedCertificate();
        CertificateFingerprint = string.Join(':', Convert.ToHexString(SHA256.HashData(certificate.RawData)).Chunk(2).Select(c => new string(c)));
        var serverCertificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0);
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(serverCertificate));
        });
        _app = builder.Build();
        _app.MapGet("/", () => "hello from the target");
        _app.MapGet("/headers", (HttpRequest request) => new
        {
            host = request.Host.Value,
            cookie = request.Headers.Cookie.ToString(),
            origin = request.Headers.Origin.ToString(),
            referer = request.Headers.Referer.ToString(),
            scheme = request.Scheme,
        });
        _app.MapGet("/login", (HttpContext context) =>
        {
            context.Response.Headers.Append("Set-Cookie", "app_session=abc; Domain=127.0.0.1; Path=/; HttpOnly");
            context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
            return Results.Redirect($"{context.Request.Scheme}://{context.Request.Host}/home?welcome=1");
        });
        _app.MapGet("/home", () => "home page");
        _app.MapGet("/framed", (HttpContext context) =>
        {
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; frame-ancestors 'none'; img-src *";
            return "no framing please";
        });
        _app.MapPost("/upload", async (HttpRequest request) => $"received {(await new StreamReader(request.Body).ReadToEndAsync()).Length} chars");
        await _app.StartAsync();

        var addresses = _app.Urls.Select(u => new Uri(u)).ToList();
        HttpPort = addresses.First(u => u.Scheme == "http").Port;
        HttpsPort = addresses.First(u => u.Scheme == "https").Port;
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=nkeeper-web-target", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
