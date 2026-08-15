<script lang="ts">
  import { displayFormatter, TERMINAL_STATUSES } from '../../format.ts';
  import CopyButton from '../../components/CopyButton.svelte';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import StatusBadge from '../../components/StatusBadge.svelte';
  import { payloadFormatLabel } from './jobDetailState.ts';
  import type { JobEvent, JobDetail } from './types.ts';
  import { routes } from '../../routes.ts';
  import JobRef from '../../components/JobRef.svelte';

  let { job, tenantKey = undefined, lastEvent = null, maxAttempts = null }: { job: JobDetail; tenantKey?: string; lastEvent?: JobEvent | null; maxAttempts?: number | null } = $props();

  // The aggregate echoes the tenant key at the top level; the job row carries it too, so either source serves.
  let tenant = $derived(tenantKey ?? job.tenantKey ?? null);
</script>

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
    {#if !TERMINAL_STATUSES.includes(job.status)}
      <dt>Next run</dt><dd><RelativeTime value={job.nextRunAtUtc} /></dd>
    {/if}
    <dt>Attempts</dt><dd>{displayFormatter.number(job.executionNumber)} started, {displayFormatter.number(job.failureCount)} consecutive failures{#if maxAttempts != null && !TERMINAL_STATUSES.includes(job.status)} <span class="dim">(fails permanently at {displayFormatter.number(maxAttempts)})</span>{/if}</dd>
    <dt>Last event</dt><dd>
      {#if lastEvent}
        {lastEvent.eventCode}{#if lastEvent.reasonCode} <span class="dim">· {lastEvent.reasonCode}</span>{/if}{#if lastEvent.reasonMessage} · “{lastEvent.reasonMessage}”{/if}
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
