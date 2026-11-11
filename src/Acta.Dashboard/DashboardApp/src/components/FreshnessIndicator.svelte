<script lang="ts">
  // A compact "is what I'm looking at current?" strip for a query. A polled view shows a meter that drains to the
  // bottom as its next refresh approaches, and words only for a state worth reading; the live region below reports
  // meaningful state transitions.
  import { now } from '../time';
  import { online, sessionExpired } from '../api';
  import { livePaused, listRefetchDelay } from '../polling';
  import { displayFormatter } from '../format';
  import Icon from './Icon.svelte';

  let {
    dataUpdatedAt = 0,
    isFetching = false,
    isError = false,
    polling = true,
    intervalMs,
    onRefresh = () => {}
  }: {
    dataUpdatedAt?: number;
    isFetching?: boolean;
    isError?: boolean;
    polling?: boolean;
    /** The polling interval, where it is not the list cadence. */
    intervalMs?: number;
    onRefresh?: () => void;
  } = $props();

  let stampIso = $derived(dataUpdatedAt ? new Date(dataUpdatedAt).toISOString() : '');
  let paused = $derived(polling && $livePaused);
  let failed = $derived(!$online || isError || $sessionExpired);
  // A query restarts its refetch timer when its data arrives, so a meter that restarts with the data runs out as
  // the next refetch fires.
  let counting = $derived(polling && !paused && !failed && dataUpdatedAt > 0);
  let drainMs = $derived(intervalMs ?? listRefetchDelay(dataUpdatedAt));
  let exact = $derived(stampIso ? 'Updated ' + displayFormatter.timestamp(stampIso) : '');
  let announcement = $state('');
  let initialized = false;
  let previousOnline = true;
  let previousFetching = false;
  let previousError = false;
  let previousPaused = false;

  let label = $derived.by(() => {
    if ($sessionExpired) return 'Session ended';
    if (!$online) return stampIso ? 'Offline, showing data from ' + displayFormatter.timestamp(stampIso) : 'Offline';
    if (paused) return 'Live updates paused';
    if (isError) return polling ? 'Update failed, retrying' : 'Update failed';
    if (polling) return '';
    if (isFetching) return 'Updating…';
    if (!stampIso) return '';
    // A view that does not refresh itself states its data's age to the minute, so the words rarely change.
    return $now - dataUpdatedAt < 60_000 ? 'Updated just now' : 'Updated ' + displayFormatter.relativeTime(stampIso, $now);
  });

  // The meter's colour tracks the worst active condition so the status reads at a glance.
  let tone = $derived(failed ? 'bad' : paused || !dataUpdatedAt ? 'idle' : 'ok');

  $effect(() => {
    const currentOnline = $online;
    const currentPaused = paused;

    if (initialized) {
      if (previousOnline !== currentOnline) {
        announcement = currentOnline ? 'Reconnected.' : 'Backend connection lost.';
      } else if (previousPaused !== currentPaused) {
        announcement = currentPaused ? 'Live updates paused.' : 'Live updates resumed.';
      } else if (!previousError && isError) {
        announcement = 'Refresh failed.';
      } else if (previousFetching && !isFetching && !isError) {
        announcement = 'Refresh succeeded.';
      }
    }

    initialized = true;
    previousOnline = currentOnline;
    previousFetching = isFetching;
    previousError = isError;
    previousPaused = currentPaused;
  });
</script>

<div class="freshness">
  <span class="freshness-meter {tone}" title={exact ? exact + (counting ? '. Refreshes when the bar runs out.' : '') : undefined} aria-hidden="true">
    <span class="freshness-track">
      {#if counting}
        {#key dataUpdatedAt}<span class="freshness-level draining" style:animation-duration="{drainMs}ms"></span>{/key}
      {:else}
        <span class="freshness-level"></span>
      {/if}
    </span>
  </span>
  {#if label}<span class="freshness-label">{label}</span>{:else if exact}<span class="sr-only">{exact}</span>{/if}
  <button class="iconly" title="Refresh now" aria-label="Refresh now" disabled={$sessionExpired} onclick={() => onRefresh()}>
    <Icon name="reload" />
  </button>
  {#if polling}
    <button
      class="iconly"
      disabled={$sessionExpired}
      title={$livePaused ? 'Resume live updates' : 'Pause live updates'}
      aria-label={$livePaused ? 'Resume live updates' : 'Pause live updates'}
      aria-pressed={$livePaused}
      onclick={() => livePaused.update((p) => !p)}>
      <Icon name={$livePaused ? 'play' : 'pause'} />
    </button>
  {/if}
  <span class="sr-only" aria-live="polite">{announcement}</span>
</div>

<style>
  /* The meter, reload, and pause are one instrument: one frame around equal cells, so they sit evenly spaced. */
  .freshness {
    display: inline-flex;
    align-items: center;
    padding: 2px;
    border: 1px solid var(--line-strong, var(--line));
    border-radius: var(--radius-control);
    background: var(--panel);
    color: var(--muted);
    font-size: var(--text-xs);
  }
  .freshness-meter,
  .freshness .iconly {
    display: inline-flex;
    flex: none;
    align-items: center;
    justify-content: center;
    width: 24px;
    height: 22px;
  }
  .freshness .iconly {
    padding: 0;
    border: 0;
    border-radius: calc(var(--radius-control) - 2px);
    background: none;
  }
  .freshness .iconly:hover:not(:disabled) { background: var(--panel-subtle); }
  .freshness-meter { color: var(--ok); }
  .freshness-meter.idle { color: var(--muted); }
  .freshness-meter.bad { color: var(--bad); }
  .freshness-track {
    width: 4px;
    height: 14px;
    border-radius: 2px;
    overflow: hidden;
    background: color-mix(in srgb, currentColor 24%, transparent);
  }
  .freshness-level {
    display: block;
    height: 100%;
    background: currentColor;
  }
  .draining { animation: freshness-drain linear forwards; }
  @keyframes freshness-drain {
    to { transform: translateY(100%); }
  }
  /* A still, full meter stands in for the countdown; the global rule would otherwise empty it at once. */
  @media (prefers-reduced-motion: reduce) {
    .draining { animation: none; }
  }
  .freshness-label { padding: 0 6px 0 2px; white-space: nowrap; }
</style>
