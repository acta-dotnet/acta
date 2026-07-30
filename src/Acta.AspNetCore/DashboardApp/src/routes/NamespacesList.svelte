<script lang="ts">
  import { get } from 'svelte/store';
  import Icon from '../components/Icon.svelte';
  import Page from '../components/Page.svelte';
  import ActaGrid from '../components/grid/ActaGrid.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import FilterBar from '../components/FilterBar.svelte';
  import { scope } from '../scope.ts';
  import { isSysNamespace } from './namespaceAdmin.ts';
  import type { NamespaceListItem } from '../api.ts';
  import ActiveFilters from '../components/ActiveFilters.svelte';
  import { setScope } from '../scope.ts';
  import { createUrlFilters } from '../urlFilters.ts';
  import { parseTagTokens } from '../lib/tagTokens.ts';
  import { routes } from '../routes.ts';


  // Namespace names are always lowercase kebab, so the name filter lowercases input to match the
  // server-side nameStartsWith prefix without a 400 on a stray capital.
  const filters = createUrlFilters({ name: 'name', status: 'status', tags: 'tags' }, { name: '', status: '', tags: '' });
  const statuses = ['', 'active', 'suspended'];

  let activeChips = $derived(
    [
      $scope ? { label: 'Namespace', value: $scope, onRemove: () => setScope('') } : null,
      $filters.name ? { label: 'Name', value: $filters.name, onRemove: () => filters.patch({ name: '' }) } : null,
      $filters.status ? { label: 'Status', value: $filters.status, onRemove: () => filters.patch({ status: '' }) } : null,
      $filters.tags.trim() ? { label: 'Tags', value: $filters.tags.trim(), onRemove: () => filters.patch({ tags: '' }) } : null
    ].filter((chip): chip is { label: string; value: string; onRemove: () => void } => chip !== null)
  );

  const columns = [
    { key: 'name', header: 'Name', class: 'mono' },
    { key: 'status', header: 'Status' },
    { key: 'ownerTeam', header: 'Owner team' },
    { key: 'description', header: 'Description' },
    { key: 'actions', header: '', class: 'col-open' }
  ];

  function detailHref(namespace: { name: string }): string {
    const selected = get(scope);
    return routes.namespace(namespace.name, { namespace: selected });
  }
</script>

<Page title="Namespaces">

  <div class="panel fill">
    <FilterBar>
      <label>
        Name
        <input placeholder="name contains…" value={$filters.name} onchange={(event) => filters.patch({ name: event.currentTarget.value })} />
      </label>
      <label>
        Status
        <select value={$filters.status} onchange={(event) => filters.patch({ status: event.currentTarget.value })}>
          {#each statuses as s}<option value={s}>{s === '' ? 'Any' : s}</option>{/each}
        </select>
      </label>
      <label>
        Tags
        <input placeholder="env:prod team" value={$filters.tags} onchange={(event) => filters.patch({ tags: event.currentTarget.value.trim() })} />
      </label>
    </FilterBar>
    <ActiveFilters chips={activeChips} onClearAll={() => { filters.clear(); setScope(''); }} />

    {#snippet nameCell(namespace: NamespaceListItem)}
      <a href={detailHref(namespace)}>{namespace.name}</a>
      {#if isSysNamespace(namespace.id)}<span class="badge held ns-sys-badge" title="Seeded system namespace">sys</span>{/if}
    {/snippet}
    {#snippet statusCell(namespace: NamespaceListItem)}<StatusBadge status={namespace.status} />{/snippet}
    {#snippet actionsCell(namespace: NamespaceListItem)}
      <a
        class="icon-action"
        href={detailHref(namespace)}
        title={'Open namespace ' + namespace.name}
        aria-label={'Open namespace ' + namespace.name}
        onclick={(event) => event.stopPropagation()}><Icon name="chevron-right" /></a>
    {/snippet}

    <ActaGrid
      rowKey={(namespace: NamespaceListItem) => namespace.id}
      endpoint="namespaces/admin"
      {columns}
      filters={() => ({ nameContains: $filters.name.trim().toLowerCase(), status: $filters.status, tag: parseTagTokens($filters.tags) })}
      includeTotal={true}
      loadingText="Loading namespaces..."
      emptyText="No namespaces match the filters."
      cells={{ name: nameCell, status: statusCell, actions: actionsCell }} />
  </div>
</Page>

<style>
  .ns-sys-badge { margin-left: 8px; }
</style>
