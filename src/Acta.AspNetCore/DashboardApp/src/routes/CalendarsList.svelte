<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import type { CalendarListItem } from '../api.ts';
  import { capabilitiesQuery, canControl } from '../query.ts';
  import Icon from '../components/Icon.svelte';
  import { hashParams, updateHashParams } from '../router';
  import { scope, setScope } from '../scope';
  import Page from '../components/Page.svelte';
  import ActaGrid from '../components/grid/ActaGrid.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import FilterBar from '../components/FilterBar.svelte';
  import ActiveFilters from '../components/ActiveFilters.svelte';
  import CopyButton from '../components/CopyButton.svelte';
  import VersionStatus from '../components/calendar/VersionStatus.svelte';
  import { createUrlFilters } from '../urlFilters.ts';
  import { calendarsListSql, COPY_SQL_TITLE } from '../lib/copyAsSql.ts';
  import { routes } from '../routes.ts';
  import type { ColumnDef } from '../components/grid/types.ts';

  const initial = hashParams();
  const filters = createUrlFilters({ role: 'role', nameSearch: 'q' }, { role: '', nameSearch: '' });

  let activeChips = $derived(
    [
      $scope ? { label: 'Namespace', value: $scope, onRemove: () => setScope('') } : null,
      $filters.role ? { label: 'Role', value: $filters.role, onRemove: () => filters.patch({ role: '' }) } : null,
      $filters.nameSearch ? { label: 'Name', value: $filters.nameSearch, onRemove: () => filters.patch({ nameSearch: '' }) } : null
    ].filter((chip): chip is { label: string; value: string; onRemove: () => void } => chip !== null)
  );
  function clearAllFilters() {
    filters.clear();
    setScope('');
  }

  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(canControl(capabilities.data));

  let copySql = $derived(
    calendarsListSql(
      { namespace: $scope, role: $filters.role, nameSearch: $filters.nameSearch },
      { provider: capabilities.data?.provider, schema: capabilities.data?.schema }
    )
  );

  // A family starts as a draft on its own page: the form only names it and hands over.
  let creating = $state(false);
  let newNamespace = $state('');
  let newName = $state('');
  let newRole = $state('availability');
  let newNameOk = $derived(/^[a-z0-9][a-z0-9.-]*$/.test(newName.trim().toLowerCase()));
  function openCreate() {
    creating = true;
    newNamespace = $scope || 'sys';
  }
  function create() {
    if (!newNameOk) return;
    location.hash = routes
      .calendar(newNamespace.trim().toLowerCase() || 'sys', newName.trim().toLowerCase(), { namespace: $scope, role: newRole })
      .slice(1);
  }

  const columns: ColumnDef<CalendarListItem>[] = [
    { key: 'calendarName', header: 'Calendar' },
    { key: 'role', header: 'Role', class: 'mobile-wide' },
    { key: 'jobNamespace', header: 'Scope', dimRepeats: true, class: 'mobile-wide' },
    { key: 'currentVersion', header: 'Current', class: 'mobile-wide' },
    { key: 'latestVersion', header: 'Latest', class: 'mobile-wide' },
    { key: 'dependentCount', header: 'Schedules', align: 'right', class: 'mobile-wide' },
    { key: 'publishedAtUtc', header: 'Published', class: 'mobile-hide' },
    { key: 'modifiedAtUtc', header: 'Modified', class: 'mobile-hide' },
    { key: 'actions', header: '', class: 'col-open' }
  ];

  const detailHref = (calendar: CalendarListItem) => routes.calendar(calendar.jobNamespace, calendar.calendarName, { namespace: $scope });
</script>

