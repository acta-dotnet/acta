namespace Acta.Modules.Operations.Api;

/// <summary>
/// Operations' declared event-read API: the paged audit-event list. Execution's <c>JobsApi</c>
/// consumes this because <c>IJobs.ListJobEventsAsync</c> deliberately stays on the job client;
/// no other module reaches the events read model.
/// </summary>
internal interface IJobEventFeed
{
    ValueTask<PagedResult<JobEventListItem>> ListJobEventsAsync(ListJobEventsQuery query, CancellationToken ct);
}
