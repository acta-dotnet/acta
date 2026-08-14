<script lang="ts">
  import RelativeTime from '../../components/RelativeTime.svelte';
  import type { JobExplanation } from './types.ts';
  import { routes } from '../../routes.ts';

  let {
    jobNamespace = null,
    explanation = null
  }: {
    jobNamespace?: string | null;
    explanation?: JobExplanation | null;
  } = $props();

  const isSignal = (kind: string) => kind.toLowerCase() === 'signal';
</script>

{#if explanation}
  <section class="panel explain" id="job-explain" aria-labelledby="job-diagnosis-heading">
    <h2 id="job-diagnosis-heading">Evidence</h2>

    <dl class="kv">
      {#if explanation.activeWait}
        <dt>Waiting on</dt>
        <dd id="job-wait-evidence">{isSignal(explanation.activeWait.kind) ? 'signal' : 'timer'} <span class="mono">{explanation.activeWait.name}</span>{#if explanation.activeWait.dueAtUtc}<span class="dim"> · due <RelativeTime value={explanation.activeWait.dueAtUtc} /></span>{/if}</dd>
      {/if}
      {#if explanation.lease}
        <dt>Worker</dt>
        <dd id="job-worker-evidence"><a class="mono" href={routes.worker(explanation.lease.workerId, { namespace: jobNamespace })}>{explanation.lease.workerName ?? '#' + explanation.lease.workerId}</a>{#if explanation.lease.expired}<span class="dim"> · lease expired <RelativeTime value={explanation.lease.expiresAtUtc} /></span>{/if}{#if explanation.lease.workerLastHeartbeatAtUtc}<span class="dim"> · last heartbeat <RelativeTime value={explanation.lease.workerLastHeartbeatAtUtc} /></span>{/if}
          {#if explanation.lease.expired}<div class="dim">{explanation.lease.recoveryExpectation}</div>{/if}</dd>
      {:else if explanation.lastExecutedBy}
        <dt>Worker</dt><dd class="dim">last executed on <span class="mono">{explanation.lastExecutedBy}</span></dd>
      {/if}
      {#if explanation.steps.length > 0}
        <dt>Steps</dt>
        <dd id="job-step-evidence">{#each explanation.steps as step}<div><span class="mono">{step.name}</span>: {step.explanation}</div>{/each}</dd>
      {/if}
      {#if explanation.reason}
        <dt>Reason</dt><dd id="job-reason-evidence" class="dim">{explanation.reason}</dd>
      {/if}
    </dl>
  </section>
{/if}
