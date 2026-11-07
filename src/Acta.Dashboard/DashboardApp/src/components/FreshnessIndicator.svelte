<script lang="ts">
  // A compact "is what I'm looking at current?" strip for a query. Visual timestamps use the shared
  // opt-in second clock; the live region below only reports meaningful state transitions.
  import { secondNow } from '../time';
  import { online } from '../api';
  import { livePaused } from '../polling';
  import { displayFormatter } from '../format';
  import Icon from './Icon.svelte';

  let {
    dataUpdatedAt = 0,
    isFetching = false,
    isError = false,
    polling = true,
    onRefresh = () => {}
  }: {
    dataUpdatedAt?: number;
    isFetching?: boolean;
    isError?: boolean;
    polling?: boolean;
    onRefresh?: () => void;
  } = $props();

  let stampIso = $derived(dataUpdatedAt ? new Date(dataUpdatedAt).toISOString() : '');
  let displayedNow = $derived(Math.max($secondNow, dataUpdatedAt));
  let announcement = $state('');
  let initialized = false;
  let previousOnline = true;
  let previousFetching = false;
  let previousError = false;
  let previousPaused = false;

  let label = $derived.by(() => {
    if (!$online) return stampIso ? 'Offline, showing data from ' + displayFormatter.timestamp(stampIso) : 'Offline';
    if (polling && $livePaused) return 'Live updates paused';
    if (isFetching) return 'Updating…';
    if (isError) return polling ? 'Update failed, retrying' : 'Update failed';
    if (stampIso) return 'Updated ' + displayFormatter.relativeTime(stampIso, displayedNow);
    return '';
  });

  // Dot color tracks the worst active condition so the status reads at a glance.
  let dotKind = $derived(!$online || isError ? 'bad' : polling && $livePaused ? 'paused' : isFetching ? 'live' : 'ok');

  $effect(() => {
    const currentOnline = $online;
    const currentPaused = polling && $livePaused;

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
  <span class="freshness-dot {dotKind}" aria-hidden="true"></span>
  <span class="freshness-label">{label}</span>
  <button class="iconly" title="Refresh now" aria-label="Refresh now" onclick={() => onRefresh()}>
    <Icon name="reload" />
  </button>
  {#if polling}
    <button
      class="iconly"
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
  .freshness {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    color: var(--muted);
    font-size: var(--text-xs);
  }
  .freshness-dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    flex: none;
    background: var(--muted);
  }
  .freshness-dot.ok { background: var(--ok); }
  .freshness-dot.live { background: var(--accent); }
  .freshness-dot.paused { background: var(--muted); }
  .freshness-dot.bad { background: var(--bad); }
  .freshness-label { white-space: nowrap; }
  .freshness .iconly { padding: 3px 4px; }
</style>
