<script lang="ts">
  import { QueryClient } from '@tanstack/query-core';
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import JobControls from '../components/JobControls.svelte';

  let { jobRef, status }: { jobRef: string; status: string } = $props();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<QueryClientProvider {client}>
  {#snippet children()}<JobControls {jobRef} {status} />{/snippet}
</QueryClientProvider>
