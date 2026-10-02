<script lang="ts">
  import { QueryClient } from '@tanstack/query-core';
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import JobControls from '../components/JobControls.svelte';
  import ScheduleControls from '../components/ScheduleControls.svelte';

  let { kind }: { kind: 'job' | 'schedule' | 'schedule-editor' } = $props();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<QueryClientProvider {client}>
  {#snippet children()}
    {#if kind === 'job'}
      <JobControls jobRef="job_test" status="ready" />
    {:else if kind === 'schedule-editor'}
      <ScheduleControls
        jobNamespace="billing"
        jobName="invoice"
        scheduleName="daily"
        status="active"
        version={4}
        mode="editor" />
    {:else}
      <ScheduleControls
        jobNamespace="billing"
        jobName="invoice"
        scheduleName="daily"
        status="active"
        version={1}
        mode="actions" />
    {/if}
  {/snippet}
</QueryClientProvider>
