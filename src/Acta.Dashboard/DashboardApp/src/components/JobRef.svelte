<script lang="ts">
  import CopyButton from './CopyButton.svelte';
  import { hoverPopover } from '../lib/hoverPopover';
  let { value, href = null, copy = true }: { value: string; href?: string | null; copy?: boolean } = $props();
  // Public refs are a type tag ('job_') + a UUIDv7's 32 hex digits. The first 12 encode the creation
  // millisecond, so rows created together share them; only the tail differs. The display shows the tag and
  // the last 8 digits; the whole ref stays in the text for a selection or a find, and in the link's name.
  const match = $derived(/^([a-z0-9]+_)([0-9a-f]{12})([0-9a-f]{12})([0-9a-f]{8})$/i.exec(value ?? ''));
  const detailId = $props.id();
  const anchor = `--ref-${detailId}`;
</script>

{#snippet parts()}
  {#if match}<span class="ref-head">{match[1].slice(0, -1)}</span><span class="ref-gap">_{match[2]}{match[3]}</span><span class="ref-tail">{match[4]}</span>{:else}{value}{/if}
{/snippet}

<span class="ref-group">
{#if href}
  <a class="mono jobref" {href} title={value} aria-label={value}>{@render parts()}</a>
{:else}
  <button class="mono jobref value-detail" type="button" popovertarget={detailId} style:anchor-name={anchor} use:hoverPopover aria-label={'Show full reference ' + value}>{@render parts()}</button>
  <span id={detailId} popover="auto" class="technical-popover" style:position-anchor={anchor} role="note"><strong>Full reference</strong><br /><span class="ref-line"><span class="mono">{value}</span><CopyButton {value} label="Copy full reference" /></span></span>
{/if}
{#if copy}<span class="ref-copy"><CopyButton {value} label="Copy ref" /></span>{/if}
</span>

<style>
  /* The group, and the popover's line, sit on the text baseline as one word; inside each the copy glyph centres on
     the reference, which a baseline alignment would rest on its bottom edge, without the 1px nudge that sets an icon
     beside a button's lowercase label. */
  .ref-group, .ref-line { display: inline-flex; align-items: center; }
  .ref-line { gap: 6px; }
  .ref-group :global(.copy .ic), .ref-line :global(.copy .ic) { top: 0; }
  .jobref { white-space: nowrap; font-family: var(--mono); padding: 4px 0; margin: -4px 0; }
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
  .ref-copy { display: inline-flex; margin-left: 5px; opacity: 0; transition: opacity 0.12s ease; }
  :global(tr:hover) .ref-copy,
  .ref-group:hover .ref-copy,
  .ref-group:focus-within .ref-copy,
  .ref-copy:focus-within { opacity: 1; }
  @media (hover: none) { .ref-copy { opacity: 1; } }
</style>
