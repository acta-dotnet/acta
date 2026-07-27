<script lang="ts">
  import { type JobScheduleView } from '../../api.ts';
  import ScheduleStatus from '../../components/ScheduleStatus.svelte';
  import ScheduleControls from '../../components/ScheduleControls.svelte';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import { routes } from '../../routes.ts';
  import { displayFormatter } from '../../format.ts';

  // The schedules attached to this job's recurring slot, fed from the aggregate detail read (the
  // /schedules list filters by jobNamespace + jobName, and the detail read carries that set with
  // liveOnly: false so an orphaned schedule still shows). Rendered only when the job actually has
  // schedules: most jobs are one-shot, so an empty panel would be pure noise. A control action asks
  // JobDetail to refetch the detail query (onChanged).
  // `total` is the filter-wide count: the detail read caps this list, so a total above the array
  // length means this is a preview.
  let {
    schedules = [],
    total = null,
    onChanged = () => {}
  }: { schedules?: JobScheduleView[]; total?: number | null; onChanged?: () => void } = $props();

  let truncated = $derived(total !== null && total > schedules.length);
</script>

{#if schedules.length > 0}
  <section class="detail-panel" aria-label="Job schedules">
    <h2>Schedules</h2>
    <p class="detail-help">
      Recurring schedules bound to this job's slot. Pause, resume, trigger, or override each one; open a
      schedule for its full timing detail and change history.
    </p>
    <div class="schedule-list">
      {#each schedules as schedule (schedule.jobScheduleId)}
        <div class="schedule-item">
          <div class="schedule-head">
            <a class="mono" href={routes.schedule(schedule.jobNamespace, schedule.jobName, schedule.scheduleName)}>{schedule.scheduleName}</a>
            <ScheduleStatus status={schedule.status} pausedUntilUtc={schedule.pausedUntilUtc} />
          </div>
          <dl class="schedule-meta">
            <div><dt>Expression</dt><dd class="mono">{schedule.expression}</dd></div>
            <div><dt>Kind</dt><dd>{schedule.expressionKind}</dd></div>
            <div><dt>Next run</dt><dd><RelativeTime value={schedule.nextRunAtUtc} /></dd></div>
          </dl>
          <ScheduleControls
            jobNamespace={schedule.jobNamespace}
            jobName={schedule.jobName}
            scheduleName={schedule.scheduleName}
            status={schedule.status}
            version={schedule.version}
            expression={schedule.expression}
            timeZone={schedule.timeZone}
            onChanged={onChanged} />
        </div>
      {/each}
    </div>
    {#if truncated}
      <p class="detail-help">
        Showing {displayFormatter.number(schedules.length)} of {displayFormatter.number(total ?? 0)}.
        <a href={routes.schedules({ namespace: schedules[0].jobNamespace })}>Open the schedules list</a> for the rest.
      </p>
    {/if}
  </section>
{/if}

<style>
  .schedule-list { display: flex; flex-direction: column; gap: 14px; }
  .schedule-item {
    padding: 14px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
  }
  .schedule-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    flex-wrap: wrap;
    gap: 8px;
    margin-bottom: 8px;
  }
  .schedule-meta { display: flex; flex-wrap: wrap; gap: 6px 24px; margin: 0 0 12px; }
  .schedule-meta dt { color: var(--muted); font-size: var(--text-xs); font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; }
  .schedule-meta dd { margin: 2px 0 0; }
</style>
