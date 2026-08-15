<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { api, ApiError } from '../api.ts';
  import { displayFormatter } from '../format.ts';
  import { keys } from '../query.ts';
  import { scope } from '../scope.ts';
  import { routes } from '../routes.ts';
  import ChangeHistory from '../components/ChangeHistory.svelte';
  import TagEditor from '../components/TagEditor.svelte';
  import CopyButton from '../components/CopyButton.svelte';
  import JobRef from '../components/JobRef.svelte';
  import Icon from '../components/Icon.svelte';
  import { mergeHistory, type HistoryEvent } from '../components/changeHistory.ts';
  import type { Paged } from '../api.ts';
  import Page from '../components/Page.svelte';
  import PageFreshness from '../components/PageFreshness.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import StateView from '../components/StateView.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import { workerStatusInterpretation, workerSupportSummary, type WorkerDetailShape } from './workerDetailState.ts';
  import { detailRefetchInterval, livePaused } from '../polling.ts';

  let { workerRef }: { workerRef: string } = $props();

  const detail = createQuery(() => {
    // Read the store while building the options so pausing immediately cancels the active interval.
    const paused = $livePaused;
    return {
      queryKey: keys.detail('workers', workerRef),
      queryFn: async ({ signal }: { signal: AbortSignal }): Promise<WorkerDetailShape | null> => {
        try {
          return await api<WorkerDetailShape>(`workers/${encodeURIComponent(workerRef)}`, {}, { signal });
        } catch (error) {
          if (error instanceof ApiError && error.status === 404) return null;
          throw error;
        }
      },
      refetchInterval: (query) => {
        const worker = query.state.data;
        const active = !!worker && (worker.status === 'active' || worker.status === 'draining');
        return detailRefetchInterval(active, paused);
      }
    };
  });

  let worker = $derived(detail.data ?? null);

  // Worker lifecycle trail (started/stopped/dead), scoped to this worker server-side via the events
  // endpoint's workerRef filter, then merged newest-first across the codes.
  const HISTORY_CODES = ['worker.started', 'worker.stopped', 'worker.died'];
  const history = createQuery(() => ({
    queryKey: keys.detail('worker-history', workerRef),
    queryFn: async ({ signal }: { signal: AbortSignal }) => {
      const pages = await Promise.all(
        HISTORY_CODES.map((eventCode) =>
          api<Paged<HistoryEvent>>('events', { workerRef, eventCode, pageSize: 20 }, { signal }).then((page) => page.items)
        )
      );
      return mergeHistory(pages);
    }
  }));
  let missing = $derived(!detail.isPending && !detail.error && detail.data === null);
  let error = $derived(detail.error instanceof Error ? detail.error.message : detail.error ? String(detail.error) : null);
  let polling = $derived(worker?.status === 'active' || worker?.status === 'draining');
  let backHref = $derived(routes.workers({ namespace: $scope }));
  let supportSummary = $derived(worker ? workerSupportSummary(worker) : '');
</script>

<Page title={`Worker ${workerRef}`}>
  {#snippet breadcrumb()}<a href={backHref}><Icon name="chevron-left" />Workers</a>{/snippet}
  {#snippet actions()}
    {#if worker}
      <CopyButton value={supportSummary} label="Copy support summary" />
    {/if}
    <PageFreshness
      dataUpdatedAt={detail.dataUpdatedAt}
      isFetching={detail.isFetching}
      isError={!!detail.error}
      {polling}
      onRefresh={() => detail.refetch()} />
  {/snippet}

  {#if missing}
    <div class="panel"><StateView emptyText="Worker not found." /></div>
  {:else if error}
    <div class="panel"><StateView {error} onRetry={() => detail.refetch()} /></div>
  {:else if worker}
    <section class="entity-summary" aria-label="Worker identity">
      <div class="entity-meta mono">{worker.workerRef} · {worker.jobNamespace} · {worker.host}{worker.processId == null ? '' : ` / PID ${worker.processId}`}</div>
      <StatusBadge status={worker.status} />
    </section>

    <div class="detail-workspace">
      <div class="detail-main">
        <section class="detail-panel" aria-labelledby="worker-status-heading">
          <h2 id="worker-status-heading">Status</h2>
          <p>{workerStatusInterpretation(worker.status)}</p>
          <dl class="detail-readonly detail-readonly-grid">
            <div><dt>Heartbeat lag</dt><dd><RelativeTime value={worker.lastHeartbeatAtUtc} /></dd></div>
            <div><dt>Exact heartbeat</dt><dd>{displayFormatter.timestamp(worker.lastHeartbeatAtUtc)}</dd></div>
            <div><dt>Started</dt><dd>{displayFormatter.timestamp(worker.startedAtUtc)}</dd></div>
            <div><dt>Last row change</dt><dd>{displayFormatter.timestamp(worker.modifiedAtUtc)}</dd></div>
          </dl>
        </section>

        <section class="detail-panel" aria-labelledby="worker-process-heading">
          <h2 id="worker-process-heading">Process identity</h2>
          <dl class="detail-readonly detail-readonly-grid">
            <div><dt>Host</dt><dd><span class="mono">{worker.host}</span></dd></div>
            <div><dt>Process ID</dt><dd><span class="mono">{worker.processId ?? 'unknown'}</span></dd></div>
            <div><dt>Max concurrency</dt><dd>{displayFormatter.number(worker.maxConcurrency)}</dd></div>
            <div><dt>Worker ref</dt><dd><JobRef value={worker.workerRef} copy /></dd></div>
          </dl>
        </section>

        <ChangeHistory history={history.data ?? []} loading={history.isPending} emptyText="No recorded worker lifecycle events." />
      </div>

      <aside class="detail-rail">
        <section class="detail-panel" aria-labelledby="worker-versions-heading">
          <h2 id="worker-versions-heading">Versions</h2>
          <dl class="detail-readonly">
            <div><dt>Deployment</dt><dd class="mono">{worker.deploymentVersion}</dd></div>
            <div><dt>Acta engine</dt><dd class="mono">{worker.engineVersion ?? 'unknown'}</dd></div>
            <div><dt>.NET</dt><dd class="mono">{worker.dotnetVersion ?? 'unknown'}</dd></div>
          </dl>
        </section>

        <section class="detail-panel go-to" aria-label="Go to">
          <p class="detail-kicker">Go to</p>
          <nav>
            <a href={routes.namespace(worker.jobNamespace, { namespace: worker.jobNamespace })}>Namespace</a>
            <a href={routes.jobs({ namespace: worker.jobNamespace })}>Namespace jobs</a>
            <a href={routes.alerts({ namespace: worker.jobNamespace })}>Namespace alerts</a>
          </nav>
        </section>

        <TagEditor path={`workers/${encodeURIComponent(worker.workerRef)}/tags`} />
      </aside>
    </div>
  {:else}
    <div class="panel"><StateView loading={true} loadingText="Loading worker..." /></div>
  {/if}
</Page>
