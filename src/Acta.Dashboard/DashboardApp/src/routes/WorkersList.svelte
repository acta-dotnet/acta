<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { api, type NamespaceListItem, type Paged } from '../api.ts';
  import { keys } from '../query.ts';
  import { readAllPages } from '../lib/pagedWalk.ts';
  import { livePaused, listRefetchInterval } from '../polling';
  import { hashParams, updateHashParams } from '../router';
  import { scope, setScope } from '../scope';
  import Page from '../components/Page.svelte';
  import ActaGrid from '../components/grid/ActaGrid.svelte';
  import WorkerStatus from '../components/WorkerStatus.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import JobRef from '../components/JobRef.svelte';
  import FilterBar from '../components/FilterBar.svelte';
  import ActiveFilters from '../components/ActiveFilters.svelte';
  import { createUrlFilters } from '../urlFilters.ts';
  import { routes } from '../routes.ts';
  import type { ColumnDef } from '../components/grid/types.ts';
  import { displayFormatter } from '../format.ts';

  interface WorkerRow {
    workerRef: string;
    status: string;
    jobNamespace: string;
    hostName: string;
    deploymentVersion: string;
    engineVersion: string | null;
    dotnetVersion: string | null;
    processId: number | null;
    maxConcurrentExecutors: number;
    lastSeenAtUtc: string;
    stale: boolean;
    startedAtUtc: string;
  }

  const statuses = ['', 'active', 'draining', 'stopped', 'dead'];
  const initial = hashParams();
  const filters = createUrlFilters({ status: 'status' }, { status: '' });

  let activeChips = $derived(
    [
      $scope ? { label: 'Namespace', value: $scope, onRemove: () => setScope('') } : null,
      $filters.status ? { label: 'Status', value: $filters.status, onRemove: () => filters.patch({ status: '' }) } : null
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
    { key: 'hostName', header: 'Host' },
    { key: 'deploymentVersion', header: 'Version', class: 'mobile-hide' },
    { key: 'engineVersion', header: 'Engine', class: 'mobile-hide' },
    { key: 'dotnetVersion', header: '.NET', class: 'mono mobile-hide' },
    { key: 'processId', header: 'PID', class: 'mono mobile-hide', align: 'right' },
    { key: 'maxConcurrentExecutors', header: 'Executors', class: 'mono mobile-hide', align: 'right' },
    { key: 'lastSeenAtUtc', header: 'Last heartbeat' },
    { key: 'startedAtUtc', header: 'Started', class: 'mobile-hide' }
  ];

  // Each namespace's current deployed version, to show a rolling deploy's progress: a live worker on another
  // version is an earlier build still draining, or one kept after a rollback.
  const currentVersions = createQuery(() => ({
    queryKey: keys.list('worker-namespace-versions', {}),
    queryFn: async ({ signal }: { signal: AbortSignal }) => {
      const all = await readAllPages((cursor) => api<Paged<NamespaceListItem>>('namespaces', { pageSize: 100, cursor }, { signal }));
      return new Map(all.map((item) => [item.jobNamespace, item.deploymentVersion]));
    },
    // Refreshed with the rows it is compared to: a deploy that lands while the page is open moves the current version.
    refetchInterval: $livePaused ? false : listRefetchInterval
  }));

  function currentVersionOf(worker: WorkerRow): string | null {
    return currentVersions.data?.get(worker.jobNamespace) ?? null;
  }

  function isNotCurrent(worker: WorkerRow): boolean {
    const current = currentVersionOf(worker);
    return current !== null && (worker.status === 'active' || worker.status === 'draining') && worker.deploymentVersion !== current;
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
    </FilterBar>

    <ActiveFilters chips={activeChips} onClearAll={clearAllFilters} />

    {#snippet workerCell(w: WorkerRow)}
      <JobRef value={w.workerRef} href={routes.worker(w.workerRef, { namespace: $scope })} copy />
    {/snippet}
    {#snippet statusCell(w: WorkerRow)}<WorkerStatus status={w.status} stale={w.stale} />{/snippet}
    {#snippet versionCell(w: WorkerRow)}{w.deploymentVersion}{#if isNotCurrent(w)}<span class="not-current" title={'This namespace is deployed at ' + currentVersionOf(w)}>not current</span>{/if}{/snippet}
    {#snippet lastSeenCell(w: WorkerRow)}<RelativeTime value={w.lastSeenAtUtc} />{/snippet}
    {#snippet startedCell(w: WorkerRow)}<RelativeTime value={w.startedAtUtc} />{/snippet}
    {#snippet executorsCell(w: WorkerRow)}{displayFormatter.number(w.maxConcurrentExecutors)}{/snippet}

    <ActaGrid
      rowKey={(worker: WorkerRow) => worker.workerRef}
      endpoint="workers"
      mobileCards={true}
      {columns}
      filters={() => ({ status: $filters.status, jobNamespace: $scope })}
      includeTotal={true}
      initialPageSize={Number(initial.get('pageSize') ?? '50') || 50}
      onPageSizeChange={(size) => updateHashParams({ pageSize: String(size) })}
      loadingText="Loading workers..."
      emptyText={activeChips.length > 0
        ? 'No workers match these ' + displayFormatter.number(activeChips.length) + ' filters.'
        : 'No workers registered here. Confirm a worker process is running and configured for this namespace.'}
      cells={{ workerRef: workerCell, status: statusCell, deploymentVersion: versionCell, maxConcurrentExecutors: executorsCell, lastSeenAtUtc: lastSeenCell, startedAtUtc: startedCell }}
      rowClass={(w: WorkerRow) => (w.status === 'dead' ? 'trouble' : w.stale ? 'stale' : '')} />
  </div>
</Page>

<style>
  /* Quiet on purpose: a worker of an earlier build is the normal middle of a rolling deploy, not a fault. */
  .not-current {
    margin-left: 8px;
    color: var(--muted);
    font-size: var(--text-sm);
    white-space: nowrap;
  }
</style>
