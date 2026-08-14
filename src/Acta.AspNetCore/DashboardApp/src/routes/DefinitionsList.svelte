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
    definitionId: number;
    jobNamespace: string;
    status: string;
    jobName: string;
    inputTypeName: string;
    outputTypeName: string | null;
    priorityEffective: string;
    priorityOverride: string | null;
    maxAttemptsEffective: number;
    maxAttemptsOverride: number | null;
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
    { key: 'jobName', header: 'Name' },
    { key: 'jobNamespace', header: 'Namespace', dimRepeats: true },
    { key: 'status', header: 'Status' },
    { key: 'inputTypeName', header: 'Input', class: 'mono mobile-hide' },
    { key: 'outputTypeName', header: 'Output', class: 'mono mobile-hide' },
    { key: 'priority', header: 'Priority', align: 'right' },
    { key: 'maxAttempts', header: 'Max attempts', align: 'right' },
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
    return routes.definition(def.definitionId, { namespace: ns });
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
    {#snippet nameCell(def: DefinitionRow)}<a href={detailHref(def)}>{def.jobName}</a> <a class="jobs-drill" href={jobsHref(def)} title="View this definition's jobs" aria-label="View this definition's jobs"><Icon name="chevron-right" /></a>{/snippet}
    {#snippet inputCell(def: DefinitionRow)}<span title={def.inputTypeName}>{displayFormatter.typeName(def.inputTypeName)}</span>{/snippet}
    {#snippet outputCell(def: DefinitionRow)}<span title={def.outputTypeName ?? ''}>{displayFormatter.typeName(def.outputTypeName)}</span>{/snippet}
    {#snippet priorityCell(def: DefinitionRow)}{def.priorityEffective}{#if def.priorityOverride != null}<span class="ovr" title="operator override">*</span>{/if}{/snippet}
    {#snippet attemptsCell(def: DefinitionRow)}{displayFormatter.number(def.maxAttemptsEffective)}{#if def.maxAttemptsOverride != null}<span class="ovr" title="operator override">*</span>{/if}{/snippet}
    {#snippet modifiedCell(def: DefinitionRow)}<RelativeTime value={def.modifiedAtUtc} />{/snippet}

    <ActaGrid
      rowKey={(definition: DefinitionRow) => definition.definitionId}
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
        outputTypeName: outputCell,
        priority: priorityCell,
        maxAttempts: attemptsCell,
        modifiedAtUtc: modifiedCell
      }} />
  </div>
</Page>

<style>
  /* Row drill-through to the definition's jobs: quiet chevron that takes the accent on hover. */
  .jobs-drill { color: var(--muted); margin-left: 4px; }
  .jobs-drill:hover { color: var(--accent); }
  .jobs-drill :global(.ic) { top: 2px; }

  .ovr {
    color: var(--accent);
    font-weight: 700;
    margin-left: 2px;
  }
</style>
