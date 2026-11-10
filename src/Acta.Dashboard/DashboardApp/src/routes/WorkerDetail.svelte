<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { api, ApiError } from '../api.ts';
  import { displayFormatter } from '../format.ts';
  import { keys } from '../query.ts';
  import { scope } from '../scope.ts';
  import { routes } from '../routes.ts';
  import ChangeHistory from '../components/ChangeHistory.svelte';
  import CopyButton from '../components/CopyButton.svelte';
  import JobRef from '../components/JobRef.svelte';
  import Icon from '../components/Icon.svelte';
  import { mergeHistory, type HistoryEvent } from '../components/changeHistory.ts';
  import type { Paged } from '../api.ts';
  import Page from '../components/Page.svelte';
  import DetailLayout from '../components/DetailLayout.svelte';
  import DetailFacts from '../components/DetailFacts.svelte';
  import FocusDisclosure from '../components/FocusDisclosure.svelte';
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

<Page title={worker?.hostName ? 'Worker on ' + worker.hostName : 'Worker'}>
  {#snippet breadcrumb()}<a href={backHref}><Icon name="chevron-left" />Workers</a>{/snippet}
  {#snippet actions()}
    {#if worker}<CopyButton value={supportSummary} label="Copy support summary" />{/if}
    <PageFreshness dataUpdatedAt={detail.dataUpdatedAt} isFetching={detail.isFetching} isError={!!detail.error} {polling} onRefresh={() => detail.refetch()} />
  {/snippet}
  {#if missing}
    <div class="panel"><StateView emptyText="Worker not found." /></div>
  {:else if error}
    <div class="panel"><StateView {error} onRetry={() => detail.refetch()} /></div>
  {:else if worker}
    <section class="entity-summary" aria-label="Worker identity"><StatusBadge status={worker.status} /><a href={routes.namespace(worker.jobNamespace, { namespace: worker.jobNamespace })}>{worker.jobNamespace}</a><JobRef value={worker.workerRef} copy /></section>
    <DetailLayout>
      {#snippet related()}
        <a href={routes.namespace(worker.jobNamespace, { namespace: worker.jobNamespace })}>Namespace</a>
        <a href={routes.jobs({ namespace: worker.jobNamespace })}>Namespace jobs</a>
        <a href={routes.alerts({ namespace: worker.jobNamespace })}>Namespace alerts</a>
      {/snippet}
      <section class="focus-state" style={'--tone:var(--' + (worker.status === 'active' ? 'ok' : worker.status === 'dead' ? 'bad' : 'warn') + ')'} aria-label="Worker health">
        <div class="focus-state-content">
          <h2><Icon name="activity-log" />{worker.status === 'active' ? 'Worker healthy' : worker.status === 'dead' ? 'Worker is offline' : worker.status === 'draining' ? 'Worker is draining' : 'Worker stopped'}</h2>
          <p>{workerStatusInterpretation(worker.status)}</p>
          <div class="focus-facts"><span>Last heartbeat <RelativeTime value={worker.lastSeenAtUtc} /></span><span><strong>{displayFormatter.number(worker.maxConcurrentExecutors)}</strong> executor capacity</span></div>
        </div>
      </section>
      <FocusDisclosure title="Technical details" hint="Process, runtime and dates">
        <DetailFacts facts={[
          { label: 'Host', value: worker.hostName, kind: 'mono' },
          { label: 'Process ID', value: worker.processId ?? 'unknown', kind: 'mono' },
          { label: 'Deployment', value: worker.deploymentVersion, kind: 'mono' },
          { label: 'Acta engine', value: worker.engineVersion ?? 'unknown', kind: 'mono' },
          { label: '.NET', value: worker.dotnetVersion ?? 'unknown', kind: 'mono' },
          { label: 'Worker ref', value: worker.workerRef, kind: 'mono', copy: true, wide: true },
          { label: 'Started', value: worker.startedAtUtc, kind: 'time' },
          { label: 'Modified', value: worker.modifiedAtUtc, kind: 'time' },
        ]} />
      </FocusDisclosure>
      <ChangeHistory history={history.data ?? []} loading={history.isPending} emptyText="No recorded worker lifecycle events." />
    </DetailLayout>
  {:else}<div class="panel"><StateView loading={true} loadingText="Loading worker..." /></div>{/if}
</Page>
