<script lang="ts">
  import { get } from 'svelte/store';
  import Icon from '../components/Icon.svelte';
  import Page from '../components/Page.svelte';
  import ActaGrid from '../components/grid/ActaGrid.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import FilterBar from '../components/FilterBar.svelte';
  import { scope } from '../scope.ts';
  import type { TenantListItem } from '../api.ts';
  import ActiveFilters from '../components/ActiveFilters.svelte';
  import { setScope } from '../scope.ts';
  import { createUrlFilters } from '../urlFilters.ts';
  import { parseTagTokens } from '../lib/tagTokens.ts';
  import { routes } from '../routes.ts';


  // `search` is a case-insensitive server-side contains over tenant key, display name, or description.
  const filters = createUrlFilters({ search: 'search', status: 'status', tags: 'tags' }, { search: '', status: '', tags: '' });
  const statuses = ['', 'active', 'suspended'];

  let activeChips = $derived(
    [
      $scope ? { label: 'Namespace', value: $scope, onRemove: () => setScope('') } : null,
      $filters.search ? { label: 'Search', value: $filters.search, onRemove: () => filters.patch({ search: '' }) } : null,
      $filters.status ? { label: 'Status', value: $filters.status, onRemove: () => filters.patch({ status: '' }) } : null,
      $filters.tags.trim() ? { label: 'Tags', value: $filters.tags.trim(), onRemove: () => filters.patch({ tags: '' }) } : null
    ].filter((chip): chip is { label: string; value: string; onRemove: () => void } => chip !== null)
  );

  const columns = [
    { key: 'tenantKey', header: 'Tenant key', class: 'mono' },
    { key: 'displayName', header: 'Display name' },
    { key: 'status', header: 'Status' },
    { key: 'description', header: 'Description', class: 'mobile-hide' },
    { key: 'createdAtUtc', header: 'Created', class: 'mobile-hide' },
    { key: 'modifiedAtUtc', header: 'Modified' },
    { key: 'actions', header: '', class: 'col-open' }
  ];

  function detailHref(tenant: { tenantKey: string }): string {
    return routes.tenant(tenant.tenantKey, { namespace: get(scope) });
  }
</script>

<Page title="Tenants">
  {#snippet actions()}
    <a class="add-action" href={routes.newTenant({ namespace: $scope })} title="Register tenant" aria-label="Register tenant"><Icon name="plus" /></a>
  {/snippet}

  <div class="panel fill">
    <FilterBar>
      <label>
        Search
        <input placeholder="key, display name, or description" value={$filters.search} onchange={(event) => filters.patch({ search: event.currentTarget.value })} />
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

    {#snippet tenantCell(tenant: TenantListItem)}<a href={detailHref(tenant)} class="mono">{tenant.tenantKey}</a>{/snippet}
    {#snippet statusCell(tenant: TenantListItem)}<StatusBadge status={tenant.status} />{/snippet}
    {#snippet createdCell(tenant: TenantListItem)}<RelativeTime value={tenant.createdAtUtc} />{/snippet}
    {#snippet modifiedCell(tenant: TenantListItem)}<RelativeTime value={tenant.modifiedAtUtc} />{/snippet}
    {#snippet actionsCell(tenant: TenantListItem)}
      <a
        class="icon-action"
        href={detailHref(tenant)}
        title={'Open tenant ' + tenant.tenantKey}
        aria-label={'Open tenant ' + tenant.tenantKey}><Icon name="chevron-right" /></a>
    {/snippet}

    <ActaGrid
      rowKey={(tenant: TenantListItem) => tenant.tenantKey}
      endpoint="tenants"
      mobileCards={true}
      {columns}
      filters={() => ({ nameContains: $filters.search.trim(), status: $filters.status, tag: parseTagTokens($filters.tags) })}
      includeTotal={true}
      loadingText="Loading tenants..."
      emptyText="No tenants match the filters."
      cells={{ tenantKey: tenantCell, status: statusCell, createdAtUtc: createdCell, modifiedAtUtc: modifiedCell, actions: actionsCell }} />
  </div>
</Page>