<Page title="Calendars">
  {#snippet actions()}
    {#if canControlNow}
      <button class="primary" onclick={openCreate} disabled={creating}><Icon name="plus" />New calendar</button>
    {/if}
  {/snippet}

  {#if creating}
    <form class="panel create-form" aria-label="New calendar" onsubmit={(event) => { event.preventDefault(); create(); }}>
      <label class="detail-field">
        Scope
        <input type="text" bind:value={newNamespace} placeholder="sys" list="calendar-scope-hints" />
        <datalist id="calendar-scope-hints"><option value="sys"></option></datalist>
        <span class="field-help">A namespace, or <span class="mono">sys</span> for a calendar every namespace can bind.</span>
      </label>
      <label class="detail-field">
        Name
        <input type="text" bind:value={newName} placeholder="office-hours" aria-invalid={newName !== '' && !newNameOk} />
        <span class="field-help">Lowercase letters, digits, dots, and dashes.</span>
      </label>
      <label class="detail-field">
        Role
        <select bind:value={newRole}>
          <option value="availability">Availability (when work may run)</option>
          <option value="occurrence">Occurrence (when work is due)</option>
        </select>
      </label>
      <div class="create-actions">
        <button type="submit" class="primary" disabled={!newNameOk}>Open the editor</button>
        <button type="button" onclick={() => (creating = false)}>Cancel</button>
      </div>
    </form>
  {/if}

  <div class="panel fill">
    <FilterBar>
      <label>
        Role
        <select value={$filters.role} onchange={(event) => filters.patch({ role: event.currentTarget.value })}>
          <option value="">All roles</option>
          <option value="availability">Availability</option>
          <option value="occurrence">Occurrence</option>
        </select>
      </label>
      <label>
        Name
        <input placeholder="contains" value={$filters.nameSearch} onchange={(event) => filters.patch({ nameSearch: event.currentTarget.value.trim() })} />
      </label>
      <span class="dim calendar-help">
        Calendars kept here are data: saved as drafts, published, and followed by the schedules that bind them by name.
        Calendars declared in code with <span class="mono">[Calendar]</span> are not listed; a schedule prefers those.
      </span>
      <CopyButton value={copySql} label="Copy SQL" title={COPY_SQL_TITLE} />
    </FilterBar>

    <ActiveFilters chips={activeChips} onClearAll={clearAllFilters} />

    {#snippet nameCell(calendar: CalendarListItem)}<a href={detailHref(calendar)}>{calendar.calendarName}</a>{/snippet}
    {#snippet scopeCell(calendar: CalendarListItem)}
      {#if calendar.jobNamespace === 'sys'}<span class="badge scope-badge" title="Shared: every namespace can bind it">shared</span>{:else}{calendar.jobNamespace}{/if}
    {/snippet}
    {#snippet currentCell(calendar: CalendarListItem)}
      {#if calendar.currentVersion === null}<span class="dim">none</span>{:else}<VersionStatus status="published" version={calendar.currentVersion} />{/if}
    {/snippet}
    {#snippet latestCell(calendar: CalendarListItem)}
      {#if calendar.latestVersion === calendar.currentVersion}<span class="dim">same</span>{:else}<VersionStatus status={calendar.latestStatus} version={calendar.latestVersion} />{/if}
    {/snippet}
    {#snippet publishedCell(calendar: CalendarListItem)}<RelativeTime value={calendar.publishedAtUtc} />{/snippet}
    {#snippet modifiedCell(calendar: CalendarListItem)}<RelativeTime value={calendar.modifiedAtUtc} />{/snippet}
    {#snippet actionsCell(calendar: CalendarListItem)}
      <a class="icon-action" href={detailHref(calendar)} title={'Open calendar ' + calendar.calendarName} aria-label={'Open calendar ' + calendar.calendarName}><Icon name="chevron-right" /></a>
    {/snippet}

    <ActaGrid
      rowKey={(calendar: CalendarListItem) => `${calendar.jobNamespace}/${calendar.calendarName}`}
      endpoint="calendars"
      mobileCards={true}
      {columns}
      filters={() => ({ jobNamespace: $scope, role: $filters.role, nameSearch: $filters.nameSearch })}
      includeTotal={true}
      initialPageSize={Number(initial.get('pageSize') ?? '50') || 50}
      onPageSizeChange={(size) => updateHashParams({ pageSize: String(size) })}
      loadingText="Loading calendars..."
      emptyText="No catalog calendars yet. Save a draft to start one; a schedule binds it by name once a version is published."
      cells={{ calendarName: nameCell, jobNamespace: scopeCell, currentVersion: currentCell, latestVersion: latestCell, publishedAtUtc: publishedCell, modifiedAtUtc: modifiedCell, actions: actionsCell }}
      rowClass={(calendar: CalendarListItem) => (calendar.latestStatus === 'draft' ? 'due' : '')} />
  </div>
</Page>

<style>
  .calendar-help { flex-basis: 100%; }
  .scope-badge { white-space: nowrap; }
  .create-form {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
    gap: 0 20px;
    align-items: start;
  }
  .create-form .detail-field { margin-top: 4px; }
  .field-help { font-weight: 400; color: var(--muted); font-size: var(--text-xs); }
  .create-actions {
    grid-column: 1 / -1;
    display: flex;
    gap: 10px;
    margin-top: 14px;
  }
</style>
