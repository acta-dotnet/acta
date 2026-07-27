<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { api, ApiError, type JobDetailView } from '../api.ts';
  import { TERMINAL_STATUSES, statusClass, displayFormatter } from '../format.ts';
  import { keys } from '../query.ts';
  import { scope } from '../scope.ts';
  import CopyButton from '../components/CopyButton.svelte';
  import PageFreshness from '../components/PageFreshness.svelte';
  import Icon from '../components/Icon.svelte';
  import JobControls from '../components/JobControls.svelte';
  import Page from '../components/Page.svelte';
  import SignalDrawer from '../components/SignalDrawer.svelte';
  import StateView from '../components/StateView.svelte';
  import TagEditor from '../components/TagEditor.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import JobRef from '../components/JobRef.svelte';
  import JobDiagnosis from './job-detail/JobDiagnosis.svelte';
  import JobEventsPanel from './job-detail/JobEventsPanel.svelte';
  import JobLineage from './job-detail/JobLineage.svelte';
  import JobMetadata from './job-detail/JobMetadata.svelte';
  import JobSummary from './job-detail/JobSummary.svelte';
  import JobWorkerEvidence from './job-detail/JobWorkerEvidence.svelte';
  import JobInputPanel from './job-detail/JobInputPanel.svelte';
  import JobResultPanel from './job-detail/JobResultPanel.svelte';
  import JobCheckpointsPanel from './job-detail/JobCheckpointsPanel.svelte';
  import JobSchedulesPanel from './job-detail/JobSchedulesPanel.svelte';
  import { buildIncidentSummary, latestMeaningfulEvent } from './job-detail/jobDetailState.ts';
  import type { JobEvent, JobExplanation } from './job-detail/types.ts';
  import { routes } from '../routes.ts';
  import { capabilitiesQuery, canControl } from '../query.ts';
  import { detailRefetchInterval, livePaused } from '../polling.ts';

  let { jobRef }: { jobRef: string } = $props();
  let jobControls: { openAction(action: string): void } | undefined = $state();
  let signalDrawer: { openForm(name?: string): void } | undefined = $state();
  let eventsPanel: { refresh(): void } | undefined = $state();
  let events: JobEvent[] = $state([]);

  // One aggregate read renders the whole screen: snapshot plus explain, lineage, eligible workers, the
  // definition link, this job's schedules, and the input/result/checkpoint payloads. Only the unbounded
  // event history keeps its own query (JobEventsPanel). Polls on the same paused/terminal rules the
  // snapshot poll used; read the store while building the options so pausing cancels the interval.
  const detailQuery = createQuery(() => {
    const paused = $livePaused;
    return {
      queryKey: keys.detail('jobs', jobRef),
      queryFn: async ({ signal }: { signal: AbortSignal }): Promise<JobDetailView | null> => {
        try {
          return await api<JobDetailView>(`jobs/${jobRef}/detail`, {}, { signal });
        } catch (error) {
          if (error instanceof ApiError && error.status === 404) return null;
          throw error;
        }
      },
      refetchInterval: (query) => {
        const snapshot = query.state.data?.snapshot;
        return detailRefetchInterval(!!snapshot && !TERMINAL_STATUSES.includes(snapshot.status), paused);
      }
    };
  });

  let detail = $derived(detailQuery.data ?? null);
  let job = $derived(detail?.snapshot ?? null);

  // canControl gates only the mutating affordances (Clone/enqueue and the input amend edit); the
  // payload panels themselves render for everyone, since the detail read is on the open read surface.
  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(canControl(capabilities.data));

  let missing = $derived(!detailQuery.isPending && !detailQuery.error && detailQuery.data === null);
  let explanation = $derived(detail?.explain ?? null);
  let lineage = $derived(detail?.lineage ?? null);
  let workers = $derived(detail?.workers ?? null);
  let lastEvent = $derived(latestMeaningfulEvent(events));
  let backHref = $derived(routes.jobs({ namespace: $scope }));
  let incidentSummary = $derived(
    job ? buildIncidentSummary(job, explanation, events, typeof location === 'undefined' ? '' : location.href) : ''
  );

  function errorMessage(error: unknown): string | null {
    return error instanceof Error ? error.message : error ? String(error) : null;
  }

  function reload(): void {
    void detailQuery.refetch();
    eventsPanel?.refresh();
  }

  function scrollTo(id: string): void {
    document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  function runExplainAction(action: JobExplanation['nextActions'][number]): void {
    if (action.kind === 'raise-signal') {
      signalDrawer?.openForm(explanation?.activeWait?.name ?? '');
      scrollTo('job-signals');
    } else if (['pause', 'resume', 'restart', 'cancel'].includes(action.kind)) {
      jobControls?.openAction(action.kind);
      scrollTo('job-actions');
    } else if (action.kind === 'inspect-timeline') {
      scrollTo('job-timeline');
    } else if (action.kind === 'wait-recovery') {
      scrollTo('job-worker-evidence');
    }
  }
</script>

<Page title={job?.jobName ?? 'Job'}>
  {#snippet breadcrumb()}<a href={backHref}><Icon name="chevron-left" />Jobs</a>{/snippet}
  {#snippet actions()}
    {#if canControlNow && job}
      <a class="clone-action" href={routes.enqueue({ namespace: job.jobNamespace, jobName: job.jobName, from: job.jobRef })}><Icon name="copy" />Clone</a>
    {/if}
    <CopyButton value={jobRef} label="Copy ref" />
    <PageFreshness
      dataUpdatedAt={detailQuery.dataUpdatedAt}
      isFetching={detailQuery.isFetching}
      isError={!!detailQuery.error}
      polling={!!job && !TERMINAL_STATUSES.includes(job.status)}
      onRefresh={reload} />
  {/snippet}

  {#if missing}
    <div class="panel"><StateView emptyText="Job not found." /></div>
  {:else if detailQuery.error}
    <div class="panel"><StateView error={errorMessage(detailQuery.error)} onRetry={() => detailQuery.refetch()} /></div>
  {:else if job && detail}
    {#if explanation?.headline}
      {@const tone = statusClass(job.status) === 'bad' ? 'bad' : ['warn', 'held'].includes(statusClass(job.status)) ? 'warn' : 'ok'}
      <div class="verdict {tone}">
        <span class="verdict-label">{explanation.headline}</span>
        {#if explanation.reason}<span class="verdict-reason">{explanation.reason}</span>{/if}
        {#if tone !== 'ok'}
          {#each explanation.nextActions as action}
            {#if ['raise-signal', 'pause', 'resume', 'restart', 'cancel', 'inspect-timeline', 'wait-recovery'].includes(action.kind)}
              <button onclick={() => runExplainAction(action)}>{action.description}</button>
            {/if}
          {/each}
        {/if}
      </div>
    {/if}

    <section class="entity-summary" aria-label="Job identity">
      <div class="entity-meta mono"><JobRef value={job.jobRef} /> · {job.jobNamespace} / {job.jobName} · attempt {displayFormatter.number(job.executionNumber)}</div>
      <StatusBadge status={job.status} />
    </section>

    <div class="detail-workspace">
      <div class="detail-main">
        <JobLineage {job} {lineage} />
        <JobSummary {job} tenantKey={detail.tenantKey} {lastEvent} />
        <JobDiagnosis jobNamespace={job.jobNamespace} {explanation} />
        <JobWorkerEvidence {job} {workers} workersTotal={detail.workersTotal ?? null} />
        <JobSchedulesPanel schedules={detail.schedules} total={detail.schedulesTotal} onChanged={reload} />
        <JobInputPanel input={detail.input} {jobRef} status={job.status} canControl={canControlNow} onAmended={reload} />
        <JobResultPanel result={detail.result} />
        <JobCheckpointsPanel checkpoints={detail.checkpoints} />
      </div>

      <aside class="detail-rail">
        <div id="job-actions"><JobControls bind:this={jobControls} {jobRef} status={job.status} priority={job.priority} embedded={true} onChanged={reload} /></div>
        <div id="job-signals"><SignalDrawer bind:this={signalDrawer} {jobRef} embedded={true} onSent={reload} /></div>
        <section class="detail-panel go-to" aria-label="Go to">
          <p class="detail-kicker">Go to</p>
          <nav>
            <a href={routes.definition(job.jobDefinitionId, { namespace: job.jobNamespace })}>Definition</a>
            <a href={routes.jobs({ jobName: job.jobName, namespace: job.jobNamespace })}>Similar jobs</a>
            <a href={routes.namespace(job.jobNamespace, { namespace: job.jobNamespace })}>Namespace</a>
            <a href={routes.workers({ namespace: job.jobNamespace })}>Workers</a>
          </nav>
        </section>
        <TagEditor path={`jobs/${jobRef}/tags`} />
      </aside>
    </div>

    <JobEventsPanel
      bind:this={eventsPanel}
      {jobRef}
      enabled={!!job}
      polling={!TERMINAL_STATUSES.includes(job.status)}
      onEventsChange={(loaded) => (events = loaded)} />

    {#if incidentSummary}
      <div class="support-row"><CopyButton value={incidentSummary} label="Copy incident summary" showLabel={true} /></div>
    {/if}

    <JobMetadata {job} />
  {:else}
    <div class="panel"><StateView loading={true} loadingText="Loading job..." /></div>
  {/if}
</Page>

<style>
  .support-row { display: flex; justify-content: flex-end; margin-bottom: 8px; }
  .clone-action {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 5px 12px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    color: var(--ink);
  }
  .clone-action:hover { border-color: var(--accent); color: var(--accent); }
  @container (max-width: 700px) {
    .detail-rail :global(#job-actions) { position: sticky; top: 8px; z-index: 3; }
  }
</style>
