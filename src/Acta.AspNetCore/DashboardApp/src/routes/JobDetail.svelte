<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { api, ApiError, type Paged } from '../api.ts';
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
  import { buildIncidentSummary, latestMeaningfulEvent } from './job-detail/jobDetailState.ts';
  import type { JobEvent, JobExplanation, JobLineage as JobLineageData, JobSnapshot, JobWorker } from './job-detail/types.ts';
  import { routes } from '../routes.ts';
  import { detailRefetchInterval, livePaused } from '../polling.ts';

  let { jobRef }: { jobRef: string } = $props();
  let jobControls: { openAction(action: string): void } | undefined = $state();
  let signalDrawer: { openForm(name?: string): void } | undefined = $state();
  let eventsPanel: { refresh(): void } | undefined = $state();
  let events: JobEvent[] = $state([]);

  const jobQuery = createQuery(() => {
    // Read the store while building the options so pausing immediately cancels the active interval.
    const paused = $livePaused;
    return {
      queryKey: keys.detail('jobs', jobRef),
      queryFn: async ({ signal }: { signal: AbortSignal }): Promise<JobSnapshot | null> => {
        try {
          return await api<JobSnapshot>(`jobs/${jobRef}`, {}, { signal });
        } catch (error) {
          if (error instanceof ApiError && error.status === 404) return null;
          throw error;
        }
      },
      refetchInterval: (query) => {
        const job = query.state.data;
        return detailRefetchInterval(!!job && !TERMINAL_STATUSES.includes(job.status), paused);
      }
    };
  });

  let job = $derived(jobQuery.data ?? null);

  const explanationQuery = createQuery(() => ({
    queryKey: keys.detail('job-explanation', jobRef),
    queryFn: ({ signal }: { signal: AbortSignal }) => api<JobExplanation>(`jobs/${jobRef}/explain`, {}, { signal }),
    enabled: !!job
  }));

  const lineageQuery = createQuery(() => ({
    queryKey: keys.detail('job-lineage', jobRef),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      api<JobLineageData>(`jobs/${jobRef}/lineage`, { childLimit: 100 }, { signal }),
    enabled: !!job
  }));

  const workersQuery = createQuery(() => {
    const snapshot = job;
    return {
      queryKey: keys.list('job-eligible-workers', {
        jobRef,
        jobNamespace: snapshot?.jobNamespace ?? ''
      }),
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        api<Paged<JobWorker>>('workers', { jobNamespace: snapshot!.jobNamespace, pageSize: 50 }, { signal }),
      enabled: snapshot?.status === 'ready'
    };
  });

  // The job snapshot owns the polling cadence. After every successful snapshot refresh, update the
  // evidence derived from that snapshot as one coordinated set. This also performs one final evidence
  // refresh when a live job becomes terminal and its snapshot polling turns off.
  let synchronizedJobUpdatedAt = 0;
  $effect(() => {
    const updatedAt = jobQuery.dataUpdatedAt;
    const snapshot = job;
    if (!snapshot || updatedAt <= synchronizedJobUpdatedAt) return;

    const initialLoad = synchronizedJobUpdatedAt === 0;
    synchronizedJobUpdatedAt = updatedAt;
    // The enabled queries fetch on the initial snapshot; avoid immediately restarting those requests.
    if (initialLoad) return;

    void explanationQuery.refetch();
    void lineageQuery.refetch();
    if (snapshot.status === 'ready') void workersQuery.refetch();
  });

  // Jobs carry no definition id, so the definition link resolves by namespace + name through the
  // definitions list (same guard-loop idiom ScheduleDetail uses); best-effort, link renders when found.
  const definitionQuery = createQuery(() => ({
    queryKey: keys.detail('job-definition', job ? `${job.jobNamespace}/${job.jobName}` : ''),
    queryFn: async ({ signal }: { signal: AbortSignal }) => {
      let cursor: string | undefined;
      for (let guard = 0; guard < 100; guard++) {
        const page = await api<Paged<{ jobDefinitionId: number; jobName: string }>>(
          'definitions',
          { jobNamespace: job!.jobNamespace, pageSize: 100, cursor },
          { signal }
        );
        const match = page.items.find((item) => item.jobName === job!.jobName);
        if (match) return match;
        if (!page.hasMore || !page.nextCursor) break;
        cursor = page.nextCursor;
      }
      return null;
    },
    enabled: !!job,
    staleTime: 5 * 60 * 1000
  }));

  let missing = $derived(!jobQuery.isPending && !jobQuery.error && jobQuery.data === null);
  let explanation = $derived(explanationQuery.data ?? null);
  let lineage = $derived(lineageQuery.data ?? null);
  let workers = $derived(workersQuery.data?.items ?? null);
  let lastEvent = $derived(latestMeaningfulEvent(events));
  let backHref = $derived(routes.jobs({ namespace: $scope }));
  let incidentSummary = $derived(
    job ? buildIncidentSummary(job, explanation, events, typeof location === 'undefined' ? '' : location.href) : ''
  );

  function errorMessage(error: unknown): string | null {
    return error instanceof Error ? error.message : error ? String(error) : null;
  }

  function reload(): void {
    void jobQuery.refetch();
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
    <CopyButton value={jobRef} label="Copy ref" />
    <PageFreshness
      dataUpdatedAt={jobQuery.dataUpdatedAt}
      isFetching={jobQuery.isFetching}
      isError={!!jobQuery.error}
      polling={!!job && !TERMINAL_STATUSES.includes(job.status)}
      onRefresh={reload} />
  {/snippet}

  {#if missing}
    <div class="panel"><StateView emptyText="Job not found." /></div>
  {:else if jobQuery.error}
    <div class="panel"><StateView error={errorMessage(jobQuery.error)} onRetry={() => jobQuery.refetch()} /></div>
  {:else if job}
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
        <JobLineage
          {job}
          {lineage}
          loading={lineageQuery.isPending}
          error={errorMessage(lineageQuery.error)} />
        <JobSummary {job} {lastEvent} />
        <JobDiagnosis
          jobNamespace={job.jobNamespace}
          {explanation}
          loading={explanationQuery.isPending}
          error={errorMessage(explanationQuery.error)} />
        <JobWorkerEvidence
          {job}
          {workers}
          loading={workersQuery.isPending && workersQuery.fetchStatus === 'fetching'}
          error={errorMessage(workersQuery.error)} />
      </div>

      <aside class="detail-rail">
        <div id="job-actions"><JobControls bind:this={jobControls} {jobRef} status={job.status} priority={job.priority} embedded={true} onChanged={reload} /></div>
        <div id="job-signals"><SignalDrawer bind:this={signalDrawer} {jobRef} embedded={true} onSent={reload} /></div>
        <section class="detail-panel go-to" aria-label="Go to">
          <p class="detail-kicker">Go to</p>
          <nav>
            {#if definitionQuery.data}<a href={routes.definition(definitionQuery.data.jobDefinitionId, { namespace: job.jobNamespace })}>Definition</a>{/if}
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
  @container (max-width: 700px) {
    .detail-rail :global(#job-actions) { position: sticky; top: 8px; z-index: 3; }
  }
</style>
