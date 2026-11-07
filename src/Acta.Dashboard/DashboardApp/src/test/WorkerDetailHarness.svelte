<script lang="ts">
  import { QueryClient } from '@tanstack/query-core';
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import WorkerDetail from '../routes/WorkerDetail.svelte';

  let { workerRef }: { workerRef: string } = $props();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<QueryClientProvider {client}>
  {#snippet children()}<WorkerDetail {workerRef} />{/snippet}
</QueryClientProvider>
