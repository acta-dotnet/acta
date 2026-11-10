<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { api, ApiError, type AlertControlResponse, type AlertDetailView } from '../api.ts';
  import { keys, capabilitiesQuery, can, Permission } from '../query.ts';
  import { scope } from '../scope.ts';
  import { routes } from '../routes.ts';
  import { useControlMutation } from '../lib/useControlMutation.ts';
  import { alertStateBucket } from './alertStateFilter.ts';
  import { displayFormatter } from '../format.ts';
  import ConfirmAction from '../components/ConfirmAction.svelte';
  import CopyButton from '../components/CopyButton.svelte';
  import Icon from '../components/Icon.svelte';
  import JobRef from '../components/JobRef.svelte';
  import Page from '../components/Page.svelte';
  import DetailLayout from '../components/DetailLayout.svelte';
  import DetailFacts from '../components/DetailFacts.svelte';
  import FocusDisclosure from '../components/FocusDisclosure.svelte';
  import PageFreshness from '../components/PageFreshness.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import SeverityBadge from '../components/SeverityBadge.svelte';
  import StateView from '../components/StateView.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';

  let { alertRef }: { alertRef: string } = $props();

  const detail = createQuery(() => ({
    queryKey: keys.detail('alerts', alertRef),
    queryFn: async ({ signal }: { signal: AbortSignal }): Promise<AlertDetailView | null> => {
      try {
        return await api<AlertDetailView>(`alerts/${encodeURIComponent(alertRef)}`, {}, { signal });
      } catch (error) {
        if (error instanceof ApiError && error.status === 404) return null;
        throw error;
      }
    }
  }));

  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(can(capabilities.data, Permission.control));

  let alert = $derived(detail.data ?? null);
  let missing = $derived(!detail.isPending && !detail.error && detail.data === null);
  let error = $derived(detail.error instanceof Error ? detail.error.message : detail.error ? String(detail.error) : null);
  let bucket = $derived(alert ? alertStateBucket(alert) : 'unacknowledged');
  // Once a send settles, delivered or failed, retryAfterUtc holds no retry: it is when an automatic alert still
  // unresolved is sent again as a reminder.
  let settled = $derived(alert?.deliveryStatus === 'delivered' || alert?.deliveryStatus === 'failed');

  // Same two verbs the list drives, at the same alerts/{alertRef}/{action} path; invalidating the
  // 'alerts' key prefix refreshes this screen and every cached list page together.
  const mutation = useControlMutation<{ action: 'acknowledge' | 'resolve'; reason?: string }, AlertControlResponse>({
    path: (vars) => `alerts/${encodeURIComponent(alertRef)}/${vars.action}`,
    notFound: () => ({ action: 'not-found' }),
    invalidateKeys: () => [['alerts']] as const
  });
  let busy = $derived(mutation.isPending);
  let message = $state('');
  let messageKind = $state('');
  let confirming = $state<'acknowledge' | 'resolve' | null>(null);

  async function act(action: 'acknowledge' | 'resolve', note: string) {
    confirming = null;
    message = '';
    try {
      const result = await mutation.mutateAsync({ action, reason: note });
      const applied = result.action === 'applied';
      message = applied ? (action === 'acknowledge' ? 'Acknowledged.' : 'Resolved.') : 'Alert not found.';
      messageKind = applied ? 'ok' : 'warn';
      if (applied) await detail.refetch();
    } catch (e) {
      message = e instanceof Error ? e.message : String(e);
      messageKind = 'bad';
    }
  }

  let backHref = $derived(routes.alerts({ namespace: $scope }));
</script>

