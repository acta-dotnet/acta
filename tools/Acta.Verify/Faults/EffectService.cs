using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;

namespace Acta.Verify;

internal sealed class EffectService : IDisposable
{
    public string Endpoint { get; }
    public string Token { get; } = Guid.NewGuid().ToString("N");
    public string StorePath { get; }
    private OwnedWorker? _process;
    public int ProcessId => _process?.Id ?? throw new InvalidOperationException("Oracle has not started.");

    public EffectService(string storePath)
    {
        StorePath = storePath;
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        Endpoint = $"http://127.0.0.1:{port}/";
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _process = OwnedWorker.Oracle(this);
        await _process.ReadyAsync(ct);
    }

    public async Task<int?> WaitBarrierAsync(string boundary, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            await using var c = new SqliteConnection($"Data Source={StorePath};Mode=ReadOnly");
            await c.OpenAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT pid FROM receipts WHERE kind=@kind ORDER BY id LIMIT 1";
            cmd.Parameters.AddWithValue("@kind", "barrier-" + boundary);
            if (await cmd.ExecuteScalarAsync(ct) is long pid)
            {
                return checked((int)pid);
            }
            await Task.Delay(25, ct);
        }
        return null;
    }

    public async Task ReleaseAsync(CancellationToken ct)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + "release");
        request.Headers.Add("X-Bench-Token", Token);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<(long Requests, long Applications)> CountsAsync(CancellationToken ct)
    {
        await using var c = new SqliteConnection($"Data Source={StorePath};Mode=ReadOnly");
        await c.OpenAsync(ct);
        await using var command = c.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM receipts WHERE kind='effect'), (SELECT COUNT(*) FROM applications)";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public async Task<Check[]> CheckIdempotentKeysAsync(IEnumerable<string> expectedKeys, CancellationToken ct)
    {
        var wanted = expectedKeys.ToHashSet(StringComparer.Ordinal);
        Dictionary<string, long> requests = new(StringComparer.Ordinal);
        Dictionary<string, long> applications = new(StringComparer.Ordinal);
        await using var c = new SqliteConnection($"Data Source={StorePath};Mode=ReadOnly");
        await c.OpenAsync(ct);
        foreach (
            var (query, target) in new[]
            {
                ("SELECT business_key,COUNT(*) FROM receipts WHERE kind='effect' GROUP BY business_key", requests),
                ("SELECT business_key,COUNT(*) FROM applications GROUP BY business_key", applications),
            }
        )
        {
            await using var command = c.CreateCommand();
            command.CommandText = query;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                target[reader.GetString(0)] = reader.GetInt64(1);
            }
        }
        List<Check> checks = [];
        foreach (var key in wanted.Where(k => applications.GetValueOrDefault(k) != 1 || requests.GetValueOrDefault(k) < 1))
        {
            checks.Add(
                new(
                    "sink-business-application",
                    key,
                    false,
                    $"{requests.GetValueOrDefault(key)} requests; {applications.GetValueOrDefault(key)} business applications. Deduplication is provided by the external sink."
                )
            );
        }
        checks.Add(
            new(
                "verified-sink-business-application",
                "cohort",
                true,
                $"Verified {wanted.Count - checks.Count} keys applied once by the external sink; failures are retained individually."
            )
        );
        foreach (var key in requests.Keys.Concat(applications.Keys).Distinct(StringComparer.Ordinal).Except(wanted, StringComparer.Ordinal))
        {
            checks.Add(new("unexpected-sink-key", key, false, "External effect identity was not in the frozen workload."));
        }
        return [.. checks];
    }

    public void Dispose() => _process?.Dispose();

    public static async Task ServeAsync(string endpoint, string token, string storePath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = storePath, Pooling = false }.ToString();
        await using (var c = new SqliteConnection(connectionString))
        {
            await c.OpenAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE receipts(id INTEGER PRIMARY KEY, kind TEXT NOT NULL, business_key TEXT NOT NULL, pid INTEGER NOT NULL); CREATE TABLE applications(id INTEGER PRIMARY KEY, business_key TEXT NOT NULL, dedup_key TEXT UNIQUE);";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        using var listener = new HttpListener();
        listener.Prefixes.Add(endpoint);
        listener.Start();
        Console.WriteLine("ACTA_BENCH_READY");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slots = new SemaphoreSlim(64);
        ConcurrentDictionary<long, Task> active = [];
        long serial = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(ct);
                await slots.WaitAsync(ct);
                var id = Interlocked.Increment(ref serial);
                var work = HandleAsync(context);
#pragma warning disable CA2025 // The finally block joins active requests before disposing their semaphore.
                active[id] = RemoveAsync(id, work);
#pragma warning restore CA2025
            }
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(active.Values);
        }

        async Task RemoveAsync(long id, Task work)
        {
            try
            {
                await work;
            }
            catch (Exception) { }
            finally
            {
                active.TryRemove(id, out _);
                slots.Release();
            }
        }

        async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                if (context.Request.Headers["X-Bench-Token"] != token)
                {
                    context.Response.StatusCode = 403;
                    return;
                }
                var path = context.Request.Url!.AbsolutePath;
                if (path == "/release")
                {
                    release.TrySetResult();
                    return;
                }
                var key = context.Request.QueryString["key"] ?? throw new ArgumentException("Missing key.");
                var kind = path == "/effect" ? "effect" : "barrier-" + (context.Request.QueryString["boundary"] ?? "claim");
                var pid = int.Parse(context.Request.QueryString["pid"] ?? "0", CultureInfo.InvariantCulture);
                await using var c = new SqliteConnection(connectionString);
                await c.OpenAsync(ct);
                await using (var durability = c.CreateCommand())
                {
                    // synchronous is connection-scoped, while WAL mode persists in the file.
                    durability.CommandText = "PRAGMA synchronous=FULL;";
                    await durability.ExecuteNonQueryAsync(ct);
                }
                await using (var transaction = await c.BeginTransactionAsync(ct))
                {
                    await using var receipt = c.CreateCommand();
                    receipt.Transaction = (SqliteTransaction)transaction;
                    receipt.CommandText = "INSERT INTO receipts(kind,business_key,pid) VALUES(@kind,@key,@pid)";
                    receipt.Parameters.AddWithValue("@kind", kind);
                    receipt.Parameters.AddWithValue("@key", key);
                    receipt.Parameters.AddWithValue("@pid", pid);
                    await receipt.ExecuteNonQueryAsync(ct);
                    if (path == "/effect")
                    {
                        await using var effect = c.CreateCommand();
                        effect.Transaction = (SqliteTransaction)transaction;
                        effect.CommandText = "INSERT OR IGNORE INTO applications(business_key,dedup_key) VALUES(@key,@dedup)";
                        effect.Parameters.AddWithValue("@key", key);
                        effect.Parameters.AddWithValue("@dedup", context.Request.QueryString["idempotent"] == "true" ? key : DBNull.Value);
                        await effect.ExecuteNonQueryAsync(ct);
                    }
                    await transaction.CommitAsync(ct);
                }
                if (path == "/barrier")
                {
                    await release.Task.WaitAsync(ct);
                }
                var delay = int.Parse(context.Request.QueryString["delay"] ?? "0", CultureInfo.InvariantCulture);
                if (delay > 0)
                {
                    await Task.Delay(delay, ct);
                }
                context.Response.StatusCode = 200;
            }
            catch (Exception)
            {
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}
