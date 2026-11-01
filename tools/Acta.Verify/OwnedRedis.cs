using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;

namespace Acta.Verify;

/// <summary>
/// A Redis container the run owns for a scenario that wakes its workers through Acta.Redis: labelled like the owned
/// database containers, on a pinned loopback port, and removed when disposed.
/// </summary>
internal sealed class OwnedRedis : IAsyncDisposable
{
    private const string Image = "redis:7";
    private readonly string _container;

    private OwnedRedis(string container, string configuration)
    {
        _container = container;
        Configuration = configuration;
    }

    /// <summary>The configuration Acta.Redis connects with.</summary>
    public string Configuration { get; }

    public static async Task<OwnedRedis> StartAsync(string ownership, CancellationToken ct)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var name = $"acta-verify-redis-{Guid.NewGuid():N}";
        var configuration = $"127.0.0.1:{port}";
        Console.WriteLine($"  Starting owned Redis container; loopback port {port}.");
        try
        {
            await OwnedProvider.DockerAsync(
                ["run", "-d", "--name", name, "--label", $"acta.verify.owner={ownership}", "-p", $"127.0.0.1:{port}:6379", Image],
                null,
                ct
            );
            await WaitUntilAcceptingAsync(configuration, ct);
        }
        catch
        {
            await RemoveAsync(name);
            throw;
        }
        return new OwnedRedis(name, configuration);
    }

    private static async Task WaitUntilAcceptingAsync(string configuration, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                await using var connection = await ConnectionMultiplexer.ConnectAsync(configuration + ",connectTimeout=1000");
                await connection.GetDatabase().PingAsync();
                return;
            }
            catch (RedisException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
        }
    }

    public ValueTask DisposeAsync() => new(RemoveAsync(_container));

    private static async Task RemoveAsync(string container)
    {
        try
        {
            await OwnedProvider.DockerAsync(["rm", "-f", "-v", container], null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  could not remove the owned Redis container ({ex.GetType().Name}: {ex.Message})");
        }
    }
}
