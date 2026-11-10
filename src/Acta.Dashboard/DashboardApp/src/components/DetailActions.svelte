<script lang="ts">
  import type { Snippet } from 'svelte';
  import Icon from './Icon.svelte';
  let { children }: { children: Snippet } = $props();
  let menu: HTMLDetailsElement | undefined = $state();
  function dismiss(event: PointerEvent): void {
    if (menu?.open && event.target instanceof Node && !menu.contains(event.target)) menu.open = false;
  }
  function escape(event: KeyboardEvent): void {
    if (event.key !== 'Escape' || !menu?.open || document.querySelector(':popover-open')) return;
    menu.open = false;
    menu.querySelector('summary')?.focus();
  }
</script>

<svelte:window onpointerdown={dismiss} onkeydown={escape} />

<details class="detail-actions" bind:this={menu}>
  <summary aria-label="Actions">Actions<Icon name="chevron-right" /></summary>
  <div class="detail-action-content">{@render children()}</div>
</details>
