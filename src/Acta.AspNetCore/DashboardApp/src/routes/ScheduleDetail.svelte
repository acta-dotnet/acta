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
  import TagEditor from '../components/TagEditor.svelte';
  import CopyButton from '../components/CopyButton.svelte';
  import PageFreshness from '../components/PageFreshness.svelte';
  import { mergeHistory, type HistoryEvent } from '../components/changeHistory.ts';
  import { scope } from '../scope.ts';
  import { routes } from '../routes.ts';

  interface ScheduleItem {
    jobNamespace: string;
    jobName: string;
    // The recurring slot job this schedule fires; null only while the slot has not been created yet.
    jobRef: string | null;
    scheduleName: string;
    origin: string;
    expressionKind: string;
    expression: string;
    timeZoneId: string;
    misfireStrategy: string;
    nextRunAtUtc: string | null;
    orphanedAtUtc: string | null;
    status: string;
    pausedUntilUtc: string | null;
    createdAtUtc: string;
    modifiedAtUtc: string;
    version: number;
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
  // filter) so a busy schedule's executions can't crowd them out, then merge newest-first.
  const HISTORY_CODES = ['schedule.paused', 'schedule.resumed', 'schedule.pause-expired', 'schedule.overrides-updated', 'schedule.triggered'];
  const history = createQuery(() => ({
    queryKey: keys.detail('schedule-history', schedule?.jobRef ?? ''),
    queryFn: async ({ signal }: { signal: AbortSignal }) => {
      const pages = await Promise.all(
        HISTORY_CODES.map((eventCode) =>
          api<Paged<HistoryEvent>>('events', { jobRef: schedule!.jobRef, eventCode, pageSize: 20 }, { signal }).then(
            (page) => page.items
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
          <h2>Timing &amp; overrides</h2>

          <dl class="detail-readonly detail-readonly-grid">
            <div><dt>Expression</dt><dd><span class="mono">{schedule.expression}</span> <CopyButton value={schedule.expression} label="Copy expression" /></dd></div>
            <div><dt>Time zone</dt><dd>{schedule.timeZoneId} <CopyButton value={schedule.timeZoneId} label="Copy time zone" /></dd></div>
            <div><dt>Expression kind</dt><dd>{schedule.expressionKind}</dd></div>
            <div><dt>Misfire strategy</dt><dd>{schedule.misfireStrategy}</dd></div>
            <div><dt>Origin</dt><dd>{schedule.origin}</dd></div>
            <div><dt>Next run</dt><dd><RelativeTime value={schedule.nextRunAtUtc} /></dd></div>
          </dl>
          <p class="detail-help">
            Misfire strategy governs fires missed while the host was down: it decides, on recovery, whether to
            catch up, fire once, or skip to the next scheduled time.
          </p>

          <ScheduleControls
            mode="editor"
            jobNamespace={schedule.jobNamespace}
            jobName={schedule.jobName}
            scheduleName={schedule.scheduleName}
            status={schedule.status}
            version={schedule.version}
            expression={schedule.expression}
            timeZoneId={schedule.timeZoneId}
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
            version={schedule.version}
            expression={schedule.expression}
            timeZoneId={schedule.timeZoneId}
            onChanged={() => { void detail.refetch(); void history.refetch(); }} />
        </section>

        <section class="detail-panel go-to" aria-label="Go to">
          <p class="detail-kicker">Go to</p>
          <nav>
            <a href={jobsHref}>Jobs</a>
            <a href={definitionHref}>Definition</a>
          </nav>
        </section>

        <TagEditor path={`schedules/${encodeURIComponent(schedule.jobNamespace)}/${encodeURIComponent(schedule.jobName)}/${encodeURIComponent(schedule.scheduleName)}/tags`} />
      </aside>
    </div>
  {/if}
</Page>
