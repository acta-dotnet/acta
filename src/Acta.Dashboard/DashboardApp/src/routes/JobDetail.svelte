<script lang="ts">
  import { tick } from 'svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { api, ApiError, type EventItem, type JobDetailView } from '../api.ts';
  import { consecutiveFailuresText, TERMINAL_STATUSES, statusClass } from '../format.ts';
  import { keys } from '../query.ts';
  import { scope } from '../scope.ts';
  import CopyButton from '../components/CopyButton.svelte';
  import PageFreshness from '../components/PageFreshness.svelte';
  import Icon from '../components/Icon.svelte';
  import JobControls from '../components/JobControls.svelte';
  import Page from '../components/Page.svelte';
  import DetailActions from '../components/DetailActions.svelte';
  import DetailLayout from '../components/DetailLayout.svelte';
  import FocusDisclosure from '../components/FocusDisclosure.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import SignalDrawer from '../components/SignalDrawer.svelte';
  import StateView from '../components/StateView.svelte';
  import TagEditor from '../components/TagEditor.svelte';
  import JobRef from '../components/JobRef.svelte';
  import JobDiagnosis from './job-detail/JobDiagnosis.svelte';
  import JobEventsPanel from './job-detail/JobEventsPanel.svelte';
  import JobLineage from './job-detail/JobLineage.svelte';
  import JobNextRun from './job-detail/JobNextRun.svelte';
  import JobSummary from './job-detail/JobSummary.svelte';
  import JobWorkerEvidence from './job-detail/JobWorkerEvidence.svelte';
  import JobInputPanel from './job-detail/JobInputPanel.svelte';
  import JobResultPanel from './job-detail/JobResultPanel.svelte';
  import JobCheckpointsPanel from './job-detail/JobCheckpointsPanel.svelte';
  import JobExecutionsPanel from './job-detail/JobExecutionsPanel.svelte';
  import JobSchedulesPanel from './job-detail/JobSchedulesPanel.svelte';
  import { createUrlFilters } from '../urlFilters.ts';
  import { activeWaitLabel, buildIncidentSummary, executionLabel } from './job-detail/jobDetailState.ts';
  import { statusTonePresentation } from '../components/jobTimelineState.ts';
  import { routes } from '../routes.ts';
  import { capabilitiesQuery, can, Permission } from '../query.ts';
  import { detailRefetchInterval, livePaused } from '../polling.ts';
  import { jobControlState } from '../components/jobControlState.ts';

  // The route's target: a ref, or id:<n> where the host allows numeric lookup. Only the detail read uses it; once the
  // job loads, every call names it by its ref, since a control or a tag write takes a ref alone.
  let { jobRef }: { jobRef: string } = $props();
  let eventsPanel: { refresh(): void } | undefined = $state();
  let executionsPanel: { refresh(): void } | undefined = $state();
  let events: EventItem[] = $state([]);

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
          // The route answers 400 only for a target that does not parse, which names no job either, and a retry cannot fix.
          if (error instanceof ApiError && (error.status === 404 || error.status === 400)) return null;
          throw error;
        }
      },
      refetchInterval: (query) => {
        const row = query.state.data?.job;
        return detailRefetchInterval(!!row && !TERMINAL_STATUSES.includes(row.status), paused);
      }
    };
  });

  let detail = $derived(detailQuery.data ?? null);
  let job = $derived(detail?.job ?? null);

  // The permissions gate only the mutating affordances: Clone enqueues, and the input amend edit is a
  // control. The payload panels render for everyone, since the detail read is on the read surface.
  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(can(capabilities.data, Permission.control));
  let canEnqueueNow = $derived(can(capabilities.data, Permission.enqueue));
  let canPurgeNow = $derived(!!job && can(capabilities.data, Permission.admin) && jobControlState(job.status, job.kind).canPurge);
  // Once the caller could act here the controls stay mounted: a control refused because the grant was revoked
  // keeps its message, and the refreshed permissions then say that no action is open.
  let controlsSeen = $state(false);
  $effect(() => {
    if (canControlNow) controlsSeen = true;
  });
  let showControls = $derived(canControlNow || controlsSeen);

  let missing = $derived(!detailQuery.isPending && !detailQuery.error && detailQuery.data === null);
  let explanation = $derived(detail?.explain ?? null);
  let lineage = $derived(detail?.lineage ?? null);
  let workers = $derived(detail?.workers ?? null);
  let lineagePanel: { inspect(ref: string): void } | undefined = $state();
  let activityOpen = $state(false);
  let executionsOpen = $state(false);
  let dataOpen = $state(false);
  let schedulesOpen = $state(false);
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
    document.getElementById(id)?.scrollIntoView({ behavior: 'instant', block: 'start' });
  }

  let hero = $derived(statusTonePresentation(job ? statusClass(job.status) : ''));
  let heroAttempt = $derived(job ? executionLabel(job) : '');
  let hasExecutions = $derived(!!job && job.executionNumber > 0);
  let hasLineage = $derived(!!lineage && (lineage.ancestors.length > 0 || lineage.children.length > 0));
  let failedChild = $derived(lineage?.children.find((child) => child.status === 'failed'));
  let signalWait = $derived(explanation?.activeWait?.kind.toLowerCase() === 'signal');
  // The status panel's inline action is the failed child, a signal wait's sender, or the job's primary
  // control. The Actions menu leaves that control out only when the panel actually shows it.
  let controlsMode = $derived<'all' | 'secondary'>(failedChild || signalWait ? 'all' : 'secondary');
  // MaxAttempts is the job's budget, or each firing's on a permanent job, where a spent one returns it to idle.
  let budgetText = $derived(
    !job || detail?.maxAttemptsEffective == null || TERMINAL_STATUSES.includes(job.status) ? ''
      : job.kind === 'permanent' ? 'each firing gets ' + detail.maxAttemptsEffective + ' attempts'
      : 'fails permanently at ' + detail.maxAttemptsEffective
  );
  let hasData = $derived(!!detail && (!!detail.input || detail.checkpoints.items.length > 0));
  let hasSchedules = $derived(!!detail && (detail.schedules.length > 0 || (detail.schedulesTotal ?? 0) > 0));
  const tabFilters = createUrlFilters({ tab: 'tab', execution: 'execution' }, { tab: '', execution: '' });
  let focusExecution = $derived(/^\d+$/.test($tabFilters.execution) ? Number($tabFilters.execution) : null);

  // Existing section and attempt URLs open the matching disclosure, including back/forward navigation. The
  // effect follows the URL's values, not every hash change, so a disclosure opened by hand stays as it is.
  let requestedTab = $derived($tabFilters.tab);
  let requestedExecution = $derived($tabFilters.execution);
  $effect(() => {
    const requested = requestedTab;
    if (requested === 'executions') { activityOpen = true; executionsOpen = true; }
    else executionsOpen = false;
    if (requested === 'checkpoints' || requested === 'input' || requested === 'result') dataOpen = true;
    if (requested === 'schedules') schedulesOpen = true;
    if (requestedExecution) activityOpen = true;
  });

  function viewInTimeline(executionNumber: number): void {
    tabFilters.patch({ tab: '', execution: String(executionNumber) });
    activityOpen = true;
    executionsOpen = false;
    void tick().then(() => scrollTo('job-timeline'));
  }

  function showExecutions(): void {
    activityOpen = true;
    executionsOpen = true;
    tabFilters.patch({ tab: 'executions', execution: '' });
    void tick().then(() => scrollTo('job-executions-section'));
  }
