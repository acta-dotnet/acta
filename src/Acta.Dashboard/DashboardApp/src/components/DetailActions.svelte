<script lang="ts">
  import type { Snippet } from 'svelte';
  import Icon from './Icon.svelte';
  let { children }: { children: Snippet } = $props();
  let menu: HTMLDetailsElement | undefined = $state();
  // A confirmation a menu action opened may render at the page root; working in it is not leaving the menu, and
  // the menu must stay open so the dialog can return focus to the action that opened it.
  function dismiss(event: PointerEvent): void {
    if (!menu?.open || !(event.target instanceof Element) || menu.contains(event.target)) return;
    if (event.target.closest('[role="dialog"], .confirm-overlay')) return;
    menu.open = false;
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
