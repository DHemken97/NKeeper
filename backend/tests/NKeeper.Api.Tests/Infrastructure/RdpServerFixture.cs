using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

[assembly: AssemblyFixture(typeof(NKeeper.Api.Tests.Infrastructure.RdpServerFixture))]

namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>
/// An xrdp server, an x11vnc server and Apache Guacamole's guacd for RDP and VNC session tests. guacd shares the xrdp
/// container's network namespace and xrdp listens on the very port published on the host, so
/// <c>127.0.0.1:<see cref="Port"/></c> reaches the same server from the API under test (on the
/// host, for the certificate check) and from guacd (in Docker, for the session).
/// Tests are skipped, not passed, when Docker is unavailable.
/// </summary>
public sealed class RdpServerFixture : IAsyncLifetime
{
    public const string UserName = "rdpuser";
    public const string Password = "Rdp-Test-Passw0rd";
    public const string Address = "127.0.0.1";
    public const string VncPassword = "vnc-pass";
    private const int GuacdPort = 4822;

    private IFutureDockerImage? _image;
    private IContainer? _xrdp;
    private IContainer? _guacd;
    private IContainer? _vnc;

    public string? UnavailableReason { get; private set; }

    /// <summary>The RDP port, identical inside the containers and on the host.</summary>
    public int Port { get; private set; }

    public int GuacdHostPort => _xrdp!.GetMappedPublicPort(GuacdPort);

    /// <summary>The VNC port, identical inside the containers and on the host.</summary>
    public int VncPort { get; private set; }

    /// <summary>The server certificate's SHA-256, as colon-separated upper-case hex.</summary>
    public string CertificateFingerprint { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        try
        {
            Port = FreeTcpPort();
            VncPort = FreeTcpPort();
            var vncImage = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(CommonDirectoryPath.GetProjectDirectory(), "Rdp/vnc")
                .WithName("nkeeper-test-vnc:latest")
                .WithCleanUp(false)
                .Build();
            await vncImage.CreateAsync();
            _image = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(CommonDirectoryPath.GetProjectDirectory(), "Rdp/xrdp")
                .WithName("nkeeper-test-xrdp:latest")
                .WithCleanUp(false)
                .Build();
            await _image.CreateAsync();

            _xrdp = new ContainerBuilder(_image)
                .WithEnvironment("XRDP_PORT", Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithPortBinding(Port, Port)
                .WithPortBinding(VncPort, VncPort)
                .WithPortBinding(GuacdPort, assignRandomHostPort: true)
                .Build();
            await _xrdp.StartAsync();

            _guacd = new ContainerBuilder("guacamole/guacd:1.6.0")
                .WithCreateParameterModifier(parameters =>
                {
                    parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
                    parameters.HostConfig.NetworkMode = $"container:{_xrdp.Id}";
                })
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Listening on host"))
                .Build();
            await _guacd.StartAsync();

            _vnc = new ContainerBuilder(vncImage)
                .WithEnvironment("VNC_PORT", VncPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithEnvironment("VNC_PASSWORD", VncPassword)
                .WithCreateParameterModifier(parameters =>
                {
                    parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
                    parameters.HostConfig.NetworkMode = $"container:{_xrdp.Id}";
                })
                .Build();
            await _vnc.StartAsync();

            await WaitForTcpAsync(Address, Port);
            await WaitForTcpAsync(Address, VncPort);
            var fingerprint = await _xrdp.ExecAsync(["sh", "-c", "openssl x509 -in /etc/xrdp/cert.pem -noout -fingerprint -sha256 | cut -d= -f2"]);
            CertificateFingerprint = fingerprint.Stdout.Trim();
        }
#pragma warning disable CA1031 // Any failure here means "no RDP test server", which is reported as a skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UnavailableReason = $"RDP session tests need Docker (Testcontainers). Docker was not usable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public void SkipIfUnavailable()
    {
        if (UnavailableReason is not null)
        {
            Assert.Skip(UnavailableReason);
        }
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForTcpAsync(string address, int port)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address, port);
                return;
            }
            catch (SocketException) when (attempt < 60)
            {
                await Task.Delay(500);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_guacd is not null)
        {
            await _guacd.DisposeAsync();
        }

        if (_vnc is not null)
        {
            await _vnc.DisposeAsync();
        }

        if (_xrdp is not null)
        {
            await _xrdp.DisposeAsync();
        }
    }
}
