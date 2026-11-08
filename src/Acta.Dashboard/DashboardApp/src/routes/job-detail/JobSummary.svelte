<script lang="ts">
  import type { EventItem, JobScheduleView } from '../../api.ts';
  import { displayFormatter, failureCountText, TERMINAL_STATUSES } from '../../format.ts';
  import { eventFacts } from '../../lib/eventFacts.ts';
  import CopyButton from '../../components/CopyButton.svelte';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import StatusBadge from '../../components/StatusBadge.svelte';
  import { nextFiring, payloadFormatLabel } from './jobDetailState.ts';
  import type { JobDetail } from './types.ts';
  import { routes } from '../../routes.ts';
  import JobRef from '../../components/JobRef.svelte';

  let {
    job,
    lastEvent = null,
    maxAttempts = null,
    schedules = [],
    schedulesTotal = null
  }: {
    job: JobDetail;
    lastEvent?: EventItem | null;
    maxAttempts?: number | null;
    schedules?: JobScheduleView[];
    schedulesTotal?: number | null;
  } = $props();

  let tenant = $derived(job.tenantName ?? null);
  // An idle permanent job has no run time of its own: its schedules decide when it next fires.
  let firing = $derived(job.status === 'idle' && !job.nextRunAtUtc ? nextFiring(schedules, schedulesTotal) : null);
</script>

{#snippet scheduleLink(schedule: JobScheduleView)}<a href={routes.schedule(schedule.jobNamespace, schedule.jobName, schedule.scheduleName)} class="mono">{schedule.scheduleName}</a>{/snippet}

<section class="panel" aria-labelledby="job-summary-heading">
  <h2 id="job-summary-heading">Summary</h2>
  <p class="detail-kicker">Identity</p>
  <dl class="kv">
    <dt>Name</dt><dd>{job.jobName}</dd>
    <dt>Namespace</dt><dd><a href={routes.namespace(job.jobNamespace, { namespace: job.jobNamespace })}>{job.jobNamespace}</a></dd>
    <dt>Tenant</dt><dd>{#if tenant}<a href={routes.tenant(tenant, { namespace: job.jobNamespace })} class="mono">{tenant}</a>{:else}-{/if}</dd>
    <dt>Priority</dt><dd>{job.priority}</dd>
    <dt>Input</dt><dd>{payloadFormatLabel(job.inputFormatId)}</dd>
  </dl>
  <p class="detail-kicker">Execution</p>
  <dl class="kv">
    <dt>Status</dt><dd><StatusBadge status={job.status} /></dd>
    <dt>Lane</dt><dd>{#if job.lane}<a href={routes.jobs({ lane: job.lane, namespace: job.jobNamespace })} class="mono">{job.lane}</a>{:else}-{/if}</dd>
    {#if job.status === 'blocked' && job.blockedBehindJobRef}
      <dt>Blocked behind</dt><dd><JobRef value={job.blockedBehindJobRef} href={routes.job(job.blockedBehindJobRef, { namespace: job.jobNamespace })} /></dd>
    {/if}
    {#if !TERMINAL_STATUSES.includes(job.status)}
      <dt>Next run</dt><dd>
        {#if job.status !== 'idle' || job.nextRunAtUtc}<RelativeTime value={job.nextRunAtUtc} />
        {:else if firing}
          {@const { fires, resumes } = firing}
          {#if fires}<RelativeTime value={fires.atUtc} /> <span class="dim">when schedule {@render scheduleLink(fires.schedule)} fires{#if resumes}; schedule {@render scheduleLink(resumes.schedule)} may fire first once it resumes{/if}</span>{#if resumes} <RelativeTime value={resumes.atUtc} />{/if}
          {:else if resumes}<span class="dim">Schedule {@render scheduleLink(resumes.schedule)} resumes</span> <RelativeTime value={resumes.atUtc} />{/if}
        {:else if firing === null}<span class="dim">No schedule is set to fire it.</span>
        {:else}<span class="dim">When one of its schedules fires.</span>{/if}
      </dd>
    {/if}
    <!-- On a permanent job MaxAttempts is each firing's own budget: a spent one returns the job to idle. -->
    {#if job.kind === 'permanent'}
      <dt>Attempts</dt><dd>{displayFormatter.number(job.executionNumber)} started across all firings, {failureCountText(job.failureCount)} consecutive failures in the latest firing{#if maxAttempts != null && !TERMINAL_STATUSES.includes(job.status)} <span class="dim">(each firing gets {displayFormatter.number(maxAttempts)} attempts)</span>{/if}</dd>
    {:else}
      <dt>Attempts</dt><dd>{displayFormatter.number(job.executionNumber)} started, {failureCountText(job.failureCount)} consecutive failures{#if maxAttempts != null && !TERMINAL_STATUSES.includes(job.status)} <span class="dim">(fails permanently at {displayFormatter.number(maxAttempts)})</span>{/if}</dd>
    {/if}
    <dt>Last event</dt><dd>
      {#if lastEvent}
        {@const reason = eventFacts(lastEvent.detailsText).reason}
        {lastEvent.eventCode}{#if reason} <span class="dim">· {reason}</span>{/if}{#if lastEvent.message} · “{lastEvent.message}”{/if}
        <RelativeTime value={lastEvent.createdAtUtc} />
      {:else}-{/if}
    </dd>
  </dl>
  <p class="detail-kicker">Correlation</p>
  <dl class="kv">
    <dt>Deduplication key</dt><dd>
      {#if job.deduplicationKey}<span class="mono">{job.deduplicationKey}</span> <CopyButton value={job.deduplicationKey} label="Copy key" />{:else}-{/if}
    </dd>
    <dt>Correlation id</dt><dd>
      {#if job.correlationKey}<a href={routes.jobs({ correlationKey: job.correlationKey, namespace: job.jobNamespace })} class="mono">{job.correlationKey}</a> <CopyButton value={job.correlationKey} label="Copy correlation id" />{:else}-{/if}
    </dd>
    <dt>Parent</dt><dd>{#if job.parentJobRef}<JobRef value={job.parentJobRef} href={routes.job(job.parentJobRef, { namespace: job.jobNamespace })} />{:else}-{/if}</dd>
    <dt>Lineage root</dt><dd>{#if job.lineageRootJobRef}<JobRef value={job.lineageRootJobRef} href={routes.job(job.lineageRootJobRef, { namespace: job.jobNamespace })} />{:else}<span class="dim">this job</span>{/if}</dd>
    <dt>Created</dt><dd><RelativeTime value={job.createdAtUtc} /> <span class="dim">{displayFormatter.timestamp(job.createdAtUtc)}</span></dd>
    <dt>Modified</dt><dd><RelativeTime value={job.modifiedAtUtc} /></dd>
  </dl>
</section>
