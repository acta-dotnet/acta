<script>
  import { now } from '../time';
  import { displayFormatter } from '../format';
  import Icon from './Icon.svelte';

  let { status, pausedUntilUtc = null } = $props();
  let countdownNow = $state(Date.now());
  let target = $derived(pausedUntilUtc ? new Date(pausedUntilUtc).getTime() : 0);
  let displayedNow = $derived(
    target && Math.abs(target - countdownNow) <= 60_000 ? countdownNow : Math.max($now, countdownNow)
  );

  $effect(() => {
    const current = Math.max($now, Date.now());
    if (!target || Math.abs(target - current) > 60_000) return;
    countdownNow = current;
    const id = setInterval(() => {
      countdownNow = Date.now();
      if (countdownNow > target + 60_000) clearInterval(id);
    }, 1000);
    return () => clearInterval(id);
  });
</script>

{#if status === 'paused'}
  <span class="badge held"><Icon name="pause" />paused</span>
  {#if pausedUntilUtc}
    <span class="release" title={displayFormatter.timestamp(pausedUntilUtc)}>resumes {displayFormatter.relativeTime(pausedUntilUtc, displayedNow)}</span>
  {:else}
    <span class="release dim">until resumed</span>
  {/if}
{:else if status === 'orphaned'}
  <span class="badge retired" title="The origin declaration was removed from the catalog."><Icon name="minus-circle" />orphaned</span>
{:else}
  <span class="badge ok"><Icon name="check-circle" />{status}</span>
{/if}

<style>
  .release {
    display: block;
    font-size: var(--text-sm);
    color: var(--muted);
    margin-top: 3px;
  }
</style>
