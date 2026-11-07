<script lang="ts">
  // A summary bar of the currently-active filters so an operator never overlooks a filter hidden in a
  // collapsed form. Each chip removes its own filter; the page owns the filter state and passes the
  // removal callbacks. Copy-link shares the exact filtered view (filters live in the URL hash).
  import Icon from './Icon.svelte';
  import { displayFormatter } from '../format.ts';

  export interface ActiveFilterChip {
    label: string;
    value: string;
    onRemove: () => void;
  }

  let { chips = [], onClearAll = () => {} }: { chips?: ActiveFilterChip[]; onClearAll?: () => void } = $props();

  let copied = $state(false);
  function copyLink(): void {
    navigator.clipboard
      ?.writeText(location.href)
      .then(() => {
        copied = true;
        setTimeout(() => (copied = false), 1500);
      })
      .catch(() => {});
  }
</script>

{#if chips.length > 0}
  <div class="active-filters" role="status">
    <span class="af-count">{displayFormatter.number(chips.length)} filter{chips.length === 1 ? '' : 's'} active</span>
    {#each chips as chip}
      <button class="af-chip" onclick={() => chip.onRemove()} title={'Remove the ' + chip.label + ' filter'}>
        <span><span class="af-chip-key">{chip.label}:</span> {chip.value}</span>
        <Icon name="x" />
      </button>
    {/each}
    <button class="af-clear" onclick={() => onClearAll()}>Clear all</button>
    <button
      class="iconly af-link"
      onclick={copyLink}
      title="Copy a link to this filtered view"
      aria-label="Copy a link to this filtered view">
      <Icon name="copy" />
    </button>
    {#if copied}<span class="dim af-copied" role="status">Link copied</span>{/if}
  </div>
{/if}

<style>
  .active-filters {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px;
    margin-bottom: 12px;
    font-size: var(--text-sm);
  }
  .af-count {
    color: var(--muted);
  }
  .af-chip {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 3px 6px 3px 10px;
    border: 1px solid var(--line);
    border-radius: 999px;
    background: var(--panel);
    color: var(--ink);
    font: inherit;
  }
  .af-chip:hover {
    border-color: var(--accent);
  }
  .af-chip-key {
    color: var(--muted);
  }
  .af-clear {
    padding: 3px 10px;
    background: transparent;
    color: var(--accent);
    border-color: transparent;
  }
  .af-clear:hover {
    text-decoration: underline;
  }
  .af-copied {
    font-size: var(--text-xs);
  }
</style>
