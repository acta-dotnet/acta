<script>
  import { onMount, tick } from 'svelte';
  import { get } from 'svelte/store';
  import { api } from '../api';
  import { scope, setScope } from '../scope';
  import { isSysNamespace } from '../routes/namespaceAdmin.ts';

  // A dropdown of namespaces led by "All namespaces" (the global default). The catalog read is
  // paged, so we walk every page via the cursor to load the full namespace list (the catalog is small);
  // a scope set from the URL that isn't present is still kept as its own option so the selection shows.
  // The typed admin catalog carries the stable namespace id, letting us exclude seeded id 1 without
  // guessing its name. A system scope supplied in the URL is cleared to the global scope.
  // Not a native <select>: its OS-drawn popup ignores the theme (a white sheet on the dark themes),
  // so the list is an in-page popover like the appearance menu.
  let namespaces = $state([]);
  let open = $state(false);
  let rootElement = $state(null);
  let triggerElement = $state(null);
  let optionButtons = $state([]);

  let options = $derived([
    '',
    ...($scope && !namespaces.includes($scope) ? [$scope] : []),
    ...namespaces,
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
      const system = all.find((item) => isSysNamespace(item.id));
      if (system && get(scope) === system.name) setScope('');
      namespaces = all.filter((item) => !isSysNamespace(item.id)).map((item) => item.name);
    } catch {
      namespaces = [];
    }
  });

  async function toggle() {
    open = !open;
    if (open) {
      await tick();
      const current = options.indexOf(get(scope));
      optionButtons[current >= 0 ? current : 0]?.focus();
    }
  }

  function close(restoreFocus) {
    if (!open) return;
    open = false;
    if (restoreFocus) triggerElement?.focus();
  }

  function choose(ns) {
    setScope(ns);
    close(true);
  }

  function handleListKeyDown(event) {
    const index = optionButtons.indexOf(document.activeElement);
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      optionButtons[Math.min(index + 1, options.length - 1)]?.focus();
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      optionButtons[Math.max(index - 1, 0)]?.focus();
    } else if (event.key === 'Home') {
      event.preventDefault();
      optionButtons[0]?.focus();
    } else if (event.key === 'End') {
      event.preventDefault();
      optionButtons[options.length - 1]?.focus();
    }
  }

  function handleWindowPointerDown(event) {
    if (open && rootElement && event.target instanceof Node && !rootElement.contains(event.target)) {
      close(false);
    }
  }

  function handleWindowKeyDown(event) {
    if (event.key === 'Escape' && open) {
      event.preventDefault();
      close(true);
    }
  }
</script>

<svelte:window onpointerdown={handleWindowPointerDown} onkeydown={handleWindowKeyDown} />

<div class="scope" bind:this={rootElement}>
  <!-- No visible label: the trigger shows the current scope. The accessible name has to come from
       somewhere, so it rides aria-label, and title doubles as the hover hint. -->
  <button
    type="button"
    class="trigger"
    bind:this={triggerElement}
    aria-haspopup="listbox"
    aria-expanded={open}
    aria-controls="scope-listbox"
    aria-label="Namespace"
    title={$scope || 'All namespaces'}
    onclick={toggle}>
    {$scope || 'All namespaces'}
  </button>
  {#if open}
    <div id="scope-listbox" class="listbox" role="listbox" tabindex="-1" aria-label="Namespace" onkeydown={handleListKeyDown}>
      {#each options as ns, index (ns)}
        <button
          type="button"
          role="option"
          class="option"
          class:selected={$scope === ns}
          bind:this={optionButtons[index]}
          aria-selected={$scope === ns}
          onclick={() => choose(ns)}>
          {ns || 'All namespaces'}
        </button>
      {/each}
    </div>
  {/if}
</div>

<style>
  .scope { position: relative; }

  .listbox {
    position: absolute;
    left: 12px;
    right: 12px;
    top: calc(100% + 4px);
    z-index: 20;
    max-height: min(48dvh, 420px);
    overflow-y: auto;
    overscroll-behavior: contain;
    padding: 4px;
    display: flex;
    flex-direction: column;
    background: var(--panel);
    border: 1px solid var(--line);
    border-radius: var(--radius-panel);
    box-shadow: 0 8px 30px var(--shadow);
  }

  .option {
    padding: 7px 8px;
    border: 0;
    border-radius: var(--radius-control);
    background: transparent;
    color: var(--ink);
    font: inherit;
    font-family: var(--mono);
    text-align: left;
    cursor: pointer;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .option:hover { background: var(--panel-subtle); }
  .option.selected { color: var(--accent); font-weight: 600; }
</style>
