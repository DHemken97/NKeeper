using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// A fake Proxmox VE API on 127.0.0.1 over HTTPS with a self-signed certificate: sign-in (a
/// ticket for root@pam, two-factor for tfa@pve) or an API token, the cluster's guests, and VNC
/// proxies whose WebSocket greets with what it was opened for, then echoes like a VM's display.
/// Like Proxmox, writes need the CSRF token, and each proxy ticket works once.
/// </summary>
public sealed class ProxmoxApiFixture : IAsyncLifetime
{
    public const string Password = "Pve-Api-Passw0rd";
    public const string TokenId = "root@pam!nkeeper";
    public const string TokenSecret = "5f0b6c1e-token-secret";
    private const string Ticket = "PVE:root@pam:6720F00D::c2lnbmF0dXJl+/=";
    private const string Csrf = "6720F00D:csrf-token";

    /// <summary>Proxmox's property names are sent as declared (e.g. CSRFPreventionToken, NeedTFA).</summary>
    private static readonly System.Text.Json.JsonSerializerOptions ProxmoxJson = new();

    private WebApplication? _app;
    private int _proxies;

    public int Port { get; private set; }

    /// <summary>VNC proxy tickets issued and not used yet.</summary>
    public ConcurrentDictionary<string, string> OpenProxies { get; } = new();

    public async ValueTask InitializeAsync()
    {
        using var certificate = CreateSelfSignedCertificate();
        var serverCertificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(serverCertificate)));
        _app = builder.Build();
        _app.UseWebSockets();

        // Everything but signing in needs the ticket cookie (and CSRF token for writes) or an API token.
        _app.Use(async (context, next) =>
        {
            if (context.Request.Path != "/api2/json/access/ticket")
            {
                var token = context.Request.Headers.Authorization == $"PVEAPIToken={TokenId}={TokenSecret}";
                var cookie = context.Request.Headers.Cookie.ToString() == $"PVEAuthCookie={Ticket}";
                if (!token && !cookie)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                if (cookie && HttpMethods.IsPost(context.Request.Method) && context.Request.Headers["CSRFPreventionToken"] != Csrf)
                {
                    context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>()!.ReasonPhrase = "Permission check failed (invalid csrf token)";
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }

            await next(context);
        });

        _app.MapPost("/api2/json/access/ticket", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            return (form["username"].ToString(), form["password"].ToString()) switch
            {
                ("root@pam", Password) => Results.Json(new { data = new { ticket = Ticket, CSRFPreventionToken = Csrf, username = "root@pam" } }, ProxmoxJson),
                ("tfa@pve", Password) => Results.Json(new { data = new { ticket = "PVE:!tfa!tfa@pve:6720F00D::x", NeedTFA = 1, username = "tfa@pve" } }, ProxmoxJson),
                _ => Results.Json(new { data = (object?)null }, statusCode: StatusCodes.Status401Unauthorized),
            };
        });
        _app.MapGet("/api2/json/cluster/resources", () => Results.Json(new
        {
            data = new object[]
            {
                new { id = "qemu/105", type = "qemu", vmid = 105, name = "app01", node = "pve2", status = "running", template = 0 },
                new { id = "qemu/101", type = "qemu", vmid = 101, name = "db01", node = "pve1", status = "stopped", template = 0 },
                new { id = "lxc/300", type = "lxc", vmid = 300, name = "dns01", node = "pve1", status = "running" },
            },
        }));
        _app.MapPost("/api2/json/nodes/{node}/{type}/{vmid}/vncproxy", async (HttpRequest request, string node, string type, int vmid) =>
        {
            var form = await request.ReadFormAsync();
            if (form["websocket"] != "1")
            {
                return Results.BadRequest();
            }

            var ticket = $"PVEVNC:{Interlocked.Increment(ref _proxies)}:{node}/{type}/{vmid}::sig+/=";
            OpenProxies[ticket] = $"{node}/{type}/{vmid}";
            return Results.Json(new { data = new { port = "5900", ticket, user = "root@pam", cert = "-----BEGIN CERTIFICATE-----" } }, ProxmoxJson);
        });
        _app.Map("/api2/json/nodes/{node}/{type}/{vmid}/vncwebsocket", async (HttpContext context, string node, string type, int vmid) =>
        {
            var ticket = context.Request.Query["vncticket"].ToString();
            if (!context.WebSockets.IsWebSocketRequest || context.Request.Query["port"] != "5900"
                || !OpenProxies.TryRemove(ticket, out var guest) || guest != $"{node}/{type}/{vmid}")
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync(context.WebSockets.WebSocketRequestedProtocols.Contains("binary") ? "binary" : null);
            await socket.SendAsync(Encoding.ASCII.GetBytes($"vnc {type}/{vmid} on {node}\n"), WebSocketMessageType.Binary, true, CancellationToken.None);
            var buffer = new byte[4096];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                await socket.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Binary, result.EndOfMessage, CancellationToken.None);
            }
        });
        await _app.StartAsync();

        Port = new Uri(_app.Urls.First()).Port;
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=pve1.lab", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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
