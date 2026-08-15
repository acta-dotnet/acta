<script lang="ts">
  import { hashParams, updateHashParams } from '../router';
  import { scope, setScope } from '../scope';
  import Page from '../components/Page.svelte';
  import ActaGrid from '../components/grid/ActaGrid.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import JobRef from '../components/JobRef.svelte';
  import FilterBar from '../components/FilterBar.svelte';
  import ActiveFilters from '../components/ActiveFilters.svelte';
  import { now } from '../time';
  import { createUrlFilters } from '../urlFilters.ts';
  import { parseTagTokens } from '../lib/tagTokens.ts';
  import { routes } from '../routes.ts';
  import type { ColumnDef } from '../components/grid/types.ts';
  import { displayFormatter } from '../format.ts';

  interface WorkerRow {
    workerRef: string;
    status: string;
    jobNamespace: string;
    host: string;
    deploymentVersion: string;
    engineVersion: string | null;
    dotnetVersion: string | null;
    processId: number | null;
    maxConcurrency: number;
    lastHeartbeatAtUtc: string;
    startedAtUtc: string;
  }

  const statuses = ['', 'Active', 'Draining', 'Stopped', 'Dead'];
  const initial = hashParams();
  const filters = createUrlFilters({ status: 'status', tags: 'tags' }, { status: '', tags: '' });

  let activeChips = $derived(
    [
      $scope ? { label: 'Namespace', value: $scope, onRemove: () => setScope('') } : null,
      $filters.status ? { label: 'Status', value: $filters.status, onRemove: () => filters.patch({ status: '' }) } : null,
      $filters.tags.trim() ? { label: 'Tags', value: $filters.tags.trim(), onRemove: () => filters.patch({ tags: '' }) } : null
    ].filter((chip): chip is { label: string; value: string; onRemove: () => void } => chip !== null)
  );
  function clearAllFilters() {
    filters.clear();
    setScope('');
  }

  const columns: ColumnDef<WorkerRow>[] = [
    { key: 'workerRef', header: 'Worker' },
    { key: 'status', header: 'Status' },
    { key: 'jobNamespace', header: 'Namespace', dimRepeats: true },
    { key: 'host', header: 'Host' },
    { key: 'deploymentVersion', header: 'Version', class: 'mobile-hide' },
    { key: 'engineVersion', header: 'Engine', class: 'mobile-hide' },
    { key: 'dotnetVersion', header: '.NET', class: 'mono mobile-hide' },
    { key: 'processId', header: 'PID', class: 'mono mobile-hide', align: 'right' },
    { key: 'maxConcurrency', header: 'Concurrency', class: 'mono mobile-hide', align: 'right' },
    { key: 'lastHeartbeatAtUtc', header: 'Last heartbeat' },
    { key: 'startedAtUtc', header: 'Started', class: 'mobile-hide' }
  ];

  function isStale(worker: WorkerRow, nowMs: number): boolean {
    return (
      (worker.status === 'active' || worker.status === 'draining') &&
      nowMs - new Date(worker.lastHeartbeatAtUtc).getTime() > 180000
    );
  }
</script>

<Page title="Workers">

  <div class="panel fill">
    <FilterBar>
      <label>
        Status
        <select value={$filters.status} onchange={(event) => filters.patch({ status: event.currentTarget.value })}>
          {#each statuses as s}
            <option value={s}>{s === '' ? 'Any' : s}</option>
          {/each}
        </select>
      </label>
      <label>
        Tags
        <input
          placeholder="env:prod team"
          value={$filters.tags}
          onchange={(event) => filters.patch({ tags: event.currentTarget.value.trim() })} />
      </label>
    </FilterBar>

    <ActiveFilters chips={activeChips} onClearAll={clearAllFilters} />

    {#snippet workerCell(w: WorkerRow)}
      <JobRef value={w.workerRef} href={routes.worker(w.workerRef, { namespace: $scope })} copy />
    {/snippet}
    {#snippet statusCell(w: WorkerRow)}<StatusBadge status={w.status} />{/snippet}
    {#snippet lastSeenCell(w: WorkerRow)}<RelativeTime value={w.lastHeartbeatAtUtc} />{/snippet}
    {#snippet startedCell(w: WorkerRow)}<RelativeTime value={w.startedAtUtc} />{/snippet}
    {#snippet concurrencyCell(w: WorkerRow)}{displayFormatter.number(w.maxConcurrency)}{/snippet}

    <ActaGrid
      rowKey={(worker: WorkerRow) => worker.workerRef}
      endpoint="workers"
      mobileCards={true}
      {columns}
      filters={() => ({ status: $filters.status, jobNamespace: $scope, tag: parseTagTokens($filters.tags) })}
      includeTotal={true}
      initialPageSize={Number(initial.get('pageSize') ?? '50') || 50}
      onPageSizeChange={(size) => updateHashParams({ pageSize: String(size) })}
      loadingText="Loading workers..."
      emptyText={activeChips.length > 0
        ? 'No workers match these ' + displayFormatter.number(activeChips.length) + ' filters.'
        : 'No workers registered here. Confirm a worker process is running and configured for this namespace.'}
      cells={{ workerRef: workerCell, status: statusCell, maxConcurrency: concurrencyCell, lastHeartbeatAtUtc: lastSeenCell, startedAtUtc: startedCell }}
      rowClass={(w: WorkerRow) => (w.status === 'dead' ? 'trouble' : isStale(w, $now) ? 'stale' : '')} />
  </div>
</Page>
