namespace Acta.Runtime;

/// <summary><see cref="IJobs.GetJobIdAsync"/>: resolves the lookup, an id lookup without a round trip; no SQL of its own.</summary>
internal sealed class GetJobIdHandler(JobLookupResolution lookup)
{
    public async ValueTask<GetJobIdResponse?> HandleAsync(GetJobIdQuery query, CancellationToken ct) =>
        await lookup.ResolveAsync(query.Job, ct) is { } jobId ? new GetJobIdResponse(jobId) : null;
}
