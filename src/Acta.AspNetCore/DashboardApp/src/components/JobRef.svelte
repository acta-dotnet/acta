<script lang="ts">
  import CopyButton from './CopyButton.svelte';
  let { value, href = null, copy = false }: { value: string; href?: string | null; copy?: boolean } = $props();
  // Public refs are a type tag ('job_') + a UUIDv7's 32 hex digits. The first 12 encode the creation
  // millisecond, so rows created together share them; only the tail differs.
  const match = $derived(/^([a-z0-9]+_)([0-9a-f]{12})([0-9a-f]{20})$/i.exec(value ?? ''));
</script>

{#snippet parts()}
  {#if match}<span class="ref-head">{match[1]}{match[2]}</span><span class="ref-tail">{match[3]}</span>{:else}{value}{/if}
{/snippet}

{#if href}
  <a class="mono jobref" {href}>{@render parts()}</a>
{:else}
  <span class="mono jobref">{@render parts()}</span>
{/if}
{#if copy}<span class="ref-copy"><CopyButton {value} label="Copy ref" /></span>{/if}

<style>
  .jobref { white-space: nowrap; }
  .ref-head { color: var(--muted); }
  /* Quiet until the row is under the pointer (or the button holds focus), so a refs column
     doesn't become a strip of glyph buttons. */
  .ref-copy { margin-left: 4px; opacity: 0; transition: opacity 0.12s ease; }
  :global(tr:hover) .ref-copy,
  .ref-copy:focus-within { opacity: 1; }
</style>