</script>

<Page title={job?.jobName ?? 'Job'}>
  {#snippet breadcrumb()}<a href={backHref}><Icon name="chevron-left" />Jobs</a>{/snippet}
  {#snippet actions()}
    <PageFreshness dataUpdatedAt={detailQuery.dataUpdatedAt} isFetching={detailQuery.isFetching} isError={!!detailQuery.error} polling={!!job && !TERMINAL_STATUSES.includes(job.status)} onRefresh={reload} />

    {#if job && detail && (showControls || canEnqueueNow || canPurgeNow)}
      <DetailActions>
        {#if showControls || canPurgeNow}<div id="job-actions"><JobControls jobRef={job.jobRef} status={job.status} kind={job.kind} priority={job.priority} mode={controlsMode} onChanged={reload} /></div>{/if}
        {#if showControls && (failedChild || !signalWait)}<div id="job-signals"><SignalDrawer jobRef={job.jobRef} suggestedName={explanation?.activeWait?.name ?? ''} onSent={reload} /></div>{/if}
        {#if canEnqueueNow}<a class="clone-action" href={routes.enqueue({ namespace: job.jobNamespace, jobName: job.jobName, from: job.jobRef })}><Icon name="copy" />Clone job</a>{/if}

      </DetailActions>
    {/if}
  {/snippet}

  {#if missing}
    <div class="panel"><StateView emptyText={`Job ${jobRef} not found.`} /></div>
  {:else if detailQuery.error}
    <div class="panel"><StateView error={errorMessage(detailQuery.error)} onRetry={() => detailQuery.refetch()} /></div>
  {:else if job && detail}
    <section class="entity-summary" aria-label="Job identity">
      <StatusBadge status={job.status} />
      <a href={routes.namespace(job.jobNamespace, { namespace: job.jobNamespace })}>{job.jobNamespace}</a>
      <JobRef value={job.jobRef} copy />
      {#if job.kind === 'permanent'}<span class="dim">Permanent job</span>{/if}
    </section>

    <section class="focus-state t-{hero.tone}" aria-label="Job status">
      <div class="focus-state-content">
        <h2><Icon name={hero.icon} />{explanation?.headline ?? job.status}</h2>
        {#if explanation?.reason}<p>{explanation.reason}</p>{/if}
        {#if explanation?.lease?.expired}<p>{explanation.lease.recoveryExpectation}</p>{/if}
        <div class="focus-facts">
          {#if explanation?.activeWait}
            {@const wait = activeWaitLabel(explanation.activeWait.kind, explanation.activeWait.name)}
            <span>Waiting on <strong class="mono">{wait.name}</strong>{#if explanation.activeWait.dueAtUtc}{' · due '}<RelativeTime value={explanation.activeWait.dueAtUtc} />{/if}</span>
          {/if}
          {#if job.status === 'blocked' && job.blockedBehindJobRef}<span>Blocked behind <JobRef value={job.blockedBehindJobRef} href={routes.job(job.blockedBehindJobRef, { namespace: job.jobNamespace })} /></span>{/if}
          {#if !TERMINAL_STATUSES.includes(job.status) && (job.nextRunAtUtc || job.status === 'idle')}<span>Next run <JobNextRun {job} schedules={detail.schedules} total={detail.schedulesTotal} /></span>{/if}
          {#if hasExecutions}<button class="hero-attempt" onclick={showExecutions}>{heroAttempt}</button>{/if}
          {#if job.failureCount > 0 && job.status !== 'succeeded'}<span>{consecutiveFailuresText(job.failureCount) + (job.kind === 'permanent' ? ' in the latest firing' : '') + (budgetText ? ', ' + budgetText : '')}</span>{/if}
        </div>
      </div>
      <div class="focus-inline-actions">
        {#if failedChild}
          <button class="primary" onclick={() => lineagePanel?.inspect(failedChild!.jobRef)}>Inspect failed child<Icon name="chevron-right" /></button>
        {:else if showControls && signalWait}
          <SignalDrawer jobRef={job.jobRef} suggestedName={explanation?.activeWait?.name ?? ''} inline={true} onSent={reload} />
        {:else if showControls}
          <JobControls jobRef={job.jobRef} status={job.status} kind={job.kind} priority={job.priority} mode="primary" onChanged={reload} />
        {/if}
      </div>
    </section>

    <DetailLayout>

      {#snippet related()}
        <a href={routes.definition(job.jobNamespace, job.jobName, { namespace: job.jobNamespace })}>Definition</a>
        <a href={routes.jobs({ jobName: job.jobName, namespace: job.jobNamespace })}>Similar jobs</a>
        <a href={routes.namespace(job.jobNamespace, { namespace: job.jobNamespace })}>Namespace</a>
        <a href={routes.workers({ namespace: job.jobNamespace })}>Workers</a>
        <a href={routes.events({ namespace: job.jobNamespace, jobRef: job.jobRef })}>Events</a>
      {/snippet}

      {#if hasLineage}<JobLineage bind:this={lineagePanel} {job} {lineage} />{/if}
      <JobWorkerEvidence {job} {workers} workersTotal={detail.workersTotal ?? null} />
      {#if detail.result}<JobResultPanel result={detail.result} resultTypeName={detail.resultTypeName} />{/if}

      <FocusDisclosure title="Activity" hint={hasExecutions ? heroAttempt : 'Events and history'} bind:open={activityOpen} id="job-activity">
        <div hidden={executionsOpen}>
        {#key focusExecution}
          <JobEventsPanel bind:this={eventsPanel} jobRef={job.jobRef} polling={!TERMINAL_STATUSES.includes(job.status)} nextRunAtUtc={TERMINAL_STATUSES.includes(job.status) ? null : job.nextRunAtUtc} initialAttempt={focusExecution} permanent={job.kind === 'permanent'} onEventsChange={(loaded) => (events = loaded)} />
        {/key}
        </div>
        {#if hasExecutions}
          <FocusDisclosure title="Executions" hint="Attempts, durations and outcomes" bind:open={executionsOpen} lazy={true} hideHeading={true} id="job-executions-section">
            <JobExecutionsPanel bind:this={executionsPanel} jobRef={job.jobRef} {job} polling={!TERMINAL_STATUSES.includes(job.status)} {focusExecution} onViewInTimeline={viewInTimeline} />
          </FocusDisclosure>
        {/if}
      </FocusDisclosure>

      {#if hasData}
        <FocusDisclosure title={detail.checkpoints.items.length ? 'Data and checkpoints' : 'Stored data'} hint={(detail.input ? 'Input' : '') + (detail.checkpoints.items.length ? (detail.input ? ' · ' : '') + detail.checkpoints.items.length + (detail.checkpoints.items.length === 1 ? ' checkpoint' : ' checkpoints') : '')} bind:open={dataOpen} id="job-data">
          {#if detail.input}<JobInputPanel input={detail.input} jobRef={job.jobRef} status={job.status} canControl={canControlNow} onAmended={reload} />{/if}
          {#if detail.checkpoints.items.length}<JobCheckpointsPanel checkpoints={detail.checkpoints.items} total={detail.checkpoints.totalCount} />{/if}
        </FocusDisclosure>
      {/if}
      {#if hasSchedules}
        <FocusDisclosure title="Schedules" hint={String(detail.schedulesTotal ?? detail.schedules.length) + ' attached'} bind:open={schedulesOpen} hideHeading={true} id="job-schedules">
          <JobSchedulesPanel schedules={detail.schedules} total={detail.schedulesTotal} onChanged={reload} />
        </FocusDisclosure>
      {/if}
      <TagEditor path={'jobs/' + job.jobRef + '/tags'} permission={Permission.control} />
      <FocusDisclosure title="Technical details" hint="Identifiers, dates and policy">
        <JobSummary {job} maxAttempts={detail.maxAttemptsEffective ?? null} />
        <JobDiagnosis jobNamespace={job.jobNamespace} {explanation} />
        <div class="support-row"><CopyButton value={JSON.stringify(job, null, 2)} label="Copy raw snapshot" showLabel={true} />{#if incidentSummary}<CopyButton value={incidentSummary} label="Copy incident summary" showLabel={true} />{/if}</div>
      </FocusDisclosure>
    </DetailLayout>
  {:else}
    <div class="panel"><StateView loading={true} loadingText="Loading job..." /></div>
  {/if}
</Page>

<style>
  .support-row { display: flex; flex-wrap: wrap; gap: 10px; }
  .clone-action { display: inline-flex; align-items: center; gap: 6px; padding: 6px 12px; border: 1px solid var(--line); border-radius: var(--radius-control); color: var(--ink); justify-self: start; }
  .hero-attempt { border: 0; background: none; padding: 0; color: var(--muted); font: inherit; text-decoration: underline; text-decoration-style: dotted; text-underline-offset: 4px; }
  .hero-attempt:hover:not(:disabled) { color: var(--accent); }
  :global(.detail-main > .focus-disclosure-body) { min-width: 0; }
</style>
