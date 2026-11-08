<script lang="ts">
  import { QueryClient } from '@tanstack/query-core';
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import AlertDetail from '../routes/AlertDetail.svelte';

  let { alertRef }: { alertRef: string } = $props();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<QueryClientProvider {client}>
  {#snippet children()}<AlertDetail {alertRef} />{/snippet}
</QueryClientProvider>
