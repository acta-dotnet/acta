<script lang="ts">
  import type { JobScheduleView } from '../../api.ts';
  import type { JobDetail } from './types.ts';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import { nextFiring } from './jobDetailState.ts';
  import { routes } from '../../routes.ts';
  import { now } from '../../time.ts';

  let { job, schedules, total }: { job: JobDetail; schedules: JobScheduleView[]; total: number | null } = $props();
  let firing = $derived(job.status === 'idle' && !job.nextRunAtUtc ? nextFiring(schedules, total, $now) : null);
</script>

{#snippet scheduleLink(schedule: JobScheduleView)}<a href={routes.schedule(schedule.jobNamespace, schedule.jobName, schedule.scheduleName)} class="mono">{schedule.scheduleName}</a>{/snippet}

{#if job.status !== 'idle' || job.nextRunAtUtc}
  <RelativeTime value={job.nextRunAtUtc} />
{:else if firing}
  {@const { fires, resumes } = firing}
  {#if fires}
    <RelativeTime value={fires.atUtc} /> <span>from {@render scheduleLink(fires.schedule)}{#if resumes?.overdue}. Schedule {@render scheduleLink(resumes.schedule)} is overdue and may fire first{:else if resumes}. Schedule {@render scheduleLink(resumes.schedule)} may fire first once it resumes{/if}</span>{#if resumes && !resumes.overdue}{' '}<RelativeTime value={resumes.atUtc} />{/if}
  {:else if resumes?.overdue}
    <span>Schedule {@render scheduleLink(resumes.schedule)} is overdue. Its misfire policy decides what fires next.</span>
  {:else if resumes}
    <span>Schedule {@render scheduleLink(resumes.schedule)} resumes</span> <RelativeTime value={resumes.atUtc} />
  {/if}
{:else if firing === null}
  <span>No schedule is set to fire it.</span>
{:else}
  <span>When one of its schedules fires.</span>
{/if}
