<script>
  import RelativeTime from './RelativeTime.svelte';
  import { displayFormatter } from '../format';
  let { value } = $props();
</script>

{#if value}
  <RelativeTime {value} />
  <span class="dim">{displayFormatter.rowTimestamp(value)}</span>
{:else}
  <span class="dim">-</span>
{/if}
