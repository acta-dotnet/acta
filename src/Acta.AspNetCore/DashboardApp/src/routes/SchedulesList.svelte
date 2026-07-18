<script lang="ts">
  import { get } from 'svelte/store';
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
  import { parseTagTokens } from '../lib/tagTokens.ts';
  import { routes } from '../routes.ts';
  import type { ColumnDef } from '../components/grid/types.ts';

  interface ScheduleRow {
    jobNamespace: string;
    jobName: string;
    scheduleName: string;
    status: string;
    expression: string;
    timeZone: string;
    misfireStrategy: string;
    nextRunAtUtc: string | null;
    pausedUntilUtc: string | null;
  }

  const initial = hashParams();
  const filters = createUrlFilters({ jobName: 'jobName', live: 'live', tags: 'tags' }, { jobName: '', live: '1', tags: '' });

  // liveOnly is a view toggle rather than a removable filter, so it stays out of the chip bar.
  let activeChips = $derived(
    [
      $scope ? { label: 'Namespace', value: $scope, onRemove: () => setScope('') } : null,
      $scope && $filters.jobName.trim() ? { label: 'Job name', value: $filters.jobName.trim(), onRemove: () => filters.patch({ jobName: '' }) } : null,
      $filters.tags.trim() ? { label: 'Tags', value: $filters.tags.trim(), onRemove: () => filters.patch({ tags: '' }) } : null
    ].filter((chip): chip is { label: string; value: string; onRemove: () => void } => chip !== null)
  );
  function clearAllFilters() {
    filters.clear();
    setScope('');
  }

  const columns: ColumnDef<ScheduleRow>[] = [
    { key: 'scheduleName', header: 'Schedule' },
    { key: 'status', header: 'Status' },
    { key: 'job', header: 'Job' },
    { key: 'expression', header: 'Expression', class: 'mono' },
    { key: 'timeZone', header: 'Time zone' },
    { key: 'misfireStrategy', header: 'Misfire' },
    { key: 'nextRunAtUtc', header: 'Next run' },
    { key: 'actions', header: '', class: 'col-open' }
  ];

  function isDueSoon(schedule: ScheduleRow, nowMs: number): boolean {
    return !!schedule.nextRunAtUtc && new Date(schedule.nextRunAtUtc).getTime() - nowMs < 900000;
  }

  // An active schedule whose next-run time is already in the past should have fired - flag it so a
  // stuck controller (or a namespace with no live worker) is obvious at a glance.
  function isOverdue(schedule: ScheduleRow, nowMs: number): boolean {
    return schedule.status !== 'paused' && !!schedule.nextRunAtUtc && new Date(schedule.nextRunAtUtc).getTime() < nowMs;
  }

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
        Tags
        <input placeholder="env:prod team" value={$filters.tags} onchange={(event) => filters.patch({ tags: event.currentTarget.value.trim() })} />
      </label>
      <label>
        <input type="checkbox" checked={$filters.live !== '0'} onchange={(event) => filters.patch({ live: event.currentTarget.checked ? '1' : '0' })} />
        Live only
      </label>
      <span class="dim schedule-help">
        Schedules aren't created here — they're declared in code with a <span class="mono">[JobSchedule]</span> attribute on a
        <span class="mono">[Job]</span> handler and synced at host startup.
      </span>
    </FilterBar>

    <ActiveFilters chips={activeChips} onClearAll={clearAllFilters} />

    {#snippet scheduleCell(schedule: ScheduleRow)}<a href={detailHref(schedule)}>{schedule.scheduleName}</a>{/snippet}
    {#snippet statusCell(schedule: ScheduleRow)}<ScheduleStatus status={schedule.status} pausedUntilUtc={schedule.pausedUntilUtc} />{/snippet}
    {#snippet jobCell(schedule: ScheduleRow)}
      <a href={jobsHref(schedule)} onclick={(event) => event.stopPropagation()}>{schedule.jobNamespace} / {schedule.jobName}</a>
    {/snippet}
    {#snippet nextRunCell(schedule: ScheduleRow)}
      {#if schedule.status === 'paused'}
        <RelativeTime value={schedule.pausedUntilUtc} />
      {:else}
        <RelativeTime value={schedule.nextRunAtUtc} />
        {#if isOverdue(schedule, $now)}<span class="badge warn">overdue</span>{/if}
      {/if}
    {/snippet}
    {#snippet actionsCell(schedule: ScheduleRow)}
      <a
        class="icon-action"
        href={detailHref(schedule)}
        title={'Open schedule ' + schedule.scheduleName}
        aria-label={'Open schedule ' + schedule.scheduleName}
        onclick={(event) => event.stopPropagation()}><Icon name="chevron-right" /></a>
    {/snippet}

    <ActaGrid
      rowKey={(schedule: ScheduleRow) => `${schedule.jobNamespace}/${schedule.jobName}/${schedule.scheduleName}`}
      endpoint="schedules"
      {columns}
      filters={() => ({ jobName: $scope ? $filters.jobName.trim() : '', liveOnly: $filters.live !== '0', jobNamespace: $scope, tag: parseTagTokens($filters.tags) })}
      includeTotal={true}
      initialPageSize={Number(initial.get('pageSize') ?? '50') || 50}
      onPageSizeChange={(size) => updateHashParams({ pageSize: String(size) })}
      loadingText="Loading schedules..."
      emptyText="No schedules yet. Declare one with a [JobSchedule] attribute on a [Job] handler; it appears here after the host syncs at startup."
      cells={{ scheduleName: scheduleCell, status: statusCell, job: jobCell, nextRunAtUtc: nextRunCell, actions: actionsCell }}
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
</style>