<Page title={alert?.title ?? 'Alert'}>
  {#snippet breadcrumb()}<a href={backHref}><Icon name="chevron-left" />Alerts</a>{/snippet}
  {#snippet actions()}
    {#if alert}<CopyButton value={alert.alertRef + '\n' + alert.title + '\n' + alert.message} label="Copy alert summary" />{/if}
    <PageFreshness dataUpdatedAt={detail.dataUpdatedAt} isFetching={detail.isFetching} isError={!!detail.error} onRefresh={() => detail.refetch()} />
  {/snippet}
  {#if missing}<div class="panel"><StateView emptyText="Alert not found." /></div>
  {:else if error}<div class="panel"><StateView {error} onRetry={() => detail.refetch()} /></div>
  {:else if alert}
    <section class="entity-summary" aria-label="Alert identity">
      <SeverityBadge severity={alert.severity} /><span class="badge {bucket === 'resolved' ? 'ok' : bucket === 'acknowledged' ? 'held' : 'warn'}">{bucket === 'resolved' ? 'Resolved' : bucket === 'acknowledged' ? 'Acknowledged' : 'Open'}</span>
      <a href={routes.namespace(alert.jobNamespace, { namespace: alert.jobNamespace })}>{alert.jobNamespace}</a><JobRef value={alert.alertRef} copy />
    </section>
    <DetailLayout>
      {#snippet related()}
        {#if alert.jobRef}<a href={routes.job(alert.jobRef, { namespace: alert.jobNamespace })}>Subject job</a>{/if}
        <a href={routes.namespace(alert.jobNamespace, { namespace: alert.jobNamespace })}>Namespace</a><a href={routes.alerts({ namespace: alert.jobNamespace })}>Namespace alerts</a>
      {/snippet}
      <section class="focus-state" style={'--tone:var(--' + (bucket === 'resolved' ? 'ok' : 'bad') + ')'} aria-label="What happened">
        <div class="focus-state-content">
          <h2><Icon name={bucket === 'resolved' ? 'check' : 'warn'} />{bucket === 'resolved' ? 'Incident resolved' : bucket === 'acknowledged' ? 'Acknowledged and awaiting resolution' : 'Needs attention'}</h2>
          <p>{alert.message}</p>
          <div class="focus-facts"><span>Raised <RelativeTime value={alert.createdAtUtc} /></span><span><strong>{displayFormatter.number(alert.occurrenceCount)}</strong> {alert.occurrenceCount === 1 ? 'occurrence' : 'occurrences'}</span></div>
        </div>
        {#if canControlNow && (!alert.acknowledgedAtUtc || !alert.resolvedAtUtc)}
          <div class="focus-inline-actions">
            {#if !alert.acknowledgedAtUtc}<button disabled={busy} onclick={() => (confirming = 'acknowledge')}><Icon name="check" />Acknowledge</button>{/if}
            {#if !alert.resolvedAtUtc}<button class="primary" disabled={busy} onclick={() => (confirming = 'resolve')}><Icon name="check" />Resolve</button>{/if}
          </div>
        {/if}
        {#if message}<div class="control-message {messageKind}" role="status">{message}</div>{/if}
      </section>
      <FocusDisclosure title="Delivery" hint={alert.deliveryStatus + ' · ' + alert.channelName} open={alert.deliveryStatus !== 'delivered'}>
        <dl class="detail-readonly detail-readonly-grid">
          <div><dt>Status</dt><dd><StatusBadge status={alert.deliveryStatus} /></dd></div>
          <div><dt>Channel</dt><dd class="mono">{alert.channelName}</dd></div>
          <div><dt>Retries</dt><dd>{displayFormatter.number(alert.retryCount)}</dd></div>
          <div><dt>{settled ? 'Next reminder' : 'Next attempt'}</dt><dd><RelativeTime value={alert.retryAfterUtc} /></dd></div>
        </dl>
      </FocusDisclosure>
      <FocusDisclosure title="Technical details" hint="Origin, identifiers and dates">
        <DetailFacts facts={[
          { label: 'Full reference', value: alert.alertRef, kind: 'mono', copy: true, wide: true },
          { label: 'Kind', value: alert.kind },
          { label: 'Origin', value: alert.origin },
          { label: 'Modified', value: alert.modifiedAtUtc, kind: 'time' },
          { label: 'Acknowledged', value: alert.acknowledgedAtUtc, kind: 'time' },
          { label: 'Resolved', value: alert.resolvedAtUtc, kind: 'time' },
          { label: 'Subject', value: alert.jobRef ? null : 'Namespace-scoped, or the subject job has been purged.', wide: true },
        ]} />
      </FocusDisclosure>
    </DetailLayout>
  {:else}<div class="panel"><StateView loading={true} loadingText="Loading alert..." /></div>{/if}
</Page>

{#if confirming && alert}
  {@const action = confirming}
  <ConfirmAction
    title={(action === 'acknowledge' ? 'Acknowledge' : 'Resolve') + ' alert?'}
    body={
      action === 'acknowledge'
        ? `Marks “${alert.title}” as seen while keeping it open for follow-up.`
        : `Marks “${alert.title}” resolved. This records an operator event but does not change the job.`
    }
    confirmLabel={action === 'acknowledge' ? 'Acknowledge alert' : 'Resolve alert'}
    requireReason={action === 'resolve'}
    onConfirm={(note) => act(action, note)}
    onCancel={() => (confirming = null)} />
{/if}
