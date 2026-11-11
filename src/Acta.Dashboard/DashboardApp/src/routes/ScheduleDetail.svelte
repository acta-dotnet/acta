<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import type { Paged } from '../api.ts';
  import { api } from '../api.ts';
  import { capabilitiesQuery, can, Permission, keys } from '../query.ts';
  import Page from '../components/Page.svelte';
  import DetailActions from '../components/DetailActions.svelte';
  import DetailLayout from '../components/DetailLayout.svelte';
  import DetailFacts from '../components/DetailFacts.svelte';
  import FocusDisclosure from '../components/FocusDisclosure.svelte';
  import Icon from '../components/Icon.svelte';
  import StateView from '../components/StateView.svelte';
  import ScheduleStatus from '../components/ScheduleStatus.svelte';
  import ScheduleControls from '../components/ScheduleControls.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import ChangeHistory from '../components/ChangeHistory.svelte';
  import CopyButton from '../components/CopyButton.svelte';
  import PageFreshness from '../components/PageFreshness.svelte';
  import { mergeHistory, type HistoryEvent } from '../components/changeHistory.ts';
  import { eventFacts } from '../lib/eventFacts.ts';
  import { scope } from '../scope.ts';
  import { routes } from '../routes.ts';
  import { displayFormatter } from '../format.ts';
  import { SCHEDULE_LAG_ENVELOPE_SECONDS } from '../components/scheduleControlState.ts';

  interface ScheduleItem {
    jobNamespace: string;
    jobName: string;
    // The permanent slot job this schedule fires; null for a per-occurrence definition, which has no slot.
    jobRef: string | null;
    scheduleName: string;
    expressionKind: string;
    expression: string;
    timeZoneId: string;
    misfireStrategy: string;
    nextOccurrenceAtUtc: string | null;
    nextEvaluationAtUtc: string | null;
    evaluationStatus: string;
    status: string;
    pausedUntilUtc: string | null;
    createdAtUtc: string;
    modifiedAtUtc: string;
    version: number;
    maxOutstanding: number;
    outstandingCount: number;
    fullBehavior: string;
    scheduledDelivery: string;
    expireAfterSeconds: number | null;
    expireAction: string | null;
    availabilityCalendarName: string | null;
    startAtUtc: string | null;
    endBeforeUtc: string | null;
  }

  let {
    jobNamespace,
    jobName,
    scheduleName
  }: { jobNamespace: string; jobName: string; scheduleName: string } = $props();

  async function loadSchedule(signal: AbortSignal): Promise<ScheduleItem | null> {
    let cursor: string | undefined;
    for (let guard = 0; guard < 100; guard++) {
      const page = await api<Paged<ScheduleItem>>(
        'schedules',
        { jobNamespace, jobName, liveOnly: false, pageSize: 100, cursor },
        { signal }
      );
      const match = page.items.find((item) => item.scheduleName === scheduleName);
      if (match) return match;
      if (!page.hasMore || !page.nextCursor) break;
      cursor = page.nextCursor;
    }
    return null;
  }

  const detail = createQuery(() => ({
    queryKey: keys.detail('schedule-detail', `${jobNamespace}/${jobName}/${scheduleName}`),
    queryFn: ({ signal }) => loadSchedule(signal),
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false
  }));

  let schedule = $state<ScheduleItem | null>(null);
  $effect(() => {
    if (detail.data) schedule = detail.data;
  });

  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(can(capabilities.data, Permission.control));
  // The status panel calls an evaluation overdue past the Overview's envelope, so a normal fire delay never reads as a problem.
  // It is measured at the read the page shows, which does not poll: against the clock, a page left open would turn every
  // healthy schedule overdue.
  let overdue = $derived(
    schedule?.status === 'active' && !!schedule.nextEvaluationAtUtc &&
      detail.dataUpdatedAt - new Date(schedule.nextEvaluationAtUtc).getTime() > SCHEDULE_LAG_ENVELOPE_SECONDS * 1000
  );

  // Schedule lifecycle events carry the definition's id whichever row they are recorded against: the
  // slot job of a permanent definition, or the definition itself for a per-occurrence one, which has no
  // slot. Read each code through the definition's events (server-side eventCode filter) so executions
  // can't crowd them out, then merge newest-first. A definition can carry several schedules, so a row
  // whose details.schedule names another one is dropped, and a code reads on until twenty are this one's.
  const HISTORY_CODES = [
    'schedule.paused',
    'schedule.resumed',
    'schedule.pause-expired',
    'schedule.triggered',
    'schedule.skipped',
    'schedule.retimed',
    'schedule.evaluation-failed',
    'schedule.orphaned'
  ];
  let definitionEvents = $derived(`definitions/${encodeURIComponent(jobNamespace)}/${encodeURIComponent(jobName)}/events`);
  async function scheduleEvents(eventCode: string, signal: AbortSignal): Promise<HistoryEvent[]> {
    const found: HistoryEvent[] = [];
    let cursor: string | undefined;
    for (let guard = 0; guard < 10 && found.length < 20; guard++) {
      const page = await api<Paged<HistoryEvent>>(definitionEvents, { eventCode, pageSize: 100, cursor }, { signal });
      found.push(...page.items.filter((ev) => (eventFacts(ev.detailsText).schedule ?? scheduleName) === scheduleName));
      if (!page.hasMore || !page.nextCursor) break;
      cursor = page.nextCursor;
    }
    return found;
  }
  const history = createQuery(() => ({
    queryKey: keys.detail('schedule-history', `${jobNamespace}/${jobName}/${scheduleName}`),
    queryFn: async ({ signal }: { signal: AbortSignal }) => {
      const pages = await Promise.all(HISTORY_CODES.map((eventCode) => scheduleEvents(eventCode, signal)));
      return mergeHistory(pages);
    }
  }));

  let loading = $derived(detail.isPending);
  let error = $derived(detail.error instanceof Error ? detail.error.message : detail.error ? String(detail.error) : '');
  let backHref = $derived(routes.schedules({ namespace: $scope }));
  let jobsHref = $derived(
    schedule
      ? routes.jobs({ jobName: schedule.jobName, namespace: schedule.jobNamespace })
      : routes.jobs()
  );
  let definitionHref = $derived(
    schedule ? routes.definition(schedule.jobNamespace, schedule.jobName, { namespace: schedule.jobNamespace }) : routes.definitions()
  );
