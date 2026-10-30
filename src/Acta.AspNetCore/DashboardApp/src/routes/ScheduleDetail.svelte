<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import type { Paged } from '../api.ts';
  import { api } from '../api.ts';
  import { capabilitiesQuery, canControl, keys } from '../query.ts';
  import Page from '../components/Page.svelte';
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

  interface ScheduleItem {
    jobNamespace: string;
    jobName: string;
    // The permanent slot job this schedule fires; null for a per-occurrence definition, which has no slot.
    jobRef: string | null;
    scheduleName: string;
    origin: string;
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
  let canControlNow = $derived(canControl(capabilities.data));

  // Schedule lifecycle events (schedule.*) are recorded against the slot job, whose timeline is
  // dominated by execution/rollover rows. Query each lifecycle code directly (server-side eventCode
  // filter) so a busy schedule's executions can't crowd them out, then merge newest-first. The slot
  // can carry several schedules, so a row whose details.schedule names another one is dropped.
  const HISTORY_CODES = ['schedule.paused', 'schedule.resumed', 'schedule.pause-expired', 'schedule.triggered'];
  const history = createQuery(() => ({
    queryKey: keys.detail('schedule-history', `${schedule?.jobRef ?? ''}/${scheduleName}`),
    queryFn: async ({ signal }: { signal: AbortSignal }) => {
      const pages = await Promise.all(
        HISTORY_CODES.map((eventCode) =>
          api<Paged<HistoryEvent>>('events', { jobRef: schedule!.jobRef, eventCode, pageSize: 20 }, { signal }).then(
            (page) => page.items.filter((ev) => (eventFacts(ev.detailsText).schedule ?? scheduleName) === scheduleName)
          )
        )
      );
      return mergeHistory(pages);
    },
    enabled: !!schedule?.jobRef
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
  {#snippet breadcrumb()}
    <a href={backHref}><Icon name="chevron-left" />Schedules</a>
  {/snippet}
  {#snippet actions()}
    <PageFreshness
      dataUpdatedAt={detail.dataUpdatedAt}
      isFetching={detail.isFetching}
      isError={!!detail.error}
      onRefresh={() => detail.refetch()} />
  {/snippet}

  {#if loading || error || !schedule}
    <div class="panel">
      <StateView {loading} {error} loadingText="Loading schedule..." emptyText="Schedule not found." onRetry={() => detail.refetch()} />
    </div>
  {:else}
    <section class="entity-summary" aria-label="Schedule identity">
      <div class="entity-meta mono">{schedule.jobNamespace} / {schedule.jobName} / {schedule.scheduleName} · version {schedule.version}</div>
      <ScheduleStatus status={schedule.status} pausedUntilUtc={schedule.pausedUntilUtc} />
    </section>

    <div class="detail-workspace">
      <div class="detail-main">
        <section class="detail-panel">
          <h2>Timing</h2>

          <!-- A name resolves afresh at every evaluation (a declared calendar first, then the catalog), so the
               page shows the name and where the catalog would hold it, not a stored resolution. -->
          {#snippet calendarBinding(name: string)}
            <span class="mono">{name}</span>
            <a class="dim" href={routes.calendars({ namespace: schedule?.jobNamespace, nameSearch: name })} title="A declared calendar of this name wins; otherwise the catalog's current version applies">in the catalog</a>
          {/snippet}
          <dl class="detail-readonly detail-readonly-grid">
            {#if schedule.expressionKind === 'calendar'}
              <div><dt>Calendar</dt><dd>{@render calendarBinding(schedule.expression)}</dd></div>
            {:else}
              <div><dt>Expression</dt><dd><span class="mono">{schedule.expression}</span> <CopyButton value={schedule.expression} label="Copy expression" /></dd></div>
            {/if}
            <div><dt>Time zone</dt><dd>{schedule.timeZoneId} <CopyButton value={schedule.timeZoneId} label="Copy time zone" /></dd></div>
            <div><dt>Source kind</dt><dd>{schedule.expressionKind}</dd></div>
            <div>
              <dt>Availability</dt>
              <dd>
                {#if schedule.availabilityCalendarName}
                  {@render calendarBinding(schedule.availabilityCalendarName)}
                {:else}
                  <span class="dim">any time</span>
                {/if}
              </dd>
            </div>
            <div><dt>Misfire strategy</dt><dd>{schedule.misfireStrategy}</dd></div>
            <div><dt>Origin</dt><dd>{schedule.origin}</dd></div>
            <div>
              <dt>Delivery</dt>
              <dd>
                {schedule.scheduledDelivery}
                {#if schedule.scheduledDelivery === 'per-occurrence'}
                  <span class="dim">· {schedule.outstandingCount} of {schedule.maxOutstanding} outstanding, {schedule.fullBehavior} when full</span>
                {/if}
              </dd>
            </div>
            <div>
              <dt>Expiry</dt>
              <dd>
                {#if schedule.expireAfterSeconds !== null}
                  {displayFormatter.duration(schedule.expireAfterSeconds)} unadmitted, then {schedule.expireAction}
                {:else}
                  <span class="dim">never</span>
                {/if}
              </dd>
            </div>
            <div><dt>Next occurrence</dt><dd><RelativeTime value={schedule.nextOccurrenceAtUtc} /></dd></div>
            <div><dt>Next evaluation</dt><dd><RelativeTime value={schedule.nextEvaluationAtUtc} /> · {schedule.evaluationStatus}</dd></div>
          </dl>
          <p class="detail-help">
            Misfire strategy governs occurrences missed while no host admitted them: skip to the next one, catch up once,
            catch up the latest, or catch up all. An availability calendar omits occurrences outside its windows, never
            shifts them. Per-occurrence delivery starts a fresh root per occurrence up to its capacity; permanent delivery
            fires one slot job.
          </p>

          <ScheduleControls
            mode="editor"
            jobNamespace={schedule.jobNamespace}
            jobName={schedule.jobName}
            scheduleName={schedule.scheduleName}
            status={schedule.status}
            onChanged={() => { void detail.refetch(); void history.refetch(); }} />
        </section>

        <ChangeHistory history={history.data ?? []} loading={history.isPending} emptyText="No recorded schedule changes." />
      </div>

      <aside class="detail-rail">
        <section class="detail-panel">
          <h2>Lifecycle</h2>
          <p>{schedule.status === 'paused' ? 'Paused; recurring fires are held.' : schedule.status === 'orphaned' ? 'Orphaned; its origin declaration is gone.' : 'Active and eligible to fire.'}</p>
          {#if canControlNow}
            <p class="detail-help">Trigger now fires once without moving the recurring cursor. Pausing holds future fires until the schedule is resumed.</p>
          {/if}
          <ScheduleControls
            mode="actions"
            jobNamespace={schedule.jobNamespace}
            jobName={schedule.jobName}
            scheduleName={schedule.scheduleName}
            status={schedule.status}
            onChanged={() => { void detail.refetch(); void history.refetch(); }} />
        </section>

        <section class="detail-panel go-to" aria-label="Go to">
          <p class="detail-kicker">Go to</p>
          <nav>
            <a href={jobsHref}>Jobs</a>
            <a href={definitionHref}>Definition</a>
          </nav>
        </section>
      </aside>
    </div>
  {/if}
</Page>
