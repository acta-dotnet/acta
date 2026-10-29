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

  // Kebab codes the backend's events filter accepts (EventCode, and the EntityCode kinds an event can
  // have as its subject), less the never-written 'unspecified'.
  // Hardcoded rather than fetched: the code families are compile-time enums with no list endpoint.
  const EVENT_CODES = [
    'tenant.suspended', 'tenant.resumed', 'tenant.updated',
    'namespace.suspended', 'namespace.resumed', 'namespace.updated',
    'definition.overrides-updated', 'definition.retired',
    'job.execution-succeeded', 'job.execution-failed', 'job.execution-orphaned', 'job.execution-yielded', 'job.failed',
    'job.suspended', 'job.rescheduled', 'job.cancelled', 'job.paused', 'job.resumed',
    'job.restarted', 'job.reprioritized', 'job.purged', 'job.input-amended',
    'job.signal-raised', 'job.state-reset', 'job.note-recorded',
    'schedule.paused', 'schedule.resumed', 'schedule.pause-expired', 'schedule.triggered',
    'schedule.admitted', 'schedule.skipped', 'schedule.retimed',
    'calendar.draft-saved', 'calendar.published', 'calendar.archived',
    'worker.started', 'worker.stopped', 'worker.died',
    'alert.acknowledged', 'alert.resolved',
    'outbox.requeued', 'outbox.discarded'
  ];
  const ENTITY_KINDS = ['alert', 'calendar', 'definition', 'job', 'namespace', 'tenant', 'worker'];

  // tenantName and jobRef arrive only by link (a tenant's or a job's page); the bar edits the rest.
  const filters = createUrlFilters(
    { eventCode: 'eventCode', entityKind: 'entityKind', tenantName: 'tenantName', jobRef: 'jobRef', createdFrom: 'from', createdTo: 'to' },
    { eventCode: '', entityKind: '', tenantName: '', jobRef: '', createdFrom: '', createdTo: '' }
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
    entityKind: $filters.entityKind,
    tenantName: $filters.tenantName,
    jobRef: $filters.jobRef,
    createdFromUtc: toUtcIso($filters.createdFrom),
    createdToUtc: toUtcIso($filters.createdTo)
  });

  const capabilities = createQuery(() => capabilitiesQuery());

  let copySql = $derived(
    eventsListSql(
      {
        namespace: $scope,
        eventCode: $filters.eventCode,
        entityKind: $filters.entityKind,
        jobRef: $filters.jobRef,
        tenantName: $filters.tenantName,
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
      $filters.entityKind ? { label: 'Subject', value: $filters.entityKind, onRemove: () => filters.patch({ entityKind: '' }) } : null,
      $filters.jobRef ? { label: 'Job', value: $filters.jobRef, onRemove: () => filters.patch({ jobRef: '' }) } : null,
      $filters.tenantName ? { label: 'Tenant', value: $filters.tenantName, onRemove: () => filters.patch({ tenantName: '' }) } : null,
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
    { key: 'subject', header: 'Subject', class: 'shrink' },
    { key: 'jobName', header: 'Name', dimRepeats: true },
    { key: 'jobNamespace', header: 'Namespace', dimRepeats: true },
    { key: 'executionNumber', header: 'Attempt', align: 'right' },
    { key: 'message', header: 'Message' },
    { key: 'detailsText', header: 'Details', class: 'mono' }
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
        Subject
        <select value={$filters.entityKind} onchange={(event) => filters.patch({ entityKind: event.currentTarget.value })}>
          <option value="">Any</option>
          {#each ENTITY_KINDS as kind}<option value={kind}>{kind}</option>{/each}
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
    <!-- The subject links where the row carries its ref; otherwise its kind and numeric id name it. -->
    {#snippet subjectCell(evt)}
      {#if evt.entityKind === 'job' && evt.jobRef}
        <JobRef value={evt.jobRef} href={routes.job(evt.jobRef, { namespace: evt.jobNamespace })} copy />
      {:else if evt.entityKind === 'worker' && evt.workerRef}
        <JobRef value={evt.workerRef} href={routes.worker(evt.workerRef, { namespace: evt.jobNamespace })} />
      {:else}
        <span class="dim">{evt.entityKind} #{displayFormatter.number(evt.entityId)}</span>
      {/if}
    {/snippet}
    {#snippet nameCell(evt)}
      {#if evt.jobName}<a href={routes.jobs({ jobName: evt.jobName, namespace: evt.jobNamespace })}>{evt.jobName}</a>{:else}<span class="dim">·</span>{/if}
    {/snippet}
    {#snippet attemptCell(evt)}{evt.executionNumber == null ? '·' : displayFormatter.number(evt.executionNumber)}{/snippet}

    <ActaGrid
      rowKey={(event) => event.eventId}
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
      cells={{ createdAtUtc: timeCell, subject: subjectCell, jobName: nameCell, executionNumber: attemptCell }} />
  </div>
</Page>

<style>
  .events-help { flex-basis: 100%; }
</style>
