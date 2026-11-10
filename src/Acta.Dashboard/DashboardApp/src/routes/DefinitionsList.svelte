<script lang="ts">
  import { get } from 'svelte/store';
  import { hashParams, updateHashParams } from '../router';
  import { scope } from '../scope';
  import { displayFormatter } from '../format';
  import Icon from '../components/Icon.svelte';
  import Page from '../components/Page.svelte';
  import ActaGrid from '../components/grid/ActaGrid.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import FilterBar from '../components/FilterBar.svelte';
  import ActiveFilters from '../components/ActiveFilters.svelte';
  import { setScope } from '../scope';
  import { createUrlFilters } from '../urlFilters.ts';
  import { parseTagTokens } from '../lib/tagTokens.ts';
  import { routes } from '../routes.ts';
  import type { ColumnDef } from '../components/grid/types.ts';

  interface DefinitionRow {
    jobNamespace: string;
    status: string;
    jobName: string;
    inputTypeName: string;
    resultTypeName: string | null;
    priorityEffective: string;
    priorityOverride: string | null;
    maxAttemptsEffective: number;
    maxAttemptsOverride: number | null;
    concurrencyLimitEffective: number | null;
    concurrencyLimitOverride: number | null;
    rateLimitEffective: string | null;
    rateLimitOverride: string | null;
    rateName: string | null;
    modifiedAtUtc: string;
  }

  const initial = hashParams();
  // `name` is a server-side contains match on the definition name, independent of namespace.
  const filters = createUrlFilters({ name: 'name', status: 'status', tags: 'tags' }, { name: '', status: '', tags: '' });
  const statuses = ['', 'active', 'retired'];
  let activeChips = $derived(
    [
      $scope ? { label: 'Namespace', value: $scope, onRemove: () => setScope('') } : null,
      $filters.status ? { label: 'Status', value: $filters.status, onRemove: () => filters.patch({ status: '' }) } : null,
      $filters.name ? { label: 'Name', value: $filters.name, onRemove: () => filters.patch({ name: '' }) } : null,
      $filters.tags.trim() ? { label: 'Tags', value: $filters.tags.trim(), onRemove: () => filters.patch({ tags: '' }) } : null
    ].filter((chip): chip is { label: string; value: string; onRemove: () => void } => chip !== null)
  );

  const columns: ColumnDef<DefinitionRow>[] = [
    { key: 'jobName', header: 'Name', class: 'grow' },
    { key: 'jobNamespace', header: 'Namespace', dimRepeats: true },
    { key: 'status', header: 'Status' },
    { key: 'inputTypeName', header: 'Input', class: 'mono mobile-hide' },
    { key: 'resultTypeName', header: 'Result', class: 'mono mobile-hide' },
    { key: 'priority', header: 'Priority', align: 'right' },
    { key: 'maxAttempts', header: 'Max attempts', align: 'right' },
    { key: 'concurrencyLimit', header: 'Concurrency per key', align: 'right' },
    { key: 'rateLimit', header: 'Rate limit', align: 'right' },
    { key: 'modifiedAtUtc', header: 'Modified', class: 'mobile-hide' }
  ];

  // Drill into this definition's jobs: the Jobs list filters by name within its namespace scope, so
  // carry both the name and the namespace (via the ns scope param) in the link.
  function jobsHref(def: DefinitionRow): string {
    return routes.jobs({ jobName: def.jobName, namespace: def.jobNamespace });
  }

  // The row opens the policy editor (default / override / effective + override edits). Carry the
  // current namespace scope so drilling in (and back) doesn't reset it to "all namespaces".
  function detailHref(def: DefinitionRow): string {
    const ns = get(scope);
    return routes.definition(def.jobNamespace, def.jobName, { namespace: ns });
  }
</script>

