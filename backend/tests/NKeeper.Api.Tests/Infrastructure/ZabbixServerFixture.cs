using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

[assembly: AssemblyFixture(typeof(NKeeper.Api.Tests.Infrastructure.ZabbixServerFixture))]

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// A fake Zabbix 7.0 frontend on 127.0.0.1 over HTTPS with a self-signed certificate, at
/// <c>/zabbix/api_jsonrpc.php</c>. It answers the JSON-RPC methods NKeeper calls from fixed data,
/// requires <see cref="ApiToken"/> as a Bearer token, and, like Zabbix, refuses
/// <c>apiinfo.version</c> when it carries one.
/// </summary>
public sealed class ZabbixServerFixture : IAsyncLifetime
{
    public const string ApiToken = "4b0c-test-token";
    public const string Version = "7.0.5";

    private WebApplication? _app;

    public int Port { get; private set; }

    /// <summary>The frontend's address, as an administrator would paste it.</summary>
    public string Url => $"https://127.0.0.1:{Port}/zabbix/";

    public string CertificateFingerprint { get; private set; } = string.Empty;

    /// <summary>The fixed hosts: pve1 (agent, inventory, items, a problem), Web 01 (DNS name) and Core switch (SNMP).</summary>
    public static readonly Dictionary<string, object>[] Hosts =
    [
        new()
        {
            ["hostid"] = "10084", ["host"] = "pve1", ["name"] = "pve1", ["status"] = "0", ["description"] = "Proxmox node", ["maintenance_status"] = "0",
            ["interfaces"] = new[] { Interface("1", "192.168.42.2", "", "10050", "1") },
            ["inventory"] = new Dictionary<string, string>
            {
                ["os"] = "Debian 12", ["macaddress_a"] = "aa-bb-cc-00-00-01", ["macaddress_b"] = "", ["serialno_a"] = "SN-PVE1", ["location"] = "Rack 4",
            },
            ["tags"] = new[] { Tag("role", "hypervisor"), Tag("bad:name", "x") },
            ["hostgroups"] = new[] { new { name = "Hypervisors" } },
        },
        new()
        {
            ["hostid"] = "10085", ["host"] = "web01", ["name"] = "Web 01", ["status"] = "0", ["description"] = "", ["maintenance_status"] = "1",
            ["interfaces"] = new[] { Interface("1", "10.0.0.20", "web01.example.com", "10050", "2", "Connection refused") },
            ["inventory"] = System.Array.Empty<object>(),
            ["tags"] = System.Array.Empty<object>(),
            ["hostgroups"] = new[] { new { name = "Web servers" } },
        },
        new()
        {
            ["hostid"] = "10086", ["host"] = "Core switch", ["name"] = "Core switch", ["status"] = "1", ["description"] = "", ["maintenance_status"] = "0",
            ["interfaces"] = new[] { Interface("2", "10.0.0.250", "", "161", "0") },
            ["inventory"] = System.Array.Empty<object>(),
            ["tags"] = System.Array.Empty<object>(),
            ["hostgroups"] = new[] { new { name = "Network" } },
        },
    ];

    private static readonly Dictionary<string, object>[] Items =
    [
        Item("10084", "system.cpu.num", "Number of CPUs", "12", "", 1_790_000_000),
        Item("10084", "vm.memory.size[total]", "Total memory", "16532209664", "B", 1_790_000_000),
        Item("10084", "system.uptime", "System uptime", "3256722", "uptime", 1_790_000_000),
        Item("10084", "agent.version", "Version of Zabbix agent running", "", "", 0),
        Item("10084", "net.if.in[\"nic0\"]", "Interface nic0: Bits received", "1423976", "bps", 1_790_000_000),
    ];

