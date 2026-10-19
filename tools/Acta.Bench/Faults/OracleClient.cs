namespace Acta.Bench;

public sealed class OracleClient : IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string? _endpoint;

    public OracleClient()
        : this(Environment.GetEnvironmentVariable("ACTA_BENCH_ORACLE"), Environment.GetEnvironmentVariable("ACTA_BENCH_ORACLE_TOKEN")) { }

    public OracleClient(string? endpoint, string? token)
    {
        _endpoint = endpoint;
        _client.DefaultRequestHeaders.Add("X-Bench-Token", token ?? "");
    }

    public async Task CallAsync(string path, string key, string? boundary, bool idempotent, int delay, CancellationToken ct)
    {
        if (_endpoint is null)
        {
            throw new InvalidOperationException("This workload requires the external oracle process.");
        }
        var query =
            $"?key={Uri.EscapeDataString(key)}&pid={Environment.ProcessId}&idempotent={idempotent.ToString().ToLowerInvariant()}&delay={delay}&boundary={Uri.EscapeDataString(boundary ?? "")}";
        using var response = await _client.PostAsync(_endpoint + path + query, content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();
}
