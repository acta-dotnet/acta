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

    /// <summary>Whether the sink holds an application of <paramref name="key"/>. The sink records the question and changes nothing.</summary>
    public async Task<bool> AppliedAsync(string key, CancellationToken ct) => await CallAsync("applied", key, null, false, 0, ct) == "true";

    public async Task<string> CallAsync(string path, string key, string? boundary, bool idempotent, int delay, CancellationToken ct)
    {
        if (_endpoint is null)
        {
            throw new InvalidOperationException("This workload requires the external effects process.");
        }
        var query =
            $"?key={Uri.EscapeDataString(key)}&pid={Environment.ProcessId}&idempotent={idempotent.ToString().ToLowerInvariant()}&delay={delay}&boundary={Uri.EscapeDataString(boundary ?? "")}";
        using var response = await _client.PostAsync(_endpoint + path + query, content: null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public void Dispose() => _client.Dispose();
}
