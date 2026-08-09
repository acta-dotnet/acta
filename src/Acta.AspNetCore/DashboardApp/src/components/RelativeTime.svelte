<script>
  import { now, secondNow } from '../time';
  import { displayFormatter } from '../format';
  let { value, emptyText = '·', title = null } = $props();

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

{#if value}
  <span title={title ?? displayFormatter.timestamp(value)}>{displayFormatter.relativeTime(value, tick)}</span>
{:else}
  <span class="dim">{emptyText}</span>
{/if}
