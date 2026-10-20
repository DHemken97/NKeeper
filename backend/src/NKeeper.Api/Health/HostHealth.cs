using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NKeeper.Api.Desktop;
using NKeeper.Api.RemoteSessions;
using NKeeper.Api.Web;
using NKeeper.Infrastructure.Inventory;
using NKeeper.Infrastructure.Persistence;
using Host = NKeeper.Infrastructure.Inventory.Host;

namespace NKeeper.Api.Health;

/// <summary>Service health checks (section <c>Health</c>).</summary>
public sealed class HealthSettings
{
    public const string SectionName = "Health";

    /// <summary>Check every host's services in the background.</summary>
    public bool Enabled { get; set; } = true;

    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long one check may take before the service counts as down.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Checks run at the same time.</summary>
    [Range(1, 256)]
    public int Parallelism { get; set; } = 32;
}

/// <summary>
/// Checks hosts' services from the NKeeper server, the way a session would reach them: a TCP
/// connection, plus, for HTTPS, iDRAC and RDP, the TLS certificate (so its expiry can be shown).
/// Nothing is logged in to and no credentials are used.
/// </summary>
public sealed class HostHealthChecker(IServiceScopeFactory scopes, IOptions<HealthSettings> settings, TimeProvider timeProvider, ILogger<HostHealthChecker> logger)
{
    /// <summary>Default ports, for services stored without one.</summary>
    private static readonly Dictionary<ServiceProtocol, int> DefaultPorts = new()
    {
        [ServiceProtocol.Ssh] = 22,
        [ServiceProtocol.Telnet] = 23,
        [ServiceProtocol.Rdp] = 3389,
        [ServiceProtocol.Rpc] = 135,
        [ServiceProtocol.Vnc] = 5900,
        [ServiceProtocol.Http] = 80,
        [ServiceProtocol.Https] = 443,
        [ServiceProtocol.Kvm] = 22,
        [ServiceProtocol.Idrac] = 443,
    };

    /// <summary>Checks every host; returns how many services were checked.</summary>
    public async Task<int> CheckAllAsync(CancellationToken cancellationToken)
    {
        List<Host> hosts;
        await using (var scope = scopes.CreateAsyncScope())
        {
            // Hosts reached through a jump host aren't reachable from here, so they aren't checked.
            hosts = await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().Hosts.AsNoTracking()
                .Where(h => h.JumpHostId == null)
                .Include(h => h.Services).AsSplitQuery().ToListAsync(cancellationToken);
        }

        var count = 0;
        await Parallel.ForEachAsync(
            hosts,
            new ParallelOptions { MaxDegreeOfParallelism = settings.Value.Parallelism, CancellationToken = cancellationToken },
            async (host, token) => Interlocked.Add(ref count, (await CheckAsync(host, token)).Count));
        return count;
    }

    /// <summary>Checks one host now and returns its results.</summary>
    public async Task<List<ServiceCheck>> CheckHostAsync(Guid hostId, CancellationToken cancellationToken)
    {
        Host? host;
        await using (var scope = scopes.CreateAsyncScope())
        {
            host = await scope.ServiceProvider.GetRequiredService<NKeeperDbContext>().Hosts.AsNoTracking()
                .Include(h => h.Services).AsSplitQuery().FirstOrDefaultAsync(h => h.Id == hostId, cancellationToken);
        }

        return host is null || host.JumpHostId is not null ? [] : await CheckAsync(host, cancellationToken);
    }

    private async Task<List<ServiceCheck>> CheckAsync(Host host, CancellationToken cancellationToken)
    {
        // The same endpoint once, even when two services share it.
        var targets = host.Services
            .Where(s => DefaultPorts.ContainsKey(s.Protocol))
            .Select(s => (Service: s, Address: s.Address ?? host.IpAddress ?? host.Hostname!, Port: s.Port ?? DefaultPorts[s.Protocol]))
            .DistinctBy(t => (t.Service.Protocol, t.Port, t.Address))
            .ToList();

        var results = new List<ServiceCheck>(targets.Count);
        foreach (var (service, address, port) in targets)
        {
            var check = await ProbeAsync(service, address, port, cancellationToken);
            check.HostId = host.Id;
            check.Address = service.Address ?? string.Empty;
            results.Add(check);
        }

        await SaveAsync(host.Id, results, cancellationToken);
        return results;
    }

