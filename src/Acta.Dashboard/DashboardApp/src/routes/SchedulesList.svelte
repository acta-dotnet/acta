<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { capabilitiesQuery } from '../query.ts';
  import Icon from '../components/Icon.svelte';
  import { hashParams, updateHashParams } from '../router';
  import { scope, setScope } from '../scope';
  import Page from '../components/Page.svelte';
  import ActaGrid from '../components/grid/ActaGrid.svelte';
  import ScheduleStatus from '../components/ScheduleStatus.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import FilterBar from '../components/FilterBar.svelte';
  import ActiveFilters from '../components/ActiveFilters.svelte';
  import { now } from '../time';
  import { createUrlFilters } from '../urlFilters.ts';
  import { schedulesListSql, COPY_SQL_TITLE } from '../lib/copyAsSql.ts';
  import CopyButton from '../components/CopyButton.svelte';
  import { routes } from '../routes.ts';
  import type { ColumnDef } from '../components/grid/types.ts';

  interface ScheduleRow {
    jobNamespace: string;
    jobName: string;
    scheduleName: string;
    status: string;
    expressionKind: string;
    expression: string;
    availabilityCalendarName: string | null;
    timeZoneId: string;
    misfireStrategy: string;
    nextOccurrenceAtUtc: string | null;
    nextEvaluationAtUtc: string | null;
    evaluationStatus: string;
    pausedUntilUtc: string | null;
  }

  const initial = hashParams();
  const filters = createUrlFilters({ jobName: 'jobName', live: 'live' }, { jobName: '', live: '1' });

  // liveOnly is a view toggle rather than a removable filter, so it stays out of the chip bar.
  let activeChips = $derived(
    [
      $scope ? { label: 'Namespace', value: $scope, onRemove: () => setScope('') } : null,
      $scope && $filters.jobName.trim() ? { label: 'Job name', value: $filters.jobName.trim(), onRemove: () => filters.patch({ jobName: '' }) } : null
    ].filter((chip): chip is { label: string; value: string; onRemove: () => void } => chip !== null)
  );
  function clearAllFilters() {
    filters.clear();
    setScope('');
  }

  const capabilities = createQuery(() => capabilitiesQuery());

  // The applied filters as ad-hoc SQL against the schedules operator view (mirrors the grid filters below).
  let copySql = $derived(
    schedulesListSql(
      { namespace: $scope, jobName: $scope ? $filters.jobName.trim() : '', liveOnly: $filters.live !== '0' },
      { provider: capabilities.data?.provider, schema: capabilities.data?.schema }
    )
  );

  const columns: ColumnDef<ScheduleRow>[] = [
    { key: 'scheduleName', header: 'Schedule' },
    { key: 'status', header: 'Status' },
    { key: 'job', header: 'Job' },
    { key: 'expression', header: 'Source' },
    { key: 'timeZoneId', header: 'Time zone', class: 'mobile-hide' },
    { key: 'misfireStrategy', header: 'Misfire', class: 'mobile-hide' },
    { key: 'nextOccurrenceAtUtc', header: 'Next occurrence' },
    { key: 'actions', header: '', class: 'col-open' }
  ];

  function isDueSoon(schedule: ScheduleRow, nowMs: number): boolean {
    return !!schedule.nextOccurrenceAtUtc && new Date(schedule.nextOccurrenceAtUtc).getTime() - nowMs < 900000;
  }

  // A schedule whose next evaluation is already past is one the admitter should have reached - flag it
  // so a namespace with no live worker is obvious at a glance. The engine leaves the instant empty while
  // a schedule waits for its firing or its limit, or is paused for good.
  function isOverdue(schedule: ScheduleRow, nowMs: number): boolean {
    return !!schedule.nextEvaluationAtUtc && new Date(schedule.nextEvaluationAtUtc).getTime() < nowMs;
  }

  // Every evaluation status but the normal ones is worth a badge; the two the engine cannot get past warn.
  const QUIET_EVALUATIONS = ['scheduled', 'resumed', 'unspecified'];
  const WARN_EVALUATIONS = ['evaluation-error', 'slot-unavailable'];

  function jobsHref(schedule: ScheduleRow): string {
    return routes.jobs({ jobName: schedule.jobName, namespace: schedule.jobNamespace });
  }

  function detailHref(schedule: ScheduleRow): string {
    return routes.schedule(schedule.jobNamespace, schedule.jobName, schedule.scheduleName);
  }
