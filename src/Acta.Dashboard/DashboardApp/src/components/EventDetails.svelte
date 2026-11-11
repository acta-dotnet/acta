<script lang="ts">
  // An event's message and details on one line of its row, ending in an ellipsis; the whole text opens in the card.
  import CopyButton from './CopyButton.svelte';
  import { hoverPopover } from '../lib/hoverPopover';
  let { message, details }: { message: string | null; details: string | null } = $props();
  const detailId = $props.id();
  const anchor = `--details-${detailId}`;
</script>

<button class="value-detail event-details" type="button" popovertarget={detailId} style:anchor-name={anchor} use:hoverPopover aria-label={'Show full details: ' + [message, details].filter(Boolean).join(' ')}>{message}{#if details}{' '}<span class="mono dim">{details}</span>{/if}</button>
<span id={detailId} popover="auto" class="technical-popover" style:position-anchor={anchor} role="note"><strong>Details</strong>{#if message}<br />{message}{/if}{#if details}<br /><span class="mono">{details}</span> <CopyButton value={details} label="Copy details" />{/if}</span>

<style>
  .event-details {
    display: block;
    width: 100%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    text-align: left;
  }
</style>
