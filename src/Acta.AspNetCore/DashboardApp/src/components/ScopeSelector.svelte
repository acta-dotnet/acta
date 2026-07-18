<script>
  import { onMount } from 'svelte';
  import { get } from 'svelte/store';
  import { api } from '../api';
  import { scope, setScope } from '../scope';
  import { isSysNamespace } from '../routes/namespaceAdmin.ts';

  // A plain dropdown of namespaces led by "All namespaces" (the global default). The catalog read is
  // paged, so we walk every page via the cursor to load the full namespace list (the catalog is small);
  // a scope set from the URL that isn't present is still kept as its own option so the selection shows.
  // The typed admin catalog carries the stable namespace id, letting us exclude seeded id 1 without
  // guessing its name. A system scope supplied in the URL is cleared to the global scope.
  let namespaces = $state([]);

  onMount(async () => {
    try {
      const all = [];
      let cursor;
      // Bound the walk so a misbehaving cursor can never spin forever; 100 pages * 100 = 10k namespaces.
      for (let guard = 0; guard < 100; guard++) {
        const page = await api('namespaces/admin', { pageSize: 100, cursor });
        all.push(...(page.items ?? []));
        if (!page.hasMore || !page.nextCursor) break;
        cursor = page.nextCursor;
      }
      const system = all.find((item) => isSysNamespace(item.id));
      if (system && get(scope) === system.name) setScope('');
      namespaces = all.filter((item) => !isSysNamespace(item.id)).map((item) => item.name);
    } catch {
      namespaces = [];
    }
  });
</script>

<label class="scope">
  Scope
  <select value={$scope} onchange={(e) => setScope(e.currentTarget.value)} title={$scope || 'All namespaces'}>
    <option value="">All namespaces</option>
    {#if $scope && !namespaces.includes($scope)}
      <option value={$scope}>{$scope}</option>
    {/if}
    {#each namespaces as ns}
      <option value={ns}>{ns}</option>
    {/each}
  </select>
</label>
