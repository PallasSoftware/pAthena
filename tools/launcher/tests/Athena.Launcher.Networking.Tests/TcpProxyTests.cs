using System.Net;
using System.Net.Sockets;
using Athena.Net.Launcher.Core;
using Athena.Net.Launcher.Networking;

namespace Athena.Net.Launcher.Networking.Tests;

public sealed class TcpProxyTests
{
    [Fact]
    public async Task TcpProxyCopiesBytesInBothDirections()
    {
        var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        var backendPort = ((IPEndPoint)backend.LocalEndpoint).Port;
        var listenPort = FreePort();
        await using var proxy = new TcpProxy(new ProxyEndpoint("test", IPAddress.Loopback, listenPort, "127.0.0.1", backendPort, [backendPort]), new NullLog());
        await proxy.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listenPort);
        using var server = await backend.AcceptTcpClientAsync();
        var clientBytes = new byte[] { 1, 2, 3, 4 };
        await client.GetStream().WriteAsync(clientBytes);
        var received = new byte[4];
        await server.GetStream().ReadExactlyAsync(received);
        Assert.Equal(clientBytes, received);
        var serverBytes = new byte[] { 8, 7, 6 };
        await server.GetStream().WriteAsync(serverBytes);
        received = new byte[3];
        await client.GetStream().ReadExactlyAsync(received);
        Assert.Equal(serverBytes, received);
        backend.Stop();
    }

    [Fact]
    public async Task MultipleTargetPorts_AreAssignedRoundRobinPerNewConnection_AndEachTunnelReachesItsOwnBackend()
    {
        var backendA = new TcpListener(IPAddress.Loopback, 0);
        var backendB = new TcpListener(IPAddress.Loopback, 0);
        backendA.Start();
        backendB.Start();
        var portA = ((IPEndPoint)backendA.LocalEndpoint).Port;
        var portB = ((IPEndPoint)backendB.LocalEndpoint).Port;
        var listenPort = FreePort();
        await using var proxy = new TcpProxy(new ProxyEndpoint("map", IPAddress.Loopback, listenPort, "127.0.0.1", portA, [portA, portB]), new NullLog());
        await proxy.StartAsync(CancellationToken.None);

        // Connection 1 -> first backend, connection 2 -> second, connection 3 -> wraps to the first.
        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, listenPort);
        using var firstServer = await backendA.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, listenPort);
        using var secondServer = await backendB.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var third = new TcpClient();
        await third.ConnectAsync(IPAddress.Loopback, listenPort);
        using var thirdServer = await backendA.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Bytes written by each client arrive only at the backend that connection was assigned to.
        await first.GetStream().WriteAsync(new byte[] { 1 });
        await second.GetStream().WriteAsync(new byte[] { 2 });
        await third.GetStream().WriteAsync(new byte[] { 3 });
        var buffer = new byte[1];
        await firstServer.GetStream().ReadExactlyAsync(buffer);
        Assert.Equal(1, buffer[0]);
        await secondServer.GetStream().ReadExactlyAsync(buffer);
        Assert.Equal(2, buffer[0]);
        await thirdServer.GetStream().ReadExactlyAsync(buffer);
        Assert.Equal(3, buffer[0]);
        backendA.Stop();
        backendB.Stop();
    }

    [Fact]
    public async Task PartialProxyStartupRollsBackEveryProxy()
    {
        var created = new List<FakeProxy>();
        var manager = new TcpProxyManager(new NullLog(), endpoint =>
        {
            var proxy = new FakeProxy(endpoint.Name == "second");
            created.Add(proxy);
            return proxy;
        });
        var endpoints = new[]
        {
            new ProxyEndpoint("first", IPAddress.Loopback, FreePort(), "localhost", 1, [1]),
            new ProxyEndpoint("second", IPAddress.Loopback, FreePort(), "localhost", 2, [2]),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(endpoints, CancellationToken.None));
        Assert.All(created, proxy => Assert.True(proxy.Stopped));
        Assert.Empty(manager.Endpoints);
    }

    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }

    private sealed class FakeProxy(bool fail) : ITcpProxy
    {
        public ProxyState State { get; private set; }
        public bool Stopped { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) { if (fail) throw new InvalidOperationException("fail"); State = ProxyState.Running; return Task.CompletedTask; }
        public Task StopAsync() { Stopped = true; State = ProxyState.Stopped; return Task.CompletedTask; }
        public async ValueTask DisposeAsync() => await StopAsync();
    }

    private sealed class NullLog : ILauncherLog
    {
        public void Information(string e, string m, IReadOnlyDictionary<string, object?>? p = null) { }
        public void Error(string e, Exception x, string m) { }
    }
}
