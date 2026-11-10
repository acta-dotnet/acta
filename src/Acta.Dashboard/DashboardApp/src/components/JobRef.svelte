<script lang="ts">
  import CopyButton from './CopyButton.svelte';
  let { value, href = null, copy = true, compact = true }: { value: string; href?: string | null; copy?: boolean; compact?: boolean } = $props();
  // Public refs are a type tag ('job_') + a UUIDv7's 32 hex digits. The first 12 encode the creation
  // millisecond, so rows created together share them; only the tail differs. Compact shows the tag and
  // the last 8 digits; the whole ref stays in the text for a selection or a find, and in the link's name.
  const match = $derived(/^([a-z0-9]+_)([0-9a-f]{12})([0-9a-f]{12})([0-9a-f]{8})$/i.exec(value ?? ''));
  const title = $derived(compact ? value : undefined);
  const detailId = $props.id();
</script>

{#snippet parts()}
  {#if match && compact}<span class="ref-head">{match[1].slice(0, -1)}</span><span class="ref-gap">_{match[2]}{match[3]}</span><span class="ref-tail">{match[4]}</span>{:else if match}<span class="ref-head">{match[1]}{match[2]}</span><span class="ref-tail">{match[3]}{match[4]}</span>{:else}{value}{/if}
{/snippet}

<span class="ref-group">
{#if href}
  <a class="mono jobref" {href} {title} aria-label={title}>{@render parts()}</a>
{:else}
  {#if compact}
    <button class="mono jobref value-detail" type="button" {title} popovertarget={detailId} aria-label={'Show full reference ' + value}>{@render parts()}</button>
    <span id={detailId} popover="auto" class="technical-popover" role="note"><strong>Full reference</strong><br /><span class="mono">{value}</span> <CopyButton {value} label="Copy full reference" /></span>
  {:else}<span class="mono jobref">{@render parts()}</span>{/if}
{/if}
{#if copy}<span class="ref-copy"><CopyButton {value} label="Copy ref" /></span>{/if}
</span>

<style>
  .ref-group { display: inline-flex; align-items: baseline; }
  .jobref { white-space: nowrap; font-family: var(--mono); }
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
  .ref-group:hover .ref-copy,
  .ref-group:focus-within .ref-copy,
  .ref-copy:focus-within { opacity: 1; }
  @media (hover: none) { .ref-copy { opacity: 1; } }
</style>
