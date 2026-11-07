<script lang="ts">
  import { QueryClient } from '@tanstack/query-core';
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import CalendarDetail from '../routes/CalendarDetail.svelte';

  let { jobNamespace, calendarName }: { jobNamespace: string; calendarName: string } = $props();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<QueryClientProvider {client}>
  {#snippet children()}<CalendarDetail {jobNamespace} {calendarName} />{/snippet}
</QueryClientProvider>
