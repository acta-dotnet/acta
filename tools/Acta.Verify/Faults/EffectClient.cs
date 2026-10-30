namespace Acta.Verify;

public sealed class EffectClient : IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string? _endpoint;

    public EffectClient()
        : this(Environment.GetEnvironmentVariable("ACTA_VERIFY_EFFECTS"), Environment.GetEnvironmentVariable("ACTA_VERIFY_EFFECTS_TOKEN"))
    { }

    public EffectClient(string? endpoint, string? token)
    {
        _endpoint = endpoint;
        _client.DefaultRequestHeaders.Add("X-Verify-Token", token ?? "");
    }

    public async Task CallAsync(string path, string key, string? boundary, bool idempotent, int delay, CancellationToken ct)
    {
        if (_endpoint is null)
        {
            throw new InvalidOperationException("This workload requires the external effects process.");
        }
        var query =
            $"?key={Uri.EscapeDataString(key)}&pid={Environment.ProcessId}&idempotent={idempotent.ToString().ToLowerInvariant()}&delay={delay}&boundary={Uri.EscapeDataString(boundary ?? "")}";
        using var response = await _client.PostAsync(_endpoint + path + query, content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();
}