</script>

<Page title={schedule?.scheduleName ?? 'Schedule'}>
  {#snippet breadcrumb()}<a href={backHref}><Icon name="chevron-left" />Schedules</a>{/snippet}
  {#snippet actions()}<PageFreshness dataUpdatedAt={detail.dataUpdatedAt} isFetching={detail.isFetching} isError={!!detail.error} onRefresh={() => detail.refetch()} />
    {#if schedule && canControlNow}{@const currentSchedule = schedule}
      <DetailActions>
        <section class="detail-panel"><h2>Schedule actions</h2><p class="detail-help">Trigger now fires once without moving the recurring cursor. Pausing holds future fires until the schedule resumes.</p>
          <ScheduleControls mode="secondary" jobNamespace={currentSchedule.jobNamespace} jobName={currentSchedule.jobName} scheduleName={currentSchedule.scheduleName} status={currentSchedule.status} onChanged={() => { void detail.refetch(); void history.refetch(); }} />
        </section>

      </DetailActions>
    {/if}
  {/snippet}
  {#if loading || error || !schedule}
    <div class="panel"><StateView {loading} {error} loadingText="Loading schedule..." emptyText="Schedule not found." onRetry={() => detail.refetch()} /></div>
  {:else}
    <section class="entity-summary" aria-label="Schedule identity"><ScheduleStatus status={schedule.status} pausedUntilUtc={schedule.pausedUntilUtc} /><a href={definitionHref}>{schedule.jobNamespace} / {schedule.jobName}</a></section>
    {@const currentSchedule = schedule}
    <section class="focus-state" style={'--tone:var(--' + (currentSchedule.status === 'active' && !overdue ? 'ok' : 'warn') + ')'} aria-label="Schedule status">
      <div class="focus-state-content">
        <h2><Icon name="calendar" />{currentSchedule.status === 'paused' ? 'Schedule paused' : currentSchedule.status === 'orphaned' ? 'Declaration removed' : overdue ? 'Overdue for evaluation' : 'Active and eligible to fire'}</h2>
        {#if overdue}<p>No live worker in this namespace has evaluated it since it fell due. When one does, its misfire strategy decides what fires.</p>{/if}
        <div class="focus-facts"><span>Next occurrence <strong><RelativeTime value={currentSchedule.nextOccurrenceAtUtc} /></strong></span>{#if currentSchedule.pausedUntilUtc}<span>Resumes <RelativeTime value={currentSchedule.pausedUntilUtc} /></span>{/if}</div>
      </div>
      <ScheduleControls mode="primary" jobNamespace={currentSchedule.jobNamespace} jobName={currentSchedule.jobName} scheduleName={currentSchedule.scheduleName} status={currentSchedule.status} onChanged={() => { void detail.refetch(); void history.refetch(); }} />
    </section>
    {#snippet calendarBinding(name: string)}<span class="mono">{name}</span> <a href={routes.calendars({ namespace: currentSchedule.jobNamespace, nameContains: name })} title="The catalog's current version of this name applies">in the catalog</a>{/snippet}
    <DetailLayout>

      {#snippet related()}<a href={jobsHref}>Jobs</a><a href={definitionHref}>Definition</a>{/snippet}
      <section class="detail-panel" aria-label="Timing">
        <h2>Timing</h2>
        <dl class="detail-readonly detail-readonly-grid">
          {#if currentSchedule.expressionKind === 'calendar'}<div><dt>Calendar</dt><dd>{@render calendarBinding(currentSchedule.expression)}</dd></div>
          {:else}<div><dt>Expression</dt><dd><span class="mono">{currentSchedule.expression}</span> <CopyButton value={currentSchedule.expression} label="Copy expression" /></dd></div>{/if}
          <div><dt>Time zone</dt><dd>{currentSchedule.timeZoneId} <CopyButton value={currentSchedule.timeZoneId} label="Copy time zone" /></dd></div>
          {#if currentSchedule.availabilityCalendarName}<div><dt>Availability</dt><dd>{@render calendarBinding(currentSchedule.availabilityCalendarName)}</dd></div>{/if}
          <div><dt>Next evaluation</dt><dd>{#if currentSchedule.nextEvaluationAtUtc}<RelativeTime value={currentSchedule.nextEvaluationAtUtc} />{' · '}{/if}{currentSchedule.evaluationStatus}</dd></div>
        </dl>
        <ScheduleControls mode="editor" jobNamespace={currentSchedule.jobNamespace} jobName={currentSchedule.jobName} scheduleName={currentSchedule.scheduleName} status={currentSchedule.status} onChanged={() => { void detail.refetch(); void history.refetch(); }} />
      </section>
      <FocusDisclosure title="Delivery policy" hint="Misfires, capacity and bounds">
        <DetailFacts facts={[
          { label: 'Misfire strategy', value: currentSchedule.misfireStrategy },
          { label: 'Delivery', value: currentSchedule.scheduledDelivery },
          { label: 'Outstanding', value: currentSchedule.scheduledDelivery === 'per-occurrence' ? currentSchedule.outstandingCount + ' of ' + currentSchedule.maxOutstanding : null },
          { label: 'When full', value: currentSchedule.scheduledDelivery === 'per-occurrence' ? currentSchedule.fullBehavior : null },
          { label: 'Expiry', value: currentSchedule.expireAfterSeconds !== null ? displayFormatter.duration(currentSchedule.expireAfterSeconds) + ' unadmitted, then ' + currentSchedule.expireAction : 'Never' },
          { label: 'Starts', value: currentSchedule.startAtUtc, kind: 'time' },
          { label: 'Ends before', value: currentSchedule.endBeforeUtc, kind: 'time' },
        ]} />
        <p class="detail-help">Misfire strategy governs occurrences missed while no host admitted them: skip to the next one, catch up once, catch up the latest, or catch up all. An availability calendar omits occurrences outside its windows and never shifts them. Per-occurrence delivery starts a fresh root per occurrence up to its capacity; permanent delivery fires one permanent job.</p>
      </FocusDisclosure>
      <FocusDisclosure title="Technical details" hint="Source, version and dates">
        <DetailFacts facts={[
          { label: 'Source kind', value: currentSchedule.expressionKind },
          { label: 'Version', value: currentSchedule.version },
          { label: 'Created', value: currentSchedule.createdAtUtc, kind: 'time' },
          { label: 'Modified', value: currentSchedule.modifiedAtUtc, kind: 'time' },
        ]} />
      </FocusDisclosure>
      <ChangeHistory history={history.data ?? []} loading={history.isPending} emptyText="No recorded schedule changes." />
    </DetailLayout>
  {/if}
</Page>
