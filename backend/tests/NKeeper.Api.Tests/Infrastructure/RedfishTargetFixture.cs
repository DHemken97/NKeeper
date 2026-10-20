using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

[assembly: AssemblyFixture(typeof(NKeeper.Api.Tests.Infrastructure.RedfishTargetFixture))]

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// A fake iDRAC9 on 127.0.0.1 over HTTPS with a self-signed certificate: the Redfish resources
/// NKeeper reads (system, manager, Dell's card service and attributes), the reset and
/// GetKVMSession actions, and a console page. It requires HTTP Basic with root / calvin.
/// </summary>
public sealed class RedfishTargetFixture : IAsyncLifetime
{
    public const string Username = "root";
    public const string Password = "calvin";
    public const string TempUsername = "kvm-temp-user";
    public const string TempPassword = "kvm temp/pass";

    private const string System = "/redfish/v1/Systems/System.Embedded.1";
    private const string Manager = "/redfish/v1/Managers/iDRAC.Embedded.1";

    private WebApplication? _app;

    public int Port { get; private set; }

    /// <summary>SHA-256 of the HTTPS certificate, as colon-separated upper-case hex.</summary>
    public string CertificateFingerprint { get; private set; } = string.Empty;

    /// <summary>The virtual console plug-in type the fake reports.</summary>
    public string PluginType { get; set; } = "eHTML5";

    /// <summary>Reset types posted to the system, in order.</summary>
    public ConcurrentQueue<string> Resets { get; } = new();

    public async ValueTask InitializeAsync()
    {
        using var certificate = CreateSelfSignedCertificate();
        CertificateFingerprint = string.Join(':', Convert.ToHexString(SHA256.HashData(certificate.RawData)).Chunk(2).Select(c => new string(c)));
        var serverCertificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(serverCertificate)));
        _app = builder.Build();

        // Redfish requires authentication; the console page and web interface log in by themselves.
        _app.Use(async (context, next) =>
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
            if (context.Request.Path.StartsWithSegments("/redfish") && context.Request.Headers.Authorization != expected)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });

        _app.MapGet("/redfish/v1/Systems", () => Collection(System));
        _app.MapGet("/redfish/v1/Managers", () => Collection(Manager));
        _app.MapGet(System, () => Body(new Dictionary<string, object>
        {
            ["@odata.id"] = System,
            ["Manufacturer"] = "Dell Inc.",
            ["Model"] = "PowerEdge R750",
            ["SKU"] = "ABC1234",
            ["SerialNumber"] = "CN7016321L0042",
            ["HostName"] = "esx01.example.com",
            ["PowerState"] = "On",
            ["BiosVersion"] = "1.13.2",
            ["Status"] = new { Health = "OK", HealthRollup = "Warning" },
            ["ProcessorSummary"] = new { Count = 2, LogicalProcessorCount = 128, Model = "Intel(R) Xeon(R) Gold 6338 CPU @ 2.00GHz" },
            ["MemorySummary"] = new { TotalSystemMemoryGiB = 512 },
            ["Actions"] = new Dictionary<string, object>
            {
                ["#ComputerSystem.Reset"] = new Dictionary<string, object>
                {
                    ["target"] = $"{System}/Actions/ComputerSystem.Reset",
                    ["ResetType@Redfish.AllowableValues"] = new[] { "On", "ForceOff", "ForceRestart", "GracefulShutdown", "PushPowerButton", "Nmi", "PowerCycle" },
                },
            },
        }));
        _app.MapPost($"{System}/Actions/ComputerSystem.Reset", async (HttpRequest request) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            Resets.Enqueue(body.RootElement.GetProperty("ResetType").GetString()!);
            return Results.NoContent();
        });
        _app.MapGet(Manager, () => Body(new { Model = "15G Monolithic", FirmwareVersion = "7.00.00.171" }));
        _app.MapGet($"{Manager}/Oem/Dell/DelliDRACCardService", () => Body(new Dictionary<string, object>
        {
            ["Actions"] = new Dictionary<string, object>
            {
                ["#DelliDRACCardService.GetKVMSession"] = new { target = $"{Manager}/Oem/Dell/DelliDRACCardService/Actions/DelliDRACCardService.GetKVMSession" },
            },
        }));
        _app.MapGet($"{Manager}/Attributes", () => Body(new
        {
            Attributes = new Dictionary<string, string> { ["VirtualConsole.1.PluginType"] = PluginType, ["VirtualConsole.1.Enable"] = "Enabled" },
        }));
        _app.MapPost($"{Manager}/Oem/Dell/DelliDRACCardService/Actions/DelliDRACCardService.GetKVMSession", () =>
            Body(new { TempUsername, TempPassword }));
        _app.MapGet("/console", (HttpRequest request) =>
            $"console for {request.Query["username"]} as {request.Query["tempUsername"]} with {request.Query["tempPassword"]}");
        _app.MapGet("/", () => "iDRAC web interface");
        await _app.StartAsync();

        Port = new Uri(_app.Urls.First()).Port;
    }

    /// <summary>Redfish property names are PascalCase, unlike ASP.NET Core's default JSON.</summary>
    private static readonly JsonSerializerOptions RedfishJson = new() { PropertyNamingPolicy = null };

    private static IResult Body(object value) => Results.Json(value, RedfishJson);

    private static IResult Collection(string member) => Body(new Dictionary<string, object>
    {
        ["Members"] = new[] { new Dictionary<string, string> { ["@odata.id"] = member } },
    });

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=idrac-ABC1234", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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
