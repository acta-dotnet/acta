<script lang="ts">
  import { QueryClient } from '@tanstack/query-core';
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import DefinitionPerformancePanel from '../routes/definition-detail/DefinitionPerformancePanel.svelte';

  let { eventRecording }: { eventRecording: string } = $props();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<QueryClientProvider {client}>
  {#snippet children()}<DefinitionPerformancePanel definitionPath="definitions/billing/send-invoice" jobNamespace="billing" {eventRecording} />{/snippet}
</QueryClientProvider>
