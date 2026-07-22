<script lang="ts">
  import RelativeTime from '../../components/RelativeTime.svelte';
  import type { JobSnapshot, JobWorker } from './types.ts';
  import { routes } from '../../routes.ts';
  import { displayFormatter } from '../../format.ts';

  let {
    job,
    workers = null
  }: { job: JobSnapshot; workers?: JobWorker[] | null } = $props();

  let dueInFuture = $derived(job.nextRunAtUtc ? new Date(job.nextRunAtUtc).getTime() > Date.now() : false);
  let liveWorkerCount = $derived(workers?.filter((worker) => worker.status === 'active' || worker.status === 'draining').length ?? null);
</script>

{#if job.status === 'ready'}
  <section class="panel" aria-labelledby="job-worker-heading">
    <h2 id="job-worker-heading">Why isn’t this running?</h2>
    {#if dueInFuture}
      <p>Scheduled to run <RelativeTime value={job.nextRunAtUtc} />: it is waiting for its next-run time, not stuck.</p>
    {:else if workers && liveWorkerCount === 0}
      <p>This job is <strong>ready</strong> but no live worker can claim it in namespace <span class="mono">{job.jobNamespace}</span> ({workers.length === 0 ? 'no workers seen' : displayFormatter.number(workers.length) + ' worker(s), none active'}).</p>
      <p class="dim">Start a worker for this namespace, or inspect the <a href={routes.workers({ namespace: job.jobNamespace })}>workers page</a>.</p>
    {:else if liveWorkerCount !== null && liveWorkerCount > 0}
      <p>{displayFormatter.number(liveWorkerCount)} live worker(s) in <span class="mono">{job.jobNamespace}</span> can claim this job; it should start shortly.</p>
    {/if}
  </section>
{/if}
