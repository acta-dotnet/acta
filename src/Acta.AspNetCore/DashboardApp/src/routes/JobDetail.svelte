<script lang="ts">
  import { tick } from 'svelte';
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
  import JobRef from '../components/JobRef.svelte';
  import JobDiagnosis from './job-detail/JobDiagnosis.svelte';
  import JobEventsPanel from './job-detail/JobEventsPanel.svelte';
  import JobLineage from './job-detail/JobLineage.svelte';
  import JobSummary from './job-detail/JobSummary.svelte';
  import JobWorkerEvidence from './job-detail/JobWorkerEvidence.svelte';
  import JobInputPanel from './job-detail/JobInputPanel.svelte';
  import JobResultPanel from './job-detail/JobResultPanel.svelte';
  import JobCheckpointsPanel from './job-detail/JobCheckpointsPanel.svelte';
  import JobSchedulesPanel from './job-detail/JobSchedulesPanel.svelte';
  import { buildIncidentSummary, latestMeaningfulEvent } from './job-detail/jobDetailState.ts';
  import { statusTonePresentation } from '../components/jobTimelineState.ts';
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

  // Hero: the timeline's node language scaled up onto the shared t-* tone classes.
  let hero = $derived(job ? statusTonePresentation(statusClass(job.status)) : statusTonePresentation(''));

  // Real tabs: only the active tab's panels are mounted, so per-tab data (the unbounded event
  // history lives on Details) is only fetched while its tab is open.
  type DetailTab = 'details' | 'input' | 'result' | 'checkpoints' | 'schedules' | 'lineage';
  const tabs: { id: DetailTab; label: string }[] = [
    { id: 'details', label: 'Details' },
    { id: 'input', label: 'Input' },
    { id: 'result', label: 'Result' },
    { id: 'checkpoints', label: 'Checkpoints' },
    { id: 'schedules', label: 'Schedules' },
    { id: 'lineage', label: 'Lineage' }
  ];
  let activeTab: DetailTab = $state('details');
  // Mobile: the floating nav toggle overlaps the pinned tab bar, so the bar only takes its
  // toggle-clearing padding while actually stuck. A sentinel above the bar detects pinning.
  let secnavStuck = $state(false);
  let secnavSentinel: HTMLElement | undefined = $state();
  $effect(() => {
    if (!secnavSentinel) return;
    const observer = new IntersectionObserver(([entry]) => (secnavStuck = !entry.isIntersecting));
    observer.observe(secnavSentinel);
    return () => observer.disconnect();
  });
  let hasLineage = $derived(
    !!lineage && (lineage.ancestors.length > 0 || lineage.children.length > 0 || lineage.steps.length > 0 || !!lineage.activeWait)
  );

  function runExplainAction(action: JobExplanation['nextActions'][number]): void {
    if (action.kind === 'raise-signal') {
      signalDrawer?.openForm(explanation?.activeWait?.name ?? '');
      scrollTo('job-signals');
    } else if (['pause', 'resume', 'restart', 'cancel'].includes(action.kind)) {
      jobControls?.openAction(action.kind);
      scrollTo('job-actions');
    } else if (action.kind === 'inspect-timeline') {
      activeTab = 'details';
      void tick().then(() => scrollTo('job-timeline'));
    } else if (action.kind === 'wait-recovery') {
      activeTab = 'details';
      void tick().then(() => scrollTo('job-worker-evidence'));
    }
  }
</script>

<Page title={job?.jobName ?? 'Job'} crumbBar={true}>
  {#snippet breadcrumb()}
    <a href={backHref} class="crumb-back" aria-label="Back to jobs"><Icon name="chevron-left" /></a>
    <a href={backHref}>Jobs</a>
    <span class="crumb-sep" aria-hidden="true">/</span>
    <h1 class="crumb-current mono">{jobRef}</h1>
  {/snippet}
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
    <section class="job-hero t-{hero.tone}" aria-label="Job status">
      <span class="hero-node"><Icon name={hero.icon} /></span>
      <div class="hero-body">
        <div class="hero-status">{explanation?.headline ?? job.status}</div>
        {#if explanation?.reason}<div class="hero-reason">{explanation.reason}</div>{/if}
        <div class="hero-meta"><JobRef value={job.jobRef} /> · {job.jobNamespace} / {job.jobName} · attempt {displayFormatter.number(job.executionNumber)}</div>
      </div>
      {#if explanation && hero.tone !== 'ok' && hero.tone !== 'run'}
        <div class="hero-actions">
          {#each explanation.nextActions as action}
            {#if ['raise-signal', 'pause', 'resume', 'restart', 'cancel', 'inspect-timeline', 'wait-recovery'].includes(action.kind)}
              <button onclick={() => runExplainAction(action)}>{action.description}</button>
            {/if}
          {/each}
        </div>
      {/if}
    </section>

    <div class="secnav-sentinel" bind:this={secnavSentinel} aria-hidden="true"></div>
    <nav class="secnav" class:stuck={secnavStuck} aria-label="Job detail sections">
      {#each tabs as tab (tab.id)}
        {#if tab.id !== 'lineage' || hasLineage}
          <button class:active={activeTab === tab.id} onclick={() => (activeTab = tab.id)}>{tab.label}</button>
        {/if}
      {/each}
    </nav>

    <div class="detail-workspace">
      <div class="detail-main">
        {#if activeTab === 'details'}
          <JobSummary {job} tenantKey={detail.tenantKey} {lastEvent} maxAttempts={detail.maxAttemptsEffective ?? null} />
          <JobDiagnosis jobNamespace={job.jobNamespace} {explanation} />
          <JobWorkerEvidence {job} {workers} workersTotal={detail.workersTotal ?? null} />
          <JobEventsPanel
            bind:this={eventsPanel}
            {jobRef}
            enabled={!!job}
            polling={!TERMINAL_STATUSES.includes(job.status)}
            nextRunAtUtc={TERMINAL_STATUSES.includes(job.status) ? null : job.nextRunAtUtc}
            onEventsChange={(loaded) => (events = loaded)} />
          <div class="support-row">
            <CopyButton value={JSON.stringify(job, null, 2)} label="Copy raw snapshot" showLabel={true} />
            {#if incidentSummary}<CopyButton value={incidentSummary} label="Copy incident summary" showLabel={true} />{/if}
          </div>
        {:else if activeTab === 'input'}
          <JobInputPanel input={detail.input} {jobRef} status={job.status} canControl={canControlNow} onAmended={reload} />
        {:else if activeTab === 'result'}
          <JobResultPanel result={detail.result} />
        {:else if activeTab === 'checkpoints'}
          <JobCheckpointsPanel checkpoints={detail.checkpoints} />
        {:else if activeTab === 'schedules'}
          <JobSchedulesPanel schedules={detail.schedules} total={detail.schedulesTotal} onChanged={reload} />
        {:else if activeTab === 'lineage'}
          <JobLineage {job} {lineage} />
        {/if}
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
  {:else}
    <div class="panel"><StateView loading={true} loadingText="Loading job..." /></div>
  {/if}
</Page>

<style>
  .support-row { display: flex; justify-content: flex-end; gap: 10px; margin-bottom: 8px; }
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
