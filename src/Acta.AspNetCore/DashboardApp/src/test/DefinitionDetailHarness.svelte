<script lang="ts">
  import { QueryClient } from '@tanstack/query-core';
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import DefinitionDetail from '../routes/DefinitionDetail.svelte';

  let { jobNamespace, jobName }: { jobNamespace: string; jobName: string } = $props();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<QueryClientProvider {client}>
  {#snippet children()}<DefinitionDetail {jobNamespace} {jobName} />{/snippet}
</QueryClientProvider>
