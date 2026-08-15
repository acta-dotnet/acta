<script>
  import { onMount } from 'svelte';
  import { get } from 'svelte/store';
  import { api } from '../api';
  import { scope, setScope } from '../scope';
  import { isSysNamespace } from '../routes/namespaceAdmin.ts';
  import Dropdown from './Dropdown.svelte';

  // The namespace scope, led by "All namespaces" (the global default). The listbox, keyboard
  // handling, outside-click and typeahead all come from Dropdown; what is left here is the part
  // that is actually about namespaces.
  //
  // The catalog read is paged, so we walk every page via the cursor to load the full list (the
  // catalog is small). A scope set from the URL that is not present is still kept as its own option
  // so the selection shows. The typed catalog carries the stable namespace id, letting us exclude
  // seeded id 1 without guessing its name; a system scope supplied in the URL is cleared to global.
  let namespaces = $state([]);

  let options = $derived([
    { value: '', label: 'All namespaces' },
    ...($scope && !namespaces.includes($scope) ? [{ value: $scope, label: $scope }] : []),
    ...namespaces.map((name) => ({ value: name, label: name })),
  ]);

  onMount(async () => {
    try {
      const all = [];
      let cursor;
      // Bound the walk so a misbehaving cursor can never spin forever; 100 pages * 100 = 10k namespaces.
      for (let guard = 0; guard < 100; guard++) {
        const page = await api('namespaces', { pageSize: 100, cursor });
        all.push(...(page.items ?? []));
        if (!page.hasMore || !page.nextCursor) break;
        cursor = page.nextCursor;
      }
      const system = all.find((item) => isSysNamespace(item.jobNamespace));
      if (system && get(scope) === system.jobNamespace) setScope('');
      namespaces = all.filter((item) => !isSysNamespace(item.jobNamespace)).map((item) => item.jobNamespace);
    } catch {
      namespaces = [];
    }
  });
</script>

<!-- No visible label: the trigger shows the current scope, so the accessible name rides aria-label
     (Dropdown's `label`) and Dropdown puts the selected label on title as the hover hint. -->
<Dropdown {options} value={$scope} label="Namespace" onchange={setScope} />
