<script lang="ts">
  import CopyButton from './CopyButton.svelte';
  let { value, href = null, copy = false, compact = false }: { value: string; href?: string | null; copy?: boolean; compact?: boolean } = $props();
  // Public refs are a type tag ('job_') + a UUIDv7's 32 hex digits. The first 12 encode the creation
  // millisecond, so rows created together share them; only the tail differs. Compact shows the tag and
  // the last 8 digits; the whole ref stays in the text for a selection or a find, and in the link's name.
  const match = $derived(/^([a-z0-9]+_)([0-9a-f]{12})([0-9a-f]{12})([0-9a-f]{8})$/i.exec(value ?? ''));
  const title = $derived(compact ? value : undefined);
</script>

{#snippet parts()}
  {#if match && compact}<span class="ref-head">{match[1]}</span><span class="ref-gap">{match[2]}{match[3]}</span><span class="ref-tail">{match[4]}</span>{:else if match}<span class="ref-head">{match[1]}{match[2]}</span><span class="ref-tail">{match[3]}{match[4]}</span>{:else}{value}{/if}
{/snippet}

{#if href}
  <a class="mono jobref" {href} {title} aria-label={title}>{@render parts()}</a>
{:else}
  <span class="mono jobref" {title}>{@render parts()}</span>
{/if}
{#if copy}<span class="ref-copy"><CopyButton {value} label="Copy ref" /></span>{/if}

<style>
  .jobref { white-space: nowrap; }
  .ref-head { color: var(--muted); }
  /* The hidden digits sit behind an ellipsis one digit wide. The ellipsis is drawn, not written, so a copy or a
     screen reader gets the digits alone. */
  .ref-gap {
    display: inline-block;
    width: 1ch;
    overflow: hidden;
    vertical-align: bottom;
    color: var(--muted);
  }
  .ref-gap::before {
    content: '…';
    content: '…' / '';
    display: inline-block;
    width: 1ch;
  }
  /* Quiet until the row is under the pointer (or the button holds focus), so a refs column
     doesn't become a strip of glyph buttons. */
  .ref-copy { margin-left: 4px; opacity: 0; transition: opacity 0.12s ease; }
  :global(tr:hover) .ref-copy,
  .ref-copy:focus-within { opacity: 1; }
</style>
