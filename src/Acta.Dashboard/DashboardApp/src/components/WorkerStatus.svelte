<script lang="ts">
  import StatusBadge from './StatusBadge.svelte';

  // stale is the server's verdict: a live worker with no heartbeat inside its lease window, so its jobs can be
  // reclaimed. It sits beside the status, which has not changed, rather than replacing it.
  let { status, stale = false }: { status: string; stale?: boolean } = $props();
</script>

<StatusBadge {status} />{#if stale}<span class="stale-mark" title="No heartbeat within the lease window, so its jobs can be reclaimed">stale</span>{/if}

<style>
  .stale-mark {
    margin-left: 8px;
    color: var(--warn);
    font-size: var(--text-sm);
    white-space: nowrap;
  }
</style>
