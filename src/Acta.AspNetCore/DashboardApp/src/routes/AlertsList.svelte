<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import Icon from '../components/Icon.svelte';
  import { hashParams, updateHashParams } from '../router';
  import { scope, setScope } from '../scope';
  import { capabilitiesQuery, canControl } from '../query.ts';
  import { useControlMutation } from '../lib/useControlMutation.ts';
  import type { AlertControlResponse } from '../api.ts';
  import { alertStateBucket, alertStateMatches, alertStateQuery, type AlertStateBucket } from './alertStateFilter.ts';
  import Page from '../components/Page.svelte';
  import ActaGrid from '../components/grid/ActaGrid.svelte';
  import SeverityBadge from '../components/SeverityBadge.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import CopyButton from '../components/CopyButton.svelte';
  import FilterBar from '../components/FilterBar.svelte';
  import ActiveFilters from '../components/ActiveFilters.svelte';
  import ConfirmAction from '../components/ConfirmAction.svelte';
  import { createUrlFilters } from '../urlFilters.ts';
  import { routes } from '../routes.ts';
  import JobRef from '../components/JobRef.svelte';
  import type { ColumnDef } from '../components/grid/types.ts';
  import { displayFormatter } from '../format.ts';

  interface AlertRow {
    jobAlertId: number;
    severity: string;
    title: string;
    message: string;
    jobNamespace: string;
    jobRef: string | null;
    channelName: string;
    occurrenceCount: number;
    deliveryStatus: string;
    createdAtUtc: string;
    acknowledgedAtUtc: string | null;
    resolvedAtUtc: string | null;
  }

  const severities = ['', 'Info', 'Warning', 'Error', 'Critical'];
  const states: { value: AlertStateBucket; label: string }[] = [
    { value: 'unacknowledged', label: 'Unacknowledged (open)' },
    { value: 'acknowledged', label: 'Acknowledged (open)' },
    { value: 'resolved', label: 'Resolved' }
  ];
  const initial = hashParams();
  const filters = createUrlFilters({ state: 'state', severity: 'sev' }, { state: 'unacknowledged', severity: '' });
  let stateFilter = $derived($filters.state as AlertStateBucket);

  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(canControl(capabilities.data));

  const columns: ColumnDef<AlertRow>[] = [
    { key: 'severity', header: 'Severity' },
    { key: 'state', header: 'State' },
    { key: 'title', header: 'Title' },
    { key: 'message', header: 'Message', class: 'dim' },
    { key: 'job', header: 'Job' },
    { key: 'channelName', header: 'Channel', class: 'mobile-hide' },
    { key: 'occurrenceCount', header: 'Count', class: 'mobile-hide', align: 'right' },
    { key: 'deliveryStatus', header: 'Delivery', class: 'mobile-hide' },
    { key: 'latest', header: 'Latest', class: 'mobile-hide' },
    { key: 'copy', header: '', class: 'mobile-hide' },
    { key: 'actions', header: '', class: 'col-actions' }
  ];

  // The state bucket is a required view selector (one of three), not a removable filter, so it stays
  // out of the chips; namespace scope and the severity floor are the removable ones.
  let activeChips = $derived.by(() => {
    const chips: { label: string; value: string; onRemove: () => void }[] = [];
    if ($scope) chips.push({ label: 'Namespace', value: $scope, onRemove: () => setScope('') });
    if ($filters.severity) {
      chips.push({
        label: 'Severity ≥',
        value: $filters.severity,
        onRemove: () => filters.patch({ severity: '' })
      });
    }
    return chips;
  });
  function clearAllFilters() {
    filters.clear();
    setScope('');
  }

  // Both verbs live at alerts/{jobAlertId}/{action} and return AlertControlResponse; invalidating
  // the 'alerts' key prefix refreshes every cached alerts list/page.
  const mutation = useControlMutation<
    { alertId: number; action: 'acknowledge' | 'resolve'; note?: string },
    AlertControlResponse
  >({
    path: (vars) => `alerts/${vars.alertId}/${vars.action}`,
    body: (vars) => ({ note: vars.note?.trim() || null }),
    notFound: (vars) => ({ alertId: vars.alertId, action: 'notFound', acknowledgedAtUtc: null, resolvedAtUtc: null }),
    invalidateKeys: () => [['alerts']] as const
  });
  let busy = $derived(mutation.isPending);
  let message = $state('');
  let messageKind = $state('');
  let confirming = $state<{ alertId: number; action: 'acknowledge' | 'resolve'; title: string } | null>(null);

  async function act(alertId: number, action: 'acknowledge' | 'resolve', note: string) {
    confirming = null;
    message = '';
    try {
      const result = await mutation.mutateAsync({ alertId, action, note });
      // Only 'applied' is green; anything else (notFound, or a future non-applied action) is a warning,
      // never a false success - same guard as JobControls' `result.action === 'applied' ? 'ok' : 'warn'`.
      const applied = result.action === 'applied';
      message = applied ? (action === 'acknowledge' ? 'Acknowledged.' : 'Resolved.') : 'Alert not found.';
      messageKind = applied ? 'ok' : 'warn';
    } catch (e) {
      message = (e as Error).message;
      messageKind = 'bad';
    }
  }