</script>

<Page title="Schedules">

  <div class="panel fill">
    <FilterBar>
      <label>
        Job name
        <input
          placeholder={$scope ? 'job name' : 'needs a namespace scope'}
          disabled={!$scope}
          value={$filters.jobName}
          onchange={(event) => filters.patch({ jobName: event.currentTarget.value.trim() })} />
      </label>
      <label>
        <input type="checkbox" checked={$filters.live !== '0'} onchange={(event) => filters.patch({ live: event.currentTarget.checked ? '1' : '0' })} />
        Live only
      </label>
      <span class="dim schedule-help">
        Schedules aren't created here - they're declared in code with a <span class="mono">[Schedule]</span> attribute on a
        <span class="mono">[Job]</span> handler and synced at host startup.
      </span>
      <CopyButton value={copySql} label="Copy SQL" title={COPY_SQL_TITLE} />
    </FilterBar>

    <ActiveFilters chips={activeChips} onClearAll={clearAllFilters} />

    {#snippet scheduleCell(schedule: ScheduleRow)}<a href={detailHref(schedule)}>{schedule.scheduleName}</a>{/snippet}
    {#snippet statusCell(schedule: ScheduleRow)}<ScheduleStatus status={schedule.status} pausedUntilUtc={schedule.pausedUntilUtc} />{/snippet}
    {#snippet jobCell(schedule: ScheduleRow)}
      <a href={jobsHref(schedule)}>{schedule.jobNamespace} / {schedule.jobName}</a>
    {/snippet}
    {#snippet sourceCell(schedule: ScheduleRow)}
      {#if schedule.expressionKind === 'calendar'}<span class="dim">calendar</span>{' '}{/if}<span class="mono">{schedule.expression}</span>
      {#if schedule.availabilityCalendarName}<span class="dim source-availability">in {schedule.availabilityCalendarName}</span>{/if}
    {/snippet}
    {#snippet nextOccurrenceCell(schedule: ScheduleRow)}
      {#if schedule.status === 'paused'}
        <RelativeTime value={schedule.pausedUntilUtc} />
      {:else}
        <RelativeTime value={schedule.nextOccurrenceAtUtc} />
        {#if isOverdue(schedule, $now)}<span class="badge warn">overdue</span>{/if}
        {#if !QUIET_EVALUATIONS.includes(schedule.evaluationStatus)}
          <span class="badge" class:warn={WARN_EVALUATIONS.includes(schedule.evaluationStatus)}>{schedule.evaluationStatus.replaceAll('-', ' ')}</span>
        {/if}
      {/if}
    {/snippet}
    {#snippet actionsCell(schedule: ScheduleRow)}
      <a
        class="icon-action"
        href={detailHref(schedule)}
        title={'Open schedule ' + schedule.scheduleName}
        aria-label={'Open schedule ' + schedule.scheduleName}><Icon name="chevron-right" /></a>
    {/snippet}

    <ActaGrid
      rowKey={(schedule: ScheduleRow) => `${schedule.jobNamespace}/${schedule.jobName}/${schedule.scheduleName}`}
      endpoint="schedules"
      mobileCards={true}
      {columns}
      filters={() => ({ jobName: $scope ? $filters.jobName.trim() : '', liveOnly: $filters.live !== '0', jobNamespace: $scope })}
      includeTotal={true}
      initialPageSize={Number(initial.get('pageSize') ?? '50') || 50}
      onPageSizeChange={(size) => updateHashParams({ pageSize: String(size) })}
      loadingText="Loading schedules..."
      emptyText="No schedules yet. Declare one with a [Schedule] attribute on a [Job] handler; it appears here after the host syncs at startup."
      cells={{ scheduleName: scheduleCell, status: statusCell, job: jobCell, expression: sourceCell, nextOccurrenceAtUtc: nextOccurrenceCell, actions: actionsCell }}
      rowClass={(schedule: ScheduleRow) =>
        schedule.status === 'paused'
          ? 'held'
          : isOverdue(schedule, $now)
            ? 'overdue'
            : isDueSoon(schedule, $now)
              ? 'due'
              : ''} />
  </div>
</Page>

<style>
  .schedule-help { flex-basis: 100%; }
  .source-availability { display: block; font-size: var(--text-xs); }
</style>
