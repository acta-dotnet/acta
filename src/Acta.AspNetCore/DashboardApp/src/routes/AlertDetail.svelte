<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { api, ApiError, type AlertControlResponse, type AlertDetailView } from '../api.ts';
  import { keys, capabilitiesQuery, canControl } from '../query.ts';
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
  import PageFreshness from '../components/PageFreshness.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import SeverityBadge from '../components/SeverityBadge.svelte';
  import StateView from '../components/StateView.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import TagEditor from '../components/TagEditor.svelte';

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
  let canControlNow = $derived(canControl(capabilities.data));

  let alert = $derived(detail.data ?? null);
  let missing = $derived(!detail.isPending && !detail.error && detail.data === null);
  let error = $derived(detail.error instanceof Error ? detail.error.message : detail.error ? String(detail.error) : null);
  let bucket = $derived(alert ? alertStateBucket(alert) : 'unacknowledged');

  // Same two verbs the list drives, at the same alerts/{alertRef}/{action} path; invalidating the
  // 'alerts' key prefix refreshes this screen and every cached list page together.
  const mutation = useControlMutation<{ action: 'acknowledge' | 'resolve'; note?: string }, AlertControlResponse>({
    path: (vars) => `alerts/${encodeURIComponent(alertRef)}/${vars.action}`,
    body: (vars) => ({ reasonMessage: vars.note?.trim() || null }),
    notFound: () => ({ alertRef, action: 'notFound', acknowledgedAtUtc: null, resolvedAtUtc: null }),
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
      const result = await mutation.mutateAsync({ action, note });
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

<Page title={alert ? alert.title : 'Alert'}>
  {#snippet breadcrumb()}<a href={backHref}><Icon name="chevron-left" />Alerts</a>{/snippet}
  {#snippet actions()}
    <PageFreshness
      dataUpdatedAt={detail.dataUpdatedAt}
      isFetching={detail.isFetching}
      isError={!!detail.error}
      onRefresh={() => detail.refetch()} />
  {/snippet}

  {#if missing}
    <div class="panel"><StateView emptyText="Alert not found." /></div>
  {:else if error}
    <div class="panel"><StateView {error} onRetry={() => detail.refetch()} /></div>
  {:else if alert}
    <section class="entity-summary" aria-label="Alert identity">
      <div class="entity-meta mono">
        <JobRef value={alert.alertRef} copy /> · {alert.jobNamespace} · {alert.kind} · {alert.origin}
      </div>
      <SeverityBadge severity={alert.severity} />
      <span class="badge {bucket === 'resolved' ? 'ok' : bucket === 'acknowledged' ? 'held' : 'warn'}">
        {bucket === 'resolved' ? 'Resolved' : bucket === 'acknowledged' ? 'Acknowledged' : 'Open'}
      </span>
    </section>

    <div class="detail-workspace">
      <div class="detail-main">
        <section class="detail-panel" aria-labelledby="alert-what-heading">
          <h2 id="alert-what-heading">What happened</h2>
          <p>{alert.message}</p>
          <dl class="detail-readonly detail-readonly-grid">
            <div><dt>Raised</dt><dd><RelativeTime value={alert.createdAtUtc} /></dd></div>
            <div><dt>Exact instant</dt><dd>{displayFormatter.timestamp(alert.createdAtUtc)}</dd></div>
            <div>
              <dt>Occurrences</dt>
              <dd>{displayFormatter.number(alert.occurrenceCount)}</dd>
            </div>
            <div><dt>Last row change</dt><dd>{displayFormatter.timestamp(alert.modifiedAtUtc)}</dd></div>
            <div>
              <dt>Acknowledged</dt>
              <dd>{alert.acknowledgedAtUtc ? displayFormatter.timestamp(alert.acknowledgedAtUtc) : '·'}</dd>
            </div>
            <div>
              <dt>Resolved</dt>
              <dd>{alert.resolvedAtUtc ? displayFormatter.timestamp(alert.resolvedAtUtc) : '·'}</dd>
            </div>
          </dl>
        </section>

        <section class="detail-panel" aria-labelledby="alert-delivery-heading">
          <h2 id="alert-delivery-heading">Delivery</h2>
          <dl class="detail-readonly detail-readonly-grid">
            <div><dt>Status</dt><dd><StatusBadge status={alert.deliveryStatus} /></dd></div>
            <div><dt>Channel</dt><dd class="mono">{alert.channelName}</dd></div>
            <div><dt>Retries</dt><dd>{displayFormatter.number(alert.retryCount)}</dd></div>
            <div>
              <dt>Next attempt</dt>
              <dd>{alert.retryAfterUtc ? displayFormatter.timestamp(alert.retryAfterUtc) : '·'}</dd>
            </div>
          </dl>
        </section>
      </div>

      <aside class="detail-rail">
        {#if canControlNow}
          <section class="detail-panel" aria-labelledby="alert-actions-heading">
            <h2 id="alert-actions-heading">Actions</h2>
            <div class="detail-form-actions">
              {#if !alert.acknowledgedAtUtc}
                <button disabled={busy} onclick={() => (confirming = 'acknowledge')}>
                  <Icon name="check-circle" />Acknowledge
                </button>
              {/if}
              {#if !alert.resolvedAtUtc}
                <button disabled={busy} onclick={() => (confirming = 'resolve')}><Icon name="check-circle" />Resolve</button>
              {/if}
            </div>
            {#if alert.acknowledgedAtUtc && alert.resolvedAtUtc}
              <p class="detail-help">This alert is acknowledged and resolved; both verbs are idempotent and already applied.</p>
            {/if}
            {#if message}<div class="control-message {messageKind}" role="status">{message}</div>{/if}
          </section>
        {/if}

        <section class="detail-panel go-to" aria-label="Go to">
          <p class="detail-kicker">Go to</p>
          <nav>
            {#if alert.jobRef}
              <a href={routes.job(alert.jobRef, { namespace: alert.jobNamespace })}>Subject job</a>
            {/if}
            <a href={routes.namespace(alert.jobNamespace, { namespace: alert.jobNamespace })}>Namespace</a>
            <a href={routes.alerts({ namespace: alert.jobNamespace })}>Namespace alerts</a>
          </nav>
          {#if !alert.jobRef}
            <p class="detail-help">No subject job: this alert is namespace-scoped, or the job row was purged.</p>
          {/if}
        </section>

        <section class="detail-panel" aria-labelledby="alert-copy-heading">
          <h2 id="alert-copy-heading">Share</h2>
          <CopyButton value={`${alert.alertRef}\n${alert.title}\n${alert.message}`} label="Copy alert summary" />
        </section>

        <TagEditor path={`alerts/${encodeURIComponent(alert.alertRef)}/tags`} />
      </aside>
    </div>
  {:else}
    <div class="panel"><StateView loading={true} loadingText="Loading alert..." /></div>
  {/if}
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
