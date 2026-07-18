<script lang="ts">
  import { QueryClient } from '@tanstack/query-core';
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import WorkerDetail from '../routes/WorkerDetail.svelte';

  let { workerId }: { workerId: number } = $props();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<QueryClientProvider {client}>
  {#snippet children()}<WorkerDetail {workerId} />{/snippet}
</QueryClientProvider>
