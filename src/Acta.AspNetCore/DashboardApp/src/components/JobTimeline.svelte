<script>
  import { displayFormatter, statusClass } from '../format';
  import {
    failedTimelineAttempts,
    matchesTimelineCategory,
    timelineAttemptNumbers
  } from './jobTimelineState.ts';
  import RelativeTime from './RelativeTime.svelte';

  let {
    events = [],
    hasMore = false,
    loadingMore = false,
    onLoadMore = () => {}
  } = $props();

  let attemptFilter = $state('all');
  let categoryFilter = $state('all');
  let eventFilter = $state('');

  let attemptNumbers = $derived(timelineAttemptNumbers(events));
  let failedAttempts = $derived(failedTimelineAttempts(events));
  let filteredEvents = $derived(
    events.filter(
      (evt) =>
        (attemptFilter === 'all' || String(evt.executionNumber ?? 0) === attemptFilter) &&
        matchesTimelineCategory(evt, categoryFilter) &&
        (!eventFilter.trim() || String(evt.eventCode).toLowerCase().includes(eventFilter.trim().toLowerCase()))
    )
  );

  let attempts = $derived(group(filteredEvents));

  function jumpToFailedAttempt() {
    if (failedAttempts.length > 0) attemptFilter = String(failedAttempts[0]);
  }

  function group(items) {
    const byAttempt = new Map();
    for (const evt of [...items].reverse()) {
      const key = evt.executionNumber ?? 0;
      if (!byAttempt.has(key)) {
        byAttempt.set(key, []);
      }
      byAttempt.get(key).push(evt);
    }
    return [...byAttempt.entries()].sort((a, b) => a[0] - b[0]);
  }
</script>

<div class="timeline-tools" aria-label="Timeline filters">
  <label>
    Attempt
    <select bind:value={attemptFilter}>
      <option value="all">All loaded attempts</option>
      {#each attemptNumbers as attempt}
        <option value={String(attempt)}>{attempt === 0 ? 'Lifecycle' : 'Attempt ' + displayFormatter.number(attempt)}</option>
      {/each}
    </select>
  </label>
  <label>
    Kind
    <select bind:value={categoryFilter}>
      <option value="all">All events</option>
      <option value="failure">Failures</option>
      <option value="control">Operator controls</option>
      <option value="signal">Signals</option>
      <option value="schedule">Schedules</option>
    </select>
  </label>
  <label>
    Event code
    <input bind:value={eventFilter} placeholder="e.g. job.execution" />
  </label>
  {#if failedAttempts.length > 0}
    <button onclick={jumpToFailedAttempt}>Jump to latest failed attempt</button>
  {/if}
</div>

{#if attempts.length === 0}
  <p class="dim">No loaded events match these filters.</p>
{:else}
  <div class="timeline">
    {#each attempts as [attempt, steps]}
      <div class="timeline-attempt" id={'attempt-' + attempt}>
        <span class="timeline-attempt-label">{attempt === 0 ? 'Lifecycle' : 'Attempt ' + displayFormatter.number(attempt)}</span>
        <div class="timeline-steps">
          {#each steps as evt}
            <span class="timeline-step">
              <span class="badge {statusClass(evt.toStatus ?? evt.executionStatus ?? '')}">{evt.eventCode}</span>
              {#if evt.durationMs != null}<span class="dim">{displayFormatter.milliseconds(evt.durationMs)}</span>{/if}
              {#if evt.reasonMessage || evt.reasonCode}
                <span class="timeline-reason">
                  {#if evt.reasonCode}<span class="mono">{evt.reasonCode}</span>{/if}{#if evt.reasonCode && evt.reasonMessage}: {/if}{evt.reasonMessage ?? ''}
                </span>
              {/if}
            </span>
          {/each}
        </div>
        <RelativeTime value={steps[steps.length - 1].createdAtUtc} />
      </div>
    {/each}
  </div>
{/if}

{#if hasMore}
  <div class="timeline-more">
    <button disabled={loadingMore} onclick={() => onLoadMore()}>{loadingMore ? 'Loading older history...' : 'Load older history'}</button>
    <span class="dim">{displayFormatter.number(events.length)} events loaded</span>
  </div>
{:else if events.length > 0}
  <p class="dim timeline-complete">Full retained history loaded · {displayFormatter.number(events.length)} events</p>
{/if}