</script>

<Page title="Alerts">

  <div class="panel fill">
    <FilterBar>
      <label>
        State
        <select value={$filters.state} onchange={(event) => filters.patch({ state: event.currentTarget.value })}>
          {#each states as s}
            <option value={s.value}>{s.label}</option>
          {/each}
        </select>
      </label>
      <label>
        Severity at least
        <select value={$filters.severity} onchange={(event) => filters.patch({ severity: event.currentTarget.value })}>
          {#each severities as s}
            <option value={s}>{s === '' ? 'Any' : s}</option>
          {/each}
        </select>
      </label>
    </FilterBar>

    <ActiveFilters chips={activeChips} onClearAll={clearAllFilters} />

    {#if message}
      <div class="control-message {messageKind}" role="status">{message}</div>
    {/if}

    {#snippet severityCell(alert: AlertRow)}<SeverityBadge severity={alert.severity} />{/snippet}
    {#snippet stateCell(alert: AlertRow)}
      {@const bucket = alertStateBucket(alert)}
      <span class="badge {bucket === 'resolved' ? 'ok' : bucket === 'acknowledged' ? 'held' : 'warn'}">
        {bucket === 'resolved' ? 'Resolved' : bucket === 'acknowledged' ? 'Acknowledged' : 'Open'}
      </span>
    {/snippet}
    {#snippet jobCell(alert: AlertRow)}
      {#if alert.jobRef}<JobRef value={alert.jobRef} href={routes.job(alert.jobRef, { namespace: alert.jobNamespace })} />{:else}<span class="dim">-</span>{/if}
    {/snippet}
    {#snippet deliveryCell(alert: AlertRow)}<StatusBadge status={alert.deliveryStatus} />{/snippet}
    {#snippet latestCell(alert: AlertRow)}
      {@const latest = alert.resolvedAtUtc ?? alert.acknowledgedAtUtc ?? alert.createdAtUtc}
      <RelativeTime value={latest} title={`Created ${displayFormatter.timestamp(alert.createdAtUtc)}${alert.acknowledgedAtUtc ? ` · Acknowledged ${displayFormatter.timestamp(alert.acknowledgedAtUtc)}` : ''}${alert.resolvedAtUtc ? ` · Resolved ${displayFormatter.timestamp(alert.resolvedAtUtc)}` : ''}`} />
    {/snippet}
    {#snippet copyCell(alert: AlertRow)}<CopyButton value={alert.title + ' - ' + alert.message} label="Copy details" />{/snippet}
    {#snippet actionsCell(alert: AlertRow)}
      {#if canControlNow}
        {#if !alert.acknowledgedAtUtc}
          <button disabled={busy} onclick={() => (confirming = { alertId: alert.jobAlertId, action: 'acknowledge', title: alert.title })}><Icon name="check-circle" />Acknowledge</button>
        {/if}
        {#if !alert.resolvedAtUtc}
          <button disabled={busy} onclick={() => (confirming = { alertId: alert.jobAlertId, action: 'resolve', title: alert.title })}><Icon name="check-circle" />Resolve</button>
        {/if}
      {/if}
    {/snippet}

    <ActaGrid
      rowKey={(alert: AlertRow) => alert.jobAlertId}
      endpoint="alerts"
      mobileCards={true}
      {columns}
      filters={() => ({ ...alertStateQuery(stateFilter), severityAtLeast: $filters.severity, jobNamespace: $scope })}
      rowFilter={(alert: AlertRow) => alertStateMatches(stateFilter, alert)}
      countMode={stateFilter === 'resolved' ? 'none' : 'on-demand'}
      initialPageSize={Number(initial.get('pageSize') ?? '50') || 50}
      onPageSizeChange={(size) => updateHashParams({ pageSize: String(size) })}
      loadingText="Loading alerts..."
      emptyText="No alerts match the filters."
      cells={{
        severity: severityCell,
        state: stateCell,
        job: jobCell,
        deliveryStatus: deliveryCell,
        latest: latestCell,
        copy: copyCell,
        actions: actionsCell
      }}
      rowClass={(alert: AlertRow) => (alert.severity === 'critical' && !alert.resolvedAtUtc ? 'trouble' : '')} />
  </div>
</Page>

{#if confirming}
  {@const target = confirming}
  <ConfirmAction
    title={(target.action === 'acknowledge' ? 'Acknowledge' : 'Resolve') + ' alert?'}
    body={
      target.action === 'acknowledge'
        ? `Marks “${target.title}” as seen while keeping it open for follow-up.`
        : `Marks “${target.title}” resolved. This records an operator event but does not change the job.`
    }
    confirmLabel={target.action === 'acknowledge' ? 'Acknowledge alert' : 'Resolve alert'}
    requireReason={target.action === 'resolve'}
    onConfirm={(note) => act(target.alertId, target.action, note)}
    onCancel={() => (confirming = null)} />
{/if}

<style>
  :global(td.col-actions) { min-width: 180px; }
</style>
