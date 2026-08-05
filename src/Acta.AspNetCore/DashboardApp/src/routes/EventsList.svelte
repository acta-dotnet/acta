<script>
  import { createQuery } from '@tanstack/svelte-query';
  import { capabilitiesQuery } from '../query.ts';
  import { scope, setScope } from '../scope';
  import { displayFormatter } from '../format';
  import { now } from '../time';
  import Page from '../components/Page.svelte';
  import ActaGrid from '../components/grid/ActaGrid.svelte';
  import TimeCell from '../components/TimeCell.svelte';
  import FilterBar from '../components/FilterBar.svelte';
  import ActiveFilters from '../components/ActiveFilters.svelte';
  import CopyButton from '../components/CopyButton.svelte';
  import { createUrlFilters } from '../urlFilters.ts';
  import { eventsListSql, COPY_SQL_TITLE } from '../lib/copyAsSql.ts';
  import { routes } from '../routes.ts';
  import JobRef from '../components/JobRef.svelte';

  // Kebab codes the backend's events filter accepts (JobEventCode / JobActorCode / JobEventReasonCode).
  // Hardcoded rather than fetched: the code families are compile-time enums with no list endpoint.
  const EVENT_CODES = [
    'tenant.suspended', 'tenant.resumed', 'tenant.updated',
    'namespace.suspended', 'namespace.resumed', 'namespace.updated',
    'definition.overrides-updated',
    'job.execution-started', 'job.execution-finished', 'job.recurring-rolled-over',
    'job.suspended', 'job.rescheduled', 'job.cancelled', 'job.paused', 'job.resumed',
    'job.restarted', 'job.reprioritized', 'job.purged', 'job.input-amended',
    'job.signal-raised', 'job.state-reset',
    'schedule.paused', 'schedule.resumed', 'schedule.pause-expired', 'schedule.overrides-updated', 'schedule.triggered',
    'worker.started', 'worker.stopped', 'worker.dead',
    'alert.acknowledged', 'alert.resolved'
  ];
  const ACTOR_CODES = ['sys', 'operator', 'job', 'worker'];
  const REASON_CODES = [
    'job.unclassified', 'job.unhandled-exception', 'job.lease-expired', 'job.execution-timeout',
    'job.non-retryable-exception', 'job.deadline-exceeded', 'job.schedules-exhausted',
    'job.control-manual', 'job.parent-cancelled', 'job.definition-retired',
    'job.handler-rescheduled', 'job.handler-suspended', 'job.handler-failed', 'job.handler-cancelled', 'job.handler-paused',
    'job.signal-released', 'job.step-retry-scheduled', 'job.exclusive-key-held', 'job.step-interrupted',
    'worker.clean-shutdown', 'worker.heartbeat-stale'
  ];

  const filters = createUrlFilters(
    { eventCode: 'eventCode', actorCode: 'actorCode', reasonCode: 'reasonCode', createdFrom: 'from', createdTo: 'to' },
    { eventCode: '', actorCode: '', reasonCode: '', createdFrom: '', createdTo: '' }
  );

  // The datetime-local inputs are read as UTC wall-clock (the dashboard's convention: operators enter
  // UTC, not browser-local), so append 'Z' before parsing to the wire ISO instant the backend filters on.
  function toUtcIso(value) {
    if (!value) return '';
    const date = new Date(value + 'Z');
    return Number.isNaN(date.getTime()) ? '' : date.toISOString();
  }

  let grid;
  let newCount = $state(0);
  let newLabel = $state('0');

  let requestFilters = $derived({
    jobNamespace: $scope,
    eventCode: $filters.eventCode,
    actorCode: $filters.actorCode,
    reasonCode: $filters.reasonCode,
    createdFromUtc: toUtcIso($filters.createdFrom),
    createdToUtc: toUtcIso($filters.createdTo)
  });

  const capabilities = createQuery(() => capabilitiesQuery());

  let copySql = $derived(
    eventsListSql(
      {
        namespace: $scope,
        eventCode: $filters.eventCode,
        actorCode: $filters.actorCode,
        reasonCode: $filters.reasonCode,
        createdFromUtc: toUtcIso($filters.createdFrom),
        createdToUtc: toUtcIso($filters.createdTo)
      },
      { provider: capabilities.data?.provider, schema: capabilities.data?.schema }
    )
  );

  // The namespace scope reads as a filter here (it narrows the feed); each chip removes only its own.
  let activeChips = $derived(
    [
      $scope ? { label: 'Namespace', value: $scope, onRemove: () => setScope('') } : null,
      $filters.eventCode ? { label: 'Event', value: $filters.eventCode, onRemove: () => filters.patch({ eventCode: '' }) } : null,
      $filters.actorCode ? { label: 'Actor', value: $filters.actorCode, onRemove: () => filters.patch({ actorCode: '' }) } : null,
      $filters.reasonCode ? { label: 'Reason', value: $filters.reasonCode, onRemove: () => filters.patch({ reasonCode: '' }) } : null,
      $filters.createdFrom ? { label: 'From (UTC)', value: $filters.createdFrom.replace('T', ' '), onRemove: () => filters.patch({ createdFrom: '' }) } : null,
      $filters.createdTo ? { label: 'To (UTC)', value: $filters.createdTo.replace('T', ' '), onRemove: () => filters.patch({ createdTo: '' }) } : null
    ].filter((chip) => chip !== null)
  );

  function clearAllFilters() {
    filters.clear();
    setScope('');
  }

  const columns = [
    { key: 'createdAtUtc', header: 'Time' },
    { key: 'eventCode', header: 'Event' },
    { key: 'job', header: 'Job', class: 'shrink' },
    { key: 'jobName', header: 'Name', dimRepeats: true },
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
      <label>
        Event
        <select value={$filters.eventCode} onchange={(event) => filters.patch({ eventCode: event.currentTarget.value })}>
          <option value="">Any</option>
          {#each EVENT_CODES as code}<option value={code}>{code}</option>{/each}
        </select>
      </label>
      <label>
        Actor
        <select value={$filters.actorCode} onchange={(event) => filters.patch({ actorCode: event.currentTarget.value })}>
          <option value="">Any</option>
          {#each ACTOR_CODES as code}<option value={code}>{code}</option>{/each}
        </select>
      </label>
      <label>
        Reason
        <select value={$filters.reasonCode} onchange={(event) => filters.patch({ reasonCode: event.currentTarget.value })}>
          <option value="">Any</option>
          {#each REASON_CODES as code}<option value={code}>{code}</option>{/each}
        </select>
      </label>
      <label>
        From (UTC)
        <input type="datetime-local" step="1" value={$filters.createdFrom} onchange={(event) => filters.patch({ createdFrom: event.currentTarget.value })} />
      </label>
      <label>
        To (UTC)
        <input type="datetime-local" step="1" value={$filters.createdTo} onchange={(event) => filters.patch({ createdTo: event.currentTarget.value })} />
      </label>
      <CopyButton value={copySql} label="Copy SQL" title={COPY_SQL_TITLE} />
      {#if newCount > 0}
        <button class="chip feed-new" onclick={() => grid?.refresh()}>{newLabel} new - jump to top</button>
      {/if}
      <span class="dim events-help">
        {#if $scope}
          Latest events for namespace <span class="mono">{$scope}</span>.
        {:else}
          Latest events across all namespaces.
        {/if}
        Scroll down for history; new events surface as a jump-to-top pill. {displayFormatter.zoneNote($now)}.
      </span>
    </FilterBar>

    <ActiveFilters chips={activeChips} onClearAll={clearAllFilters} />

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
    {#snippet nameCell(evt)}
      {#if evt.jobName}<a href={routes.jobs({ jobName: evt.jobName, namespace: evt.jobNamespace })}>{evt.jobName}</a>{:else}<span class="dim">-</span>{/if}
    {/snippet}
    {#snippet actorCell(evt)}
      {evt.actorCode}{#if evt.actorKey}<span class="dim"> · {evt.actorKey}</span>{/if}
    {/snippet}
    {#snippet attemptCell(evt)}{evt.executionNumber == null ? '-' : displayFormatter.number(evt.executionNumber)}{/snippet}
    {#snippet reasonCell(evt)}{evt.reasonMessage ?? evt.reasonCode ?? evt.detailText ?? '-'}{/snippet}

    <ActaGrid
      rowKey={(event) => event.jobEventId}
      bind:this={grid}
      endpoint="events"
      mode="feed"
      {columns}
      filters={() => requestFilters}
      onNewCount={(count, label) => { newCount = count; newLabel = label; }}
      loadingText="Loading events..."
      emptyText={activeChips.length > 0
        ? 'No events match these ' + displayFormatter.number(activeChips.length) + ' filters. Remove a chip above to widen the search.'
        : 'No events retained for this scope.'}
      cells={{ createdAtUtc: timeCell, job: jobCell, jobName: nameCell, executionNumber: attemptCell, actor: actorCell, reason: reasonCell }} />
  </div>
</Page>

<style>
  .events-help { flex-basis: 100%; }
</style>
