namespace Acta.Runtime.Modules.Execution.Api;

/// <summary>
/// Execution's declared read API for the Operations module: job-job resolution (tags resolve
/// their job/schedule scopes through it) and the operator job list. Operations composes these into
/// <see cref="IActaOperations"/>; no module reaches Execution's stores or services directly.
/// </summary>
internal interface IExecutionQueries
{
    ValueTask<long?> GetJobIdAsync(JobLookup job, CancellationToken ct);

    ValueTask<PagedResult<JobListItem>> ListJobsAsync(ListJobsQuery query, CancellationToken ct);
}
