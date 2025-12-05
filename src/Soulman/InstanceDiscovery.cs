using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.NetworkInformation;

namespace Soulman;

public record DiscoveredInstance(string MachineName, string Version, IPEndPoint EndPoint);

public class InstanceDiscovery : IHostedService, IDisposable
{
    private const int DiscoveryPort = 45832;
    private const string MagicHeader = "SOULMAN_DISCOVERY_V1";
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.64.64");
    private readonly ILogger<InstanceDiscovery> _logger;
    private readonly string _machineName;
    private readonly string _version;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, PendingDiscovery> _pending = new(StringComparer.OrdinalIgnoreCase);
    private Task? _listenerTask;
    private UdpClient? _listener;

    public InstanceDiscovery(ILogger<InstanceDiscovery> logger)
    {
        _logger = logger;
        _machineName = Environment.MachineName;
        _version = SoulmanVersion.GetVersion();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_listenerTask != null)
        {
            return Task.CompletedTask;
        }

        _listenerTask = Task.Run(() => ListenAsync(_cts.Token), cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        if (_listenerTask != null)
        {
            try
            {
                await _listenerTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Stopping instance discovery listener");
            }
        }

        DisposeListener();
    }

    public void Dispose()
    {
        _cts.Cancel();
        DisposeListener();
        _cts.Dispose();
    }

    private void DisposeListener()
    {
        try
        {
            _listener?.Dispose();
        }
        catch
        {
            // ignore shutdown errors
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        try
        {
            _listener = new UdpClient(AddressFamily.InterNetwork);
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.EnableBroadcast = true;
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            try
            {
                _listener.JoinMulticastGroup(MulticastAddress);
                _listener.MulticastLoopback = true;
            }
            catch
            {
                // multicast join is best effort; continue even if it fails
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
            var result = await _listener.ReceiveAsync().WaitAsync(token);
            await HandleMessageAsync(result, token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        _logger.LogDebug(ex, "Instance discovery listener failed");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not start instance discovery listener on UDP {Port}", DiscoveryPort);
            }
        }
    }

    private async Task HandleMessageAsync(UdpReceiveResult result, CancellationToken token)
    {
        var message = Encoding.UTF8.GetString(result.Buffer);
        if (TryHandleResponse(message, result.RemoteEndPoint))
        {
            return;
        }

        var expectedPrefix = $"{MagicHeader}:REQUEST:";
        if (message.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            await ReplyToRequestAsync(message, result, token);
        }
    }

    private async Task ReplyToRequestAsync(string message, UdpReceiveResult result, CancellationToken token)
    {
        var expectedPrefix = $"{MagicHeader}:REQUEST:";
        var remainder = message[expectedPrefix.Length..];
        if (string.IsNullOrWhiteSpace(remainder))
        {
            return;
        }

        var parts = remainder.Split(':', StringSplitOptions.RemoveEmptyEntries);
        var requestId = parts.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        int? responsePort = null;
        for (var i = 1; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "PORT", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(parts[i + 1], out var parsed))
            {
                responsePort = parsed;
                break;
            }
        }

        var replyEndpoint = responsePort.HasValue
            ? new IPEndPoint(result.RemoteEndPoint.Address, responsePort.Value)
            : result.RemoteEndPoint;

        var payload = $"{MagicHeader}:RESPONSE:{requestId}:{_machineName}|{_version}";
        var bytes = Encoding.UTF8.GetBytes(payload);

        try
        {
            if (_listener != null)
            {
                await _listener.SendAsync(bytes, bytes.Length, replyEndpoint).WaitAsync(token);

                // also reply to the sender's source port so we work even when inbound 45832 is blocked
                if (!replyEndpoint.Equals(result.RemoteEndPoint))
                {
                    await _listener.SendAsync(bytes, bytes.Length, result.RemoteEndPoint).WaitAsync(token);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed replying to discovery request from {Remote}", result.RemoteEndPoint);
        }
    }

    public async Task<IReadOnlyCollection<DiscoveredInstance>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var request = $"{MagicHeader}:REQUEST:{requestId}:PORT:{DiscoveryPort}";
        var pending = new PendingDiscovery();
        _pending[requestId] = pending;

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.MulticastLoopback = false;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        try
        {
            var probeBytes = Encoding.UTF8.GetBytes(request);
            foreach (var endpoint in GetBroadcastEndpoints())
            {
                // send a couple times to improve odds across flaky networks
                for (var i = 0; i < 2; i++)
                {
                    try
                    {
                        await udp.SendAsync(probeBytes, endpoint);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed sending discovery probe to {Endpoint}", endpoint);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed sending discovery probe");
            _pending.TryRemove(requestId, out _);
            return Array.Empty<DiscoveredInstance>();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        while (!linked.IsCancellationRequested)
        {
            try
            {
                var response = await udp.ReceiveAsync().WaitAsync(linked.Token);
                var message = Encoding.UTF8.GetString(response.Buffer);
                TryHandleResponse(message, response.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Discovery receive failed");
            }
        }

        _pending.TryRemove(requestId, out var finished);
        return finished?.Results ?? Array.Empty<DiscoveredInstance>();
    }

    private static IReadOnlyCollection<IPEndPoint> GetBroadcastEndpoints()
    {
        var endpoints = new HashSet<IPEndPoint>();

        // Global limited broadcast
        endpoints.Add(new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
        endpoints.Add(new IPEndPoint(MulticastAddress, DiscoveryPort));

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var iface in interfaces)
            {
                var props = iface.GetIPProperties();
                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        unicast.IPv4Mask == null)
                    {
                        continue;
                    }

                    var broadcast = CalculateBroadcast(unicast.Address, unicast.IPv4Mask);
                    if (broadcast != null)
                    {
                        endpoints.Add(new IPEndPoint(broadcast, DiscoveryPort));
                    }
                }
            }
        }
        catch
        {
            // non-fatal; fall back to limited broadcast only
        }

        return endpoints;
    }

    private static IPAddress? CalculateBroadcast(IPAddress address, IPAddress mask)
    {
        try
        {
            var ipBytes = address.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            if (ipBytes.Length != maskBytes.Length)
            {
                return null;
            }

            var broadcast = new byte[ipBytes.Length];
            for (var i = 0; i < ipBytes.Length; i++)
            {
                broadcast[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 255));
            }

            return new IPAddress(broadcast);
        }
        catch
        {
            return null;
        }
    }

    private bool TryHandleResponse(string message, IPEndPoint remote)
    {
        var responsePrefix = $"{MagicHeader}:RESPONSE:";
        if (!message.StartsWith(responsePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = message[responsePrefix.Length..];
        var separatorIndex = remainder.IndexOf(':');
        if (separatorIndex < 0)
        {
            return true;
        }

        var requestId = remainder[..separatorIndex];
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return true;
        }

        var response = remainder[(separatorIndex + 1)..];
        var parts = response.Split('|');
        var machine = parts.FirstOrDefault() ?? string.Empty;
        var version = parts.Skip(1).FirstOrDefault() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(machine) ||
            string.Equals(machine, _machineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_pending.TryGetValue(requestId, out var pending))
        {
            pending.Add(new DiscoveredInstance(machine, version, remote));
        }

        return true;
    }

    private class PendingDiscovery
    {
        private readonly ConcurrentDictionary<string, DiscoveredInstance> _instances =
            new(StringComparer.OrdinalIgnoreCase);

        public void Add(DiscoveredInstance instance)
        {
            _instances.AddOrUpdate(instance.MachineName, instance, (_, _) => instance);
        }

        public IReadOnlyCollection<DiscoveredInstance> Results => _instances.Values.ToArray();
    }
}
