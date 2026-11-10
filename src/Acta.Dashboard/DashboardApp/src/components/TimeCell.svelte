<script>
  import RelativeTime from './RelativeTime.svelte';
  let { value } = $props();
</script>

{#if value}
  <RelativeTime {value} />
{:else}
  <span class="dim">-</span>
{/if}
