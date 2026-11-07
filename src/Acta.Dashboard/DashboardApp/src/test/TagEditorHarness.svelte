<script lang="ts">
  import { QueryClient } from '@tanstack/query-core';
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import TagEditor from '../components/TagEditor.svelte';
  import type { PermissionName } from '../query.ts';

  let { path, permission = 'acta.control' }: { path: string; permission?: PermissionName } = $props();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<QueryClientProvider {client}>
  {#snippet children()}<TagEditor {path} {permission} />{/snippet}
</QueryClientProvider>