    public async ValueTask InitializeAsync()
    {
        using var certificate = CreateSelfSignedCertificate();
        CertificateFingerprint = string.Join(':', Convert.ToHexString(SHA256.HashData(certificate.RawData)).Chunk(2).Select(c => new string(c)));
        var serverCertificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(serverCertificate)));
        _app = builder.Build();
        _app.MapPost("/zabbix/api_jsonrpc.php", Handle);
        await _app.StartAsync();
        Port = new Uri(_app.Urls.First()).Port;
    }

    private static async Task<IResult> Handle(HttpRequest request)
    {
        using var body = await JsonDocument.ParseAsync(request.Body);
        var root = body.RootElement;
        var method = root.GetProperty("method").GetString();
        var id = root.GetProperty("id").GetInt32();
        var parameters = root.GetProperty("params");
        var authorization = request.Headers.Authorization.ToString();

        if (method == "apiinfo.version")
        {
            return authorization.Length > 0
                ? Error(id, "The \"apiinfo.version\" method must be called without authorization header.")
                : Result(id, Version);
        }

        if (authorization != $"Bearer {ApiToken}")
        {
            return Error(id, "Not authorized.");
        }

        return method switch
        {
            "host.get" => Result(id, HostGet(parameters)),
            "hostinterface.get" => Result(id, HostInterfaceGet(parameters)),
            "item.get" => Result(id, Items
                .Where(i => Ids(parameters, "hostids").Contains((string)i["hostid"]))
                .Where(i => !parameters.TryGetProperty("filter", out var filter)
                    || filter.GetProperty("key_").EnumerateArray().Any(k => k.GetString() == (string)i["key_"]))
                .ToArray()),
            "problem.get" => Result(id, Ids(parameters, "hostids").Contains("10084")
                ? new[] { new { eventid = "9001", name = "High CPU utilization", severity = "4", clock = "1790000100", acknowledged = "0", suppressed = "0" } }
                : []),
            _ => Error(id, $"Incorrect method \"{method}\"."),
        };
    }

    private static object HostGet(JsonElement parameters)
    {
        IEnumerable<Dictionary<string, object>> hosts = Hosts;
        if (parameters.TryGetProperty("hostids", out _))
        {
            var ids = Ids(parameters, "hostids");
            hosts = hosts.Where(h => ids.Contains((string)h["hostid"]));
        }

        if (parameters.TryGetProperty("search", out var search))
        {
            var text = search.GetProperty("name").GetString()!;
            hosts = hosts.Where(h => ((string)h["name"]).Contains(text, StringComparison.OrdinalIgnoreCase)
                || ((string)h["host"]).Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        if (parameters.TryGetProperty("countOutput", out _))
        {
            return hosts.Count().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return hosts.ToArray();
    }

    private static object[] HostInterfaceGet(JsonElement parameters)
    {
        var text = parameters.GetProperty("search").GetProperty("ip").GetString()!;
        return Hosts
            .Where(h => ((Dictionary<string, string>[])h["interfaces"]).Any(i => i["ip"].Contains(text, StringComparison.Ordinal) || i["dns"].Contains(text, StringComparison.OrdinalIgnoreCase)))
            .Select(object (h) => new { hostid = h["hostid"] })
            .ToArray();
    }

    private static HashSet<string> Ids(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var ids) ? [.. ids.EnumerateArray().Select(i => i.GetString()!)] : [];

    private static IResult Result(int id, object result) => Results.Json(new { jsonrpc = "2.0", result, id });

    private static IResult Error(int id, string data) =>
        Results.Json(new { jsonrpc = "2.0", error = new { code = -32602, message = "Invalid params.", data }, id });

    private static Dictionary<string, string> Interface(string type, string ip, string dns, string port, string available, string error = "") => new()
    {
        ["type"] = type, ["ip"] = ip, ["dns"] = dns, ["port"] = port, ["useip"] = dns.Length == 0 ? "1" : "0", ["main"] = "1", ["available"] = available, ["error"] = error,
    };

    private static Dictionary<string, string> Tag(string tag, string value) => new() { ["tag"] = tag, ["value"] = value };

    private static Dictionary<string, object> Item(string hostId, string key, string name, string value, string units, long clock) => new()
    {
        ["hostid"] = hostId, ["key_"] = key, ["name"] = name, ["lastvalue"] = value, ["units"] = units,
        ["lastclock"] = clock.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=zabbix.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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
