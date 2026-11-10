<script lang="ts">
  import Icon from '../components/Icon.svelte';
  import JobRef from '../components/JobRef.svelte';
  import { get } from 'svelte/store';
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { api } from '../api';
  import { capabilitiesQuery, can, namespaceNamesQuery, onlyNamespace, Permission } from '../query.ts';
  import { hashParams, updateHashParams } from '../router';
  import { scope, setScope } from '../scope';
  import Page from '../components/Page.svelte';
  import ActaGrid from '../components/grid/ActaGrid.svelte';
  import StateView from '../components/StateView.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import FilterBar from '../components/FilterBar.svelte';
  import ActiveFilters from '../components/ActiveFilters.svelte';
  import { createUrlFilters } from '../urlFilters.ts';
  import { parseTagTokens } from '../lib/tagTokens.ts';
  import { jobsListSql, COPY_SQL_TITLE } from '../lib/copyAsSql.ts';
  import CopyButton from '../components/CopyButton.svelte';
  import { routes } from '../routes.ts';
  import type { ColumnDef } from '../components/grid/types.ts';
  import { consecutiveFailuresText, displayFormatter, JOB_STATUSES, TERMINAL_STATUSES } from '../format.ts';

  interface JobRow {
    jobRef: string;
    jobName: string;
    jobNamespace: string;
    status: string;
    tenantName: string | null;
    createdAtUtc: string;
    nextRunAtUtc: string | null;
    executionNumber: number;
    failureCount: number;
  }

  const statuses = ['', ...JOB_STATUSES];
  const initial = hashParams();
  const filters = createUrlFilters(
    { status: 'status', jobName: 'jobName', correlationKey: 'correlationKey', tenantName: 'tenantName', lane: 'lane', tags: 'tags', view: 'view' },
    { status: '', jobName: '', correlationKey: '', tenantName: '', lane: '', tags: '', view: '' }
  );
  // Nav-alias views: the sidebar's Permanent jobs / Job history entries are this screen with a
  // baked-in view filter (permanent slot rows / terminal rows).
  let view = $derived($filters.view === 'permanent' || $filters.view === 'history' ? $filters.view : '');
  let pageTitle = $derived(view === 'permanent' ? 'Permanent jobs' : view === 'history' ? 'Job history' : 'Jobs');
  function clearView() {
    // patch pushes a new hash, so hashchange fires and the sidebar's alias highlight follows;
    // the other filters survive, keeping the chip contract of removing exactly its own filter.
    filters.patch({ view: '' });
  }
  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(can(capabilities.data, Permission.enqueue));
  // A job name or a deduplication key is looked up within one namespace: the scope, or the only one there is.
  const queryClient = useQueryClient();
  const namespaceNames = createQuery(() => namespaceNamesQuery());
  let lookupNamespace = $derived($scope || onlyNamespace(namespaceNames.data));
  let jobName = $derived($filters.jobName.trim());
  // Until the namespace is known, a job name filter lists nothing rather than every job.
  let jobNameWaits = $derived(jobName !== '' && !lookupNamespace);
  let jump = $state('');
  let jumpError = $state('');
  let showMore = $state(!!(initial.get('correlationKey') || initial.get('tags')));

  const columns: ColumnDef<JobRow>[] = [
    { key: 'jobRef', header: 'Ref', class: 'shrink' },
    { key: 'job', header: 'Job', class: 'grow' },
    { key: 'status', header: 'Status' },
    { key: 'jobNamespace', header: 'Namespace', class: 'mobile-hide', dimRepeats: true },
    { key: 'tenant', header: 'Tenant', class: 'mobile-hide' },
    { key: 'createdAtUtc', header: 'Age' },
    { key: 'nextRunAtUtc', header: 'Next run', class: 'mobile-hide' },
    { key: 'attempts', header: 'Attempts', align: 'right', class: 'mobile-wide' }
  ];

  function clearTenant() {
    filters.patch({ tenantName: '' });
  }

  // Active-filter chips: the namespace scope reads as a filter here (it narrows the list) and each
  // chip removes exactly its own filter.
  let activeChips = $derived(
    [
      view ? { label: 'View', value: view === 'permanent' ? 'Permanent' : 'History', onRemove: clearView } : null,
      $scope ? { label: 'Namespace', value: $scope, onRemove: () => setScope('') } : null,
      $filters.status ? { label: 'Status', value: $filters.status, onRemove: () => filters.patch({ status: '' }) } : null,
      jobName ? { label: 'Job name', value: jobName, onRemove: () => filters.patch({ jobName: '' }) } : null,
      $filters.correlationKey.trim() ? { label: 'Correlation', value: $filters.correlationKey.trim(), onRemove: () => filters.patch({ correlationKey: '' }) } : null,
      $filters.tenantName ? { label: 'Tenant', value: $filters.tenantName, onRemove: clearTenant } : null,
      $filters.lane ? { label: 'Lane', value: $filters.lane, onRemove: () => filters.patch({ lane: '' }) } : null,
      $filters.tags.trim() ? { label: 'Tags', value: $filters.tags.trim(), onRemove: () => filters.patch({ tags: '' }) } : null
    ].filter((chip): chip is { label: string; value: string; onRemove: () => void } => chip !== null)
  );

  function clearAllFilters() {
    filters.clear();
    setScope('');
  }

  // The applied filters as ad-hoc SQL against the jobs operator view (mirrors the grid filters below;
  // tags are omitted, they are not a jobs_view column, and a job name with no namespace yet matches in every one).
  let copySql = $derived(
    jobsListSql(
      {
        namespace: jobName ? lookupNamespace : $scope,
        status: $filters.status,
        jobName,
        correlationKey: $filters.correlationKey.trim(),
        tenantName: $filters.tenantName,
        lane: $filters.lane
      },
      { provider: capabilities.data?.provider, schema: capabilities.data?.schema }
    )
  );

  async function quickJump() {
    jumpError = '';
    const value = jump.trim();
    if (!value) {
      return;
    }
    // Explicit internal-id lookup: 'id:123' or '#123' (debug/admin). Only resolves when the host
    // enabled numeric id lookup; otherwise the detail page reports the job as not found.
    const idMatch = value.match(/^(?:id:|#)(\d+)$/i);
    if (idMatch) {
      location.hash = routes.job('id:' + idMatch[1], { namespace: get(scope) });
      return;
    }
    // Public job ref.
    if (value.startsWith('job_')) {
      location.hash = routes.job(value, { namespace: get(scope) });
      return;
    }
    // Anything else is a deduplication key, resolved within the lookup namespace. Without a scope it waits for the
    // namespaces, then reads the scope again: one picked meanwhile is where to look, even when that read failed.
    try {
      let names: string[] = [];
      if (!get(scope)) {
        try {
          names = await queryClient.fetchQuery(namespaceNamesQuery());
        } catch (e) {
          if (!get(scope)) throw e;
        }
      }
      const jobNamespace = get(scope) || onlyNamespace(names);
      if (!jobNamespace) {
        jumpError = 'Select a namespace scope to look up a deduplication key.';
        return;
      }
      const job = await api<{ jobRef: string }>('jobs/by-deduplication-key', { jobNamespace, deduplicationKey: value });
      location.hash = routes.job(job.jobRef, { namespace: get(scope) });
    } catch (e) {
      jumpError = e instanceof Error ? e.message : String(e);
    }
  }
</script>

<Page title={pageTitle}>
  {#snippet actions()}
    {#if canControlNow}
      <a class="primary-action" href={routes.enqueue({ namespace: $scope })}><Icon name="plus" />Enqueue job</a>
    {/if}
  {/snippet}
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
        Job name
        <input
          placeholder={lookupNamespace ? 'job name' : 'needs a namespace scope'}
          disabled={!lookupNamespace}
          value={$filters.jobName}
          onchange={(event) => filters.patch({ jobName: event.currentTarget.value.trim() })} />
      </label>
      {#if showMore}
        <label>
          Correlation id
          <input
            placeholder="trace / request / order id"
            value={$filters.correlationKey}
            onchange={(event) => filters.patch({ correlationKey: event.currentTarget.value.trim() })} />
        </label>
        <label>
          Tags
          <input
            placeholder="env:prod team"
            value={$filters.tags}
            onchange={(event) => filters.patch({ tags: event.currentTarget.value.trim() })} />
        </label>
      {/if}
      <label>
        Jump to
        <input
          placeholder="job ref, deduplication key, or id:123"
          bind:value={jump}
          onkeydown={(e) => e.key === 'Enter' && quickJump()} />
      </label>
      {#if jumpError}<span class="filter-error" role="alert">{jumpError}</span>{/if}
      <button type="button" class="chip" aria-expanded={showMore} onclick={() => (showMore = !showMore)}>
        {showMore ? 'Fewer filters' : 'More filters'}
      </button>
      {#if $filters.tenantName}
        <button class="chip" onclick={clearTenant} title="Clear tenant filter" aria-label="Clear tenant filter">Tenant: {$filters.tenantName} <Icon name="x" /></button>
      {/if}
      <CopyButton value={copySql} label="Copy SQL" title={COPY_SQL_TITLE} />
    </FilterBar>

    <ActiveFilters chips={activeChips} onClearAll={clearAllFilters} />

    {#snippet refCell(job: JobRow)}
      <JobRef value={job.jobRef} href={routes.job(job.jobRef, { namespace: $scope })} copy compact />
    {/snippet}
    {#snippet jobCell(job: JobRow)}
      <a href={routes.job(job.jobRef, { namespace: $scope })} title={job.jobName}>
        {job.jobName} {#if job.jobName?.startsWith('sys.')}<span class="badge held" title="Framework (sys.-prefixed) job">system</span>{/if}
      </a>
    {/snippet}
    {#snippet statusCell(job: JobRow)}<StatusBadge status={job.status} />{/snippet}
    {#snippet tenantCell(job: JobRow)}
      {#if job.tenantName}<a href={routes.tenant(job.tenantName, { namespace: $scope })} class="mono">{job.tenantName}</a>{:else}<span class="dim">·</span>{/if}
    {/snippet}
    {#snippet ageCell(job: JobRow)}<RelativeTime value={job.createdAtUtc} />{/snippet}
    {#snippet nextRunCell(job: JobRow)}<RelativeTime value={TERMINAL_STATUSES.includes(job.status) ? null : job.nextRunAtUtc} />{/snippet}
    {#snippet attemptsCell(job: JobRow)}{displayFormatter.number(job.executionNumber)}{job.failureCount > 0 ? ' (' + consecutiveFailuresText(job.failureCount) + ')' : ''}{/snippet}

    {#if jobNameWaits}
      <StateView
        loading={namespaceNames.isPending}
        error={namespaceNames.error?.message ?? null}
        loadingText="Loading jobs..."
        emptyText="Select a namespace scope to filter by job name, or remove the job name filter."
        onRetry={() => namespaceNames.refetch()} />
    {:else}
      <ActaGrid
        rowKey={(job: JobRow) => job.jobRef}
        endpoint="jobs"
        mobileCards={true}
        {columns}
        filters={() => ({
          status: $filters.status,
          jobName,
          correlationKey: $filters.correlationKey.trim(),
          jobNamespace: jobName ? lookupNamespace : $scope,
          tenantName: $filters.tenantName,
          lane: $filters.lane,
          tag: parseTagTokens($filters.tags),
          terminalOnly: view === 'history' ? 'true' : '',
          kind: view === 'permanent' ? 'permanent' : ''
        })}
        countMode="on-demand"
        initialPageSize={Number(initial.get('pageSize') ?? '50') || 50}
        onPageSizeChange={(size) => updateHashParams({ pageSize: String(size) })}
        loadingText="Loading jobs..."
        emptyText={activeChips.length > 0
          ? 'No jobs match these ' + displayFormatter.number(activeChips.length) + ' filters. Remove a chip above to widen the search.'
          : 'No jobs yet. Mark a handler method with [Job("name")] and call jobs.EnqueueAsync(input); the row appears here the moment the enqueue commits.'}
        cells={{ jobRef: refCell, job: jobCell, status: statusCell, tenant: tenantCell, createdAtUtc: ageCell, nextRunAtUtc: nextRunCell, attempts: attemptsCell }}
        rowClass={(job: JobRow) => (job.status === 'failed' ? 'trouble' : job.status === 'paused' ? 'held' : '')} />
    {/if}
  </div>
</Page>
