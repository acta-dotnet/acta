<script>
  let {
    loading = false,
    error = null,
    loadingText = 'Loading...',
    emptyText = 'Nothing here.',
    onRetry = null,
    loadingDelayMs = 200
  } = $props();

  // Show the loading line only if the load outlasts this delay, so a fast load (e.g. a local backend
  // on F5) never flashes "Loading..." before the grid appears.
  let showLoading = $state(false);
  let timer;

  $effect(() => {
    clearTimeout(timer);
    if (loading) {
      timer = setTimeout(() => (showLoading = true), loadingDelayMs);
    } else {
      showLoading = false;
    }

    return () => clearTimeout(timer);
  });
</script>

{#if loading}
  {#if showLoading}<div class="state">{loadingText}</div>{/if}
{:else if error}
  <div class="state error" role="alert">
    <span>{error}</span>
    {#if onRetry}
      <button onclick={() => onRetry()}>Retry</button>
    {/if}
  </div>
{:else}
  <div class="state">{emptyText}</div>
{/if}
