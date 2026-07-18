<script>
  import { now } from '../time';
  import { displayFormatter } from '../format';
  let { value, emptyText = '-', title = null } = $props();
</script>

{#if value}
  <span title={title ?? displayFormatter.timestamp(value)}>{displayFormatter.relativeTime(value, $now)}</span>
{:else}
  <span class="dim">{emptyText}</span>
{/if}
