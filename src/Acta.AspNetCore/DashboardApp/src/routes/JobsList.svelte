<script lang="ts">
  import Icon from '../components/Icon.svelte';
  import JobRef from '../components/JobRef.svelte';
  import { get } from 'svelte/store';
  import { api } from '../api';
  import { hashParams, updateHashParams } from '../router';
  import { scope, setScope } from '../scope';
  import Page from '../components/Page.svelte';
  import ActaGrid from '../components/grid/ActaGrid.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import FilterBar from '../components/FilterBar.svelte';
  import ActiveFilters from '../components/ActiveFilters.svelte';
  import { createUrlFilters } from '../urlFilters.ts';
  import { parseTagTokens } from '../lib/tagTokens.ts';
  import { routes } from '../routes.ts';
  import type { ColumnDef } from '../components/grid/types.ts';
  import { displayFormatter } from '../format.ts';

  interface JobRow {
    jobRef: string;
    jobName: string;
    jobNamespace: string;
    status: string;
    tenantId: number | null;
    createdAtUtc: string;
    nextRunAtUtc: string | null;
    executionNumber: number;
    failureCount: number;
  }

  const statuses = ['', 'Paused', 'Suspended', 'Ready', 'Dispatched', 'Executing', 'Done', 'Failed', 'Cancelled'];
  const initial = hashParams();
  const filters = createUrlFilters(
    { status: 'status', jobName: 'jobName', correlationKey: 'correlationKey', tenantId: 'tenantId', tags: 'tags' },
    { status: '', jobName: '', correlationKey: '', tenantId: '', tags: '' }
  );
  let jump = $state('');
  let jumpError = $state('');
  let showMore = $state(!!(initial.get('correlationKey') || initial.get('tags')));

  const columns: ColumnDef<JobRow>[] = [
    { key: 'job', header: 'Job' },
    { key: 'status', header: 'Status' },
    { key: 'jobNamespace', header: 'Namespace', class: 'mobile-hide', dimRepeats: true },
    { key: 'tenantId', header: 'Tenant', class: 'mobile-hide', align: 'right' },
    { key: 'createdAtUtc', header: 'Age' },
    { key: 'nextRunAtUtc', header: 'Next run', class: 'mobile-hide' },
    { key: 'attempts', header: 'Attempts', align: 'right' }
  ];

  function clearTenant() {
    filters.patch({ tenantId: '' });
  }

  // Active-filter chips: the namespace scope reads as a filter here (it narrows the list) and each
  // chip removes exactly its own filter. jobName only applies within a namespace scope.
  let activeChips = $derived(
    [
      $scope ? { label: 'Namespace', value: $scope, onRemove: () => setScope('') } : null,
      $filters.status ? { label: 'Status', value: $filters.status, onRemove: () => filters.patch({ status: '' }) } : null,
      $scope && $filters.jobName.trim() ? { label: 'Job name', value: $filters.jobName.trim(), onRemove: () => filters.patch({ jobName: '' }) } : null,
      $filters.correlationKey.trim() ? { label: 'Correlation', value: $filters.correlationKey.trim(), onRemove: () => filters.patch({ correlationKey: '' }) } : null,
      $filters.tenantId ? { label: 'Tenant', value: $filters.tenantId, onRemove: clearTenant } : null,
      $filters.tags.trim() ? { label: 'Tags', value: $filters.tags.trim(), onRemove: () => filters.patch({ tags: '' }) } : null
    ].filter((chip): chip is { label: string; value: string; onRemove: () => void } => chip !== null)
  );

  function clearAllFilters() {
    filters.clear();
    setScope('');
  }

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
    if (/^job_/i.test(value)) {
      location.hash = routes.job(value, { namespace: get(scope) });
      return;
    }
    // Anything else is an deduplication key, resolved within the selected namespace scope.
    if (!get(scope)) {
      jumpError = 'Select a namespace scope to look up an deduplication key.';
      return;
    }
    try {
      const job = await api<{ jobRef: string }>('jobs/by-key', { jobNamespace: get(scope), deduplicationKey: value });
      location.hash = routes.job(job.jobRef, { namespace: get(scope) });
    } catch (e) {
      jumpError = e instanceof Error ? e.message : String(e);
    }
  }
</script>

<Page title="Jobs">
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
          placeholder={$scope ? 'job name' : 'needs a namespace scope'}
          disabled={!$scope}
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
      {#if $filters.tenantId}
        <button class="chip" onclick={clearTenant} title="Clear tenant filter" aria-label="Clear tenant filter">Tenant: {$filters.tenantId} <Icon name="x" /></button>
      {/if}
    </FilterBar>

    <ActiveFilters chips={activeChips} onClearAll={clearAllFilters} />

    {#snippet jobCell(job: JobRow)}
      <a href={routes.job(job.jobRef, { namespace: $scope })}>
        {job.jobName} {#if job.jobName?.startsWith('sys.')}<span class="badge system">system</span>{/if}
        <JobRef value={job.jobRef} />
      </a>
    {/snippet}
    {#snippet statusCell(job: JobRow)}<StatusBadge status={job.status} />{/snippet}
    {#snippet ageCell(job: JobRow)}<RelativeTime value={job.createdAtUtc} />{/snippet}
    {#snippet nextRunCell(job: JobRow)}<RelativeTime value={job.nextRunAtUtc} />{/snippet}
    {#snippet attemptsCell(job: JobRow)}{displayFormatter.number(job.executionNumber)}{job.failureCount > 0 ? ' (' + displayFormatter.number(job.failureCount) + ' failed)' : ''}{/snippet}

    <ActaGrid
      rowKey={(job: JobRow) => job.jobRef}
      endpoint="jobs"
      mobileCards={true}
      {columns}
      filters={() => ({
        status: $filters.status,
        jobName: $scope ? $filters.jobName.trim() : '',
        correlationKey: $filters.correlationKey.trim(),
        jobNamespace: $scope,
        tenantId: $filters.tenantId,
        tag: parseTagTokens($filters.tags)
      })}
      countMode="on-demand"
      initialPageSize={Number(initial.get('pageSize') ?? '50') || 50}
      onPageSizeChange={(size) => updateHashParams({ pageSize: String(size) })}
      loadingText="Loading jobs..."
      emptyText={activeChips.length > 0
        ? 'No jobs match these ' + displayFormatter.number(activeChips.length) + ' filters. Remove a chip above to widen the search.'
        : 'No jobs yet in this view.'}
      cells={{ job: jobCell, status: statusCell, createdAtUtc: ageCell, nextRunAtUtc: nextRunCell, attempts: attemptsCell }} />
  </div>
</Page>