    private async Task<ServiceCheck> ProbeAsync(HostService service, string address, int port, CancellationToken cancellationToken)
    {
        // Whole milliseconds: what's returned now matches what the database keeps (it stores microseconds).
        var now = timeProvider.GetUtcNow();
        var check = new ServiceCheck { Protocol = service.Protocol, Port = port, CheckedAtUtc = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond)) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Value.Timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            switch (service.Protocol)
            {
                case ServiceProtocol.Https or ServiceProtocol.Idrac:
                    var certificate = await WebTargetProbe.ProbeAsync(address, port, timeout.Token);
                    (check.CertificateExpiresUtc, check.CertificateSubject) = (certificate.NotAfter, certificate.Subject);
                    break;
                case ServiceProtocol.Rdp:
                    await ConnectAsync(address, port, timeout.Token);
                    check.LatencyMs = (int)watch.ElapsedMilliseconds;
                    try
                    {
                        // The certificate is a bonus: a server that answers but won't show it is still up.
                        var identity = await RdpCertificateProbe.ProbeAsync(address, port, service.RdpSecurity ?? RdpSecurityMode.Auto, timeout.Token);
                        (check.CertificateExpiresUtc, check.CertificateSubject) = (identity.NotAfter, identity.Subject);
                    }
                    catch (Exception ex) when (ex is SessionException or IOException or OperationCanceledException or SocketException)
                    {
                    }

                    break;
                default:
                    await ConnectAsync(address, port, timeout.Token);
                    break;
            }

            check.IsUp = true;
            check.LatencyMs ??= (int)watch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            check.Error = $"No answer within {settings.Value.Timeout.TotalSeconds:0} seconds";
        }
        catch (SessionException ex)
        {
            check.Error = ex.Message;
        }
        catch (SocketException ex)
        {
            check.Error = $"Couldn't connect ({SocketErrors.Describe(ex.SocketErrorCode, service.Protocol.ToString())})";
        }
        catch (IOException ex)
        {
            check.Error = ex.Message;
        }

        return check;
    }

    private static async Task ConnectAsync(string address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(address, port, cancellationToken);
    }

    /// <summary>Replaces the host's stored results, keeping when each down service went down.</summary>
    private async Task SaveAsync(Guid hostId, List<ServiceCheck> results, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NKeeperDbContext>();
            var existing = await db.ServiceChecks.Where(c => c.HostId == hostId).ToListAsync(cancellationToken);
            foreach (var result in results)
            {
                var stored = existing.FirstOrDefault(c => c.Protocol == result.Protocol && c.Port == result.Port && c.Address == result.Address);
                result.DownSinceUtc = result.IsUp ? null : stored?.DownSinceUtc ?? result.CheckedAtUtc;
                if (stored is null)
                {
                    result.Id = Guid.NewGuid();
                    db.ServiceChecks.Add(result);
                    continue;
                }

                existing.Remove(stored);
                result.Id = stored.Id;
                db.Entry(stored).CurrentValues.SetValues(result);
            }

            // Services that were removed from the host.
            db.ServiceChecks.RemoveRange(existing);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // The host was deleted meanwhile, or another check saved first: the next round settles it.
            logger.LogDebug(ex, "Couldn't save the health of host {HostId}", hostId);
        }
    }
}

/// <summary>Runs the checks every <see cref="HealthSettings.Interval"/>.</summary>
internal sealed class HealthMonitor(HostHealthChecker checker, IOptions<HealthSettings> settings, ILogger<HealthMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(settings.Value.Interval);
        do
        {
            try
            {
                var checks = await checker.CheckAllAsync(stoppingToken);
                logger.LogDebug("Checked {Count} services", checks);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The health checks failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
