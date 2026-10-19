using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Acta.Bench;

internal sealed class CutProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, (TcpClient Client, TcpClient Upstream)> _connections = [];
    private readonly List<Task> _copies = [];
    private readonly Task _accept;
    private readonly string _host;
    private readonly int _port;
    private volatile bool _enabled = true;
    public string ConnectionString { get; }
    public int DroppedConnections { get; private set; }

    public CutProxy(string provider, string connectionString)
    {
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        if (provider == "pg")
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            _host = builder.Host!;
            _port = builder.Port;
            builder.Host = "127.0.0.1";
            builder.Port = port;
            ConnectionString = builder.ToString();
        }
        else
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var parts = builder.DataSource.Split(',');
            _host = parts[0];
            _port = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            builder.DataSource = $"127.0.0.1,{port}";
            ConnectionString = builder.ToString();
        }
        _accept = AcceptAsync();
    }

    public void Cut()
    {
        _enabled = false;
        foreach (var pair in _connections.Values)
        {
            pair.Client.Dispose();
            pair.Upstream.Dispose();
            DroppedConnections++;
        }
    }

    public void Restore() => _enabled = true;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Cut();
        try
        {
            await _accept;
        }
        catch (OperationCanceledException) { }
        await Task.WhenAll(_copies);
        _listener.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        var serial = 0;
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            if (!_enabled || _connections.Count >= 128)
            {
                client.Dispose();
                continue;
            }
            var upstream = new TcpClient();
            var id = serial++;
            _connections[id] = (client, upstream);
            _copies.RemoveAll(t => t.IsCompleted);
#pragma warning disable CA2025 // CopyAsync owns both sockets; DisposeAsync joins every outstanding copy task.
            _copies.Add(CopyAsync(id, client, upstream));
#pragma warning restore CA2025
        }
    }

    private async Task CopyAsync(int id, TcpClient client, TcpClient upstream)
    {
        try
        {
            await upstream.ConnectAsync(_host, _port, _stop.Token);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var a = client.GetStream().CopyToAsync(upstream.GetStream(), linked.Token);
            var b = upstream.GetStream().CopyToAsync(client.GetStream(), linked.Token);
            await Task.WhenAny(a, b);
            await linked.CancelAsync();
            try
            {
                await Task.WhenAll(a, b);
            }
            catch (OperationCanceledException) { }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Cutting these owned connections is the declared fault, not a harness error.
        }
        finally
        {
            client.Dispose();
            upstream.Dispose();
            _connections.TryRemove(id, out _);
        }
    }
}
