<script lang="ts">
  import { QueryClient } from '@tanstack/query-core';
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import TagEditor from '../components/TagEditor.svelte';

  let { path, title }: { path: string; title?: string } = $props();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<QueryClientProvider {client}>
  {#snippet children()}<TagEditor {path} {title} />{/snippet}
</QueryClientProvider>
