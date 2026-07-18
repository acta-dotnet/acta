<script>
  import { scope } from '../scope';
  import { displayFormatter } from '../format';
  import { now } from '../time';
  import Page from '../components/Page.svelte';
  import ActaGrid from '../components/grid/ActaGrid.svelte';
  import TimeCell from '../components/TimeCell.svelte';
  import FilterBar from '../components/FilterBar.svelte';
  import { routes } from '../routes.ts';
  import JobRef from '../components/JobRef.svelte';

  let grid;
  let newCount = $state(0);
  let newLabel = $state('0');

  const columns = [
    { key: 'createdAtUtc', header: 'Time' },
    { key: 'eventCode', header: 'Event' },
    { key: 'job', header: 'Job' },
    { key: 'jobNamespace', header: 'Namespace', dimRepeats: true },
    { key: 'executionNumber', header: 'Attempt', align: 'right' },
    { key: 'actor', header: 'Actor' },
    { key: 'fromStatus', header: 'From', dimRepeats: true },
    { key: 'toStatus', header: 'To', dimRepeats: true },
    { key: 'executionStatus', header: 'Outcome', dimRepeats: true },
    { key: 'reason', header: 'Reason' }
  ];
</script>

<Page title="Events">

  <div class="panel fill">
    <FilterBar>
      <span class="dim">
        {#if $scope}
          Showing latest events for namespace <span class="mono">{$scope}</span>.
        {:else}
          Showing latest events across all namespaces.
        {/if}
        Scroll down for history; new events surface as a jump-to-top pill.
        {displayFormatter.zoneNote($now)}.
      </span>
      {#if newCount > 0}
        <button class="chip feed-new" onclick={() => grid?.refresh()}>{newLabel} new — jump to top</button>
      {/if}
    </FilterBar>

    {#snippet timeCell(evt)}
      <TimeCell value={evt.createdAtUtc} />
    {/snippet}
    {#snippet jobCell(evt)}
      {#if evt.jobRef}
        <JobRef value={evt.jobRef} href={routes.job(evt.jobRef, { namespace: evt.jobNamespace })} />
      {:else}
        <span class="dim">-</span>
      {/if}
    {/snippet}
    {#snippet actorCell(evt)}
      {evt.actorCode}{#if evt.actorKey}<span class="dim"> · {evt.actorKey}</span>{/if}
    {/snippet}
    {#snippet attemptCell(evt)}{evt.executionNumber == null ? '—' : displayFormatter.number(evt.executionNumber)}{/snippet}
    {#snippet reasonCell(evt)}{evt.reasonMessage ?? evt.reasonCode ?? evt.detailText ?? '—'}{/snippet}

    <ActaGrid
      rowKey={(event) => event.jobEventId}
      bind:this={grid}
      endpoint="events"
      mode="feed"
      {columns}
      filters={() => ({ jobNamespace: $scope })}
      onNewCount={(count, label) => { newCount = count; newLabel = label; }}
      loadingText="Loading events..."
      emptyText="No events retained for this scope."
      cells={{ createdAtUtc: timeCell, job: jobCell, executionNumber: attemptCell, actor: actorCell, reason: reasonCell }} />
  </div>
</Page>
