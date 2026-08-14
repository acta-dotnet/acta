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
  import JobExecutionsPanel from './job-detail/JobExecutionsPanel.svelte';
  import JobSchedulesPanel from './job-detail/JobSchedulesPanel.svelte';
  import { createUrlFilters } from '../urlFilters.ts';
  import { buildIncidentSummary, latestMeaningfulEvent } from './job-detail/jobDetailState.ts';
  import { statusTonePresentation } from '../components/jobTimelineState.ts';
  import type { JobEvent } from './job-detail/types.ts';
  import { routes } from '../routes.ts';
  import { capabilitiesQuery, canControl } from '../query.ts';
  import { detailRefetchInterval, livePaused } from '../polling.ts';

  let { jobRef }: { jobRef: string } = $props();
  let eventsPanel: { refresh(): void } | undefined = $state();
  let executionsPanel: { refresh(): void } | undefined = $state();
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
    executionsPanel?.refresh();
  }

  function scrollTo(id: string): void {
    document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  // Hero: the timeline's node language scaled up onto the shared t-* tone classes.
  let hero = $derived(job ? statusTonePresentation(statusClass(job.status)) : statusTonePresentation(''));

  // Real tabs: only the active tab's panels are mounted, so per-tab data (the unbounded event
  // history lives on Details) is only fetched while its tab is open.
  type DetailTab = 'details' | 'executions' | 'input' | 'result' | 'checkpoints' | 'schedules' | 'lineage';
  const tabs: { id: DetailTab; label: string }[] = [
    { id: 'details', label: 'Details' },
    { id: 'executions', label: 'Executions' },
    { id: 'input', label: 'Input' },
    { id: 'result', label: 'Result' },
    { id: 'checkpoints', label: 'Checkpoints' },
    { id: 'schedules', label: 'Schedules' },
    { id: 'lineage', label: 'Lineage' }
  ];
  // Hash-backed so the tab and a specific execution deep-link and survive back/forward. Switching
  // tabs by hand clears the drilled execution: only the walk action carries it, so a user-cleared
  // timeline filter cannot snap back on the next remount.
  const tabFilters = createUrlFilters({ tab: 'tab', execution: 'execution' }, { tab: '', execution: '' });
  const TAB_IDS: readonly string[] = tabs.map((tab) => tab.id);
  function setTab(id: DetailTab): void {
    tabFilters.patch({ tab: id === 'details' ? '' : id, execution: '' });
  }
  let focusExecution = $derived(/^\d+$/.test($tabFilters.execution) ? Number($tabFilters.execution) : null);
  // Executions → timeline handoff: the drilled execution rides in the URL (shareable, reload-safe)
  // and presets the timeline's attempt filter. The scroll must wait for the Details tab to actually
  // mount: hashchange lands asynchronously, so scrolling in the click handler races an empty DOM.
  let pendingScroll: string | null = $state(null);
  function viewInTimeline(executionNumber: number): void {
    tabFilters.patch({ tab: '', execution: String(executionNumber) });
    pendingScroll = 'job-timeline';
  }
  $effect(() => {
    if (!pendingScroll || activeTab !== 'details') return;
    const target = pendingScroll;
    pendingScroll = null;
    void tick().then(() => scrollTo(target));
  });
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
  let hasExecutions = $derived(!!job && job.executionNumber > 0);
  // The active tab must always be a rendered tab button: a deep link to a gated tab falls to Details.
  let activeTab: DetailTab = $derived.by(() => {
    const requested = TAB_IDS.includes($tabFilters.tab) ? ($tabFilters.tab as DetailTab) : 'details';
    if (requested === 'executions' && !hasExecutions) return 'details';
    if (requested === 'lineage' && !hasLineage) return 'details';
    return requested;
  });

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
        <div class="hero-meta">
          <JobRef value={job.jobRef} /> · {job.jobNamespace} / {job.jobName} ·
          {#if hasExecutions}<button class="hero-attempt" onclick={() => setTab('executions')}>attempt {displayFormatter.number(job.executionNumber)}</button>{:else}attempt {displayFormatter.number(job.executionNumber)}{/if}
        </div>
      </div>
    </section>

    <div class="secnav-sentinel" bind:this={secnavSentinel} aria-hidden="true"></div>
    <nav class="secnav" class:stuck={secnavStuck} aria-label="Job detail sections">
      {#each tabs as tab (tab.id)}
        {#if (tab.id !== 'lineage' || hasLineage) && (tab.id !== 'executions' || hasExecutions)}
          <button class:active={activeTab === tab.id} onclick={() => setTab(tab.id)}>{tab.label}</button>
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
            initialAttempt={focusExecution}
            onEventsChange={(loaded) => (events = loaded)} />
          <div class="support-row">
            <CopyButton value={JSON.stringify(job, null, 2)} label="Copy raw snapshot" showLabel={true} />
            {#if incidentSummary}<CopyButton value={incidentSummary} label="Copy incident summary" showLabel={true} />{/if}
          </div>
        {:else if activeTab === 'executions'}
          <JobExecutionsPanel
            bind:this={executionsPanel}
            {jobRef}
            snapshot={job}
            polling={!TERMINAL_STATUSES.includes(job.status)}
            {focusExecution}
            onViewInTimeline={viewInTimeline} />
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
        <div id="job-actions"><JobControls {jobRef} status={job.status} priority={job.priority} embedded={true} onChanged={reload} /></div>
        <div id="job-signals"><SignalDrawer {jobRef} embedded={true} suggestedName={explanation?.activeWait?.name ?? ''} onSent={reload} /></div>
        <section class="detail-panel go-to" aria-label="Go to">
          <p class="detail-kicker">Go to</p>
          <nav>
            <a href={routes.definition(job.definitionId, { namespace: job.jobNamespace })}>Definition</a>
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
  .hero-attempt {
    padding: 0; border: 0; background: none; cursor: pointer;
    color: inherit; font: inherit; text-decoration: underline; text-underline-offset: 2px;
  }
  .hero-attempt:hover:not(:disabled) { color: var(--accent); border-color: transparent; }
  @container (max-width: 700px) {
    .detail-rail :global(#job-actions) { position: sticky; top: 8px; z-index: 3; }
  }
</style>
