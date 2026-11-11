<script>
  import { now, secondNow } from '../time';
  import { displayFormatter } from '../format';
  import { hoverPopover } from '../lib/hoverPopover';
  let { value, title = null } = $props();
  const detailId = $props.id();
  const anchor = `--time-${detailId}`;

  // Sub-minute values are the ones worth animating: the next run of a 5-second cron, a job that
  // just failed. Those follow the second clock; anything further out only changes on the minute
  // and stays on the shared minute clock. Both stores are subscribed, but the rendered text only
  // changes when the chosen tick changes, so a far-off timestamp costs nothing per second. Only
  // visible rows are mounted (ActaGrid virtualizes past 100 rows), so this stays cheap on a grid.
  let target = $derived(value ? Date.parse(value) : Number.NaN);
  let tick = $derived(
    Number.isFinite(target) && Math.abs(target - $now) < 90_000 ? $secondNow : $now
  );
</script>

<!-- The popover sits inside the time's own span: a sibling on its own line would leave a space before
     whatever punctuation follows the time. -->
{#if value}
  <span class="relative-time"><button class="value-detail" type="button" popovertarget={detailId} style:anchor-name={anchor} use:hoverPopover aria-label={`${displayFormatter.relativeTime(value, tick)}. Show exact date and time.`}>{displayFormatter.relativeTime(value, tick)}</button><span id={detailId} popover="auto" class="technical-popover" style:position-anchor={anchor} role="note"><strong>Date and time</strong><br />{title ?? displayFormatter.timestamp(value)}<br /><span class="mono dim">{value}</span></span></span>
{:else}
  <span class="dim">·</span>
{/if}