<Page title="Definitions">

  <div class="panel fill">
    <FilterBar>
      <label>Status <select value={$filters.status} onchange={(event) => filters.patch({ status: event.currentTarget.value })}>{#each statuses as status}<option value={status}>{status || 'Any'}</option>{/each}</select></label>
      <label>Name <input placeholder="name contains…" value={$filters.name} onchange={(event) => filters.patch({ name: event.currentTarget.value })} /></label>
      <label>Tags <input placeholder="env:prod team" value={$filters.tags} onchange={(event) => filters.patch({ tags: event.currentTarget.value.trim() })} /></label>
    </FilterBar>
    <ActiveFilters chips={activeChips} onClearAll={() => { filters.clear(); setScope(''); }} />
    {#snippet statusCell(def: DefinitionRow)}<StatusBadge status={def.status} />{/snippet}
    {#snippet nameCell(def: DefinitionRow)}<span class="def-cell"><a class="def-name" href={detailHref(def)} title={def.jobName}>{def.jobName}</a><a class="jobs-drill" href={jobsHref(def)} title="View this definition's jobs" aria-label="View this definition's jobs"><Icon name="chevron-right" /></a></span>{/snippet}
    {#snippet inputCell(def: DefinitionRow)}<span class="type-name" title={def.inputTypeName}>{displayFormatter.typeName(def.inputTypeName)}</span>{/snippet}
    {#snippet resultCell(def: DefinitionRow)}<span class="type-name" title={def.resultTypeName ?? ''}>{displayFormatter.typeName(def.resultTypeName)}</span>{/snippet}
    {#snippet priorityCell(def: DefinitionRow)}{def.priorityEffective}{#if def.priorityOverride != null}<span class="ovr" title="operator override">*</span>{/if}{/snippet}
    {#snippet attemptsCell(def: DefinitionRow)}{displayFormatter.number(def.maxAttemptsEffective)}{#if def.maxAttemptsOverride != null}<span class="ovr" title="operator override">*</span>{/if}{/snippet}
    {#snippet concurrencyCell(def: DefinitionRow)}{#if def.concurrencyLimitEffective == null}<span title="No limit set: a job enqueued with a concurrency key still runs one at a time per key">none</span>{:else}{displayFormatter.number(def.concurrencyLimitEffective)}{/if}{#if def.concurrencyLimitOverride != null}<span class="ovr" title="operator override">*</span>{/if}{/snippet}
    {#snippet rateCell(def: DefinitionRow)}<span title={def.rateName ? 'meter: ' + def.rateName : ''}>{def.rateLimitEffective ?? 'unlimited'}</span>{#if def.rateLimitOverride != null}<span class="ovr" title="operator override">*</span>{/if}{/snippet}
    {#snippet modifiedCell(def: DefinitionRow)}<RelativeTime value={def.modifiedAtUtc} />{/snippet}

    <ActaGrid
      rowKey={(definition: DefinitionRow) => definition.jobNamespace + '/' + definition.jobName}
      endpoint="definitions"
      mobileCards={true}
      {columns}
      filters={() => ({ jobNamespace: $scope, nameContains: $filters.name.trim().toLowerCase(), status: $filters.status, tag: parseTagTokens($filters.tags) })}
      includeTotal={true}
      initialPageSize={Number(initial.get('pageSize') ?? '50') || 50}
      onPageSizeChange={(size) => updateHashParams({ pageSize: String(size) })}
      loadingText="Loading definitions..."
      emptyText="No definitions registered."
      cells={{
        status: statusCell,
        jobName: nameCell,
        inputTypeName: inputCell,
        resultTypeName: resultCell,
        priority: priorityCell,
        maxAttempts: attemptsCell,
        concurrencyLimit: concurrencyCell,
        rateLimit: rateCell,
        modifiedAtUtc: modifiedCell
      }} />
  </div>
</Page>

<style>
  /* The name takes the row's slack and gives it back first when the panel is narrow, ending in an
     ellipsis before the chevron rather than hiding it. */
  .def-cell { display: flex; align-items: center; }
  .def-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; }
  .jobs-drill { flex: none; }
  /* A long type name ends in an ellipsis; its tooltip and the definition page show it whole. */
  .type-name {
    display: inline-block;
    max-width: 16ch;
    overflow: hidden;
    text-overflow: ellipsis;
    vertical-align: bottom;
  }
  /* Row drill-through to the definition's jobs: a quiet chevron in a 24px box, the WCAG 2.2 target floor, that takes the accent on hover. */
  .jobs-drill { display: inline-flex; align-items: center; justify-content: center; min-width: 24px; min-height: 24px; color: var(--muted); }
  .jobs-drill:hover { color: var(--accent); }
  .jobs-drill :global(.ic) { top: 0; }

  .ovr {
    color: var(--accent);
    font-weight: 700;
    margin-left: 2px;
  }
</style>
