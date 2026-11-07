<script lang="ts">
  import { QueryClient } from '@tanstack/query-core';
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import JobExecutionsPanel from '../routes/job-detail/JobExecutionsPanel.svelte';
  import type { JobDetail } from '../routes/job-detail/types.ts';

  let { job }: { job: JobDetail } = $props();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<QueryClientProvider {client}>
  {#snippet children()}<JobExecutionsPanel jobRef={job.jobRef} {job} polling={false} />{/snippet}
</QueryClientProvider>
