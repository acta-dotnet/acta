<script>
  import { displayFormatter } from '../format';
  import {
    failedTimelineAttempts,
    matchesTimelineCategory,
    timelineAttemptNumbers,
    timelinePresentation
  } from './jobTimelineState.ts';
  import Icon from './Icon.svelte';
  import RelativeTime from './RelativeTime.svelte';

  let {
    events = [],
    nextRunAtUtc = null,
    hasMore = false,
    loadingMore = false,
    onLoadMore = () => {},
    initialAttempt = null
  } = $props();

  // Deliberate initial capture: the panel remounts per tab switch, re-reading the handoff.
  // svelte-ignore state_referenced_locally
  let attemptFilter = $state(initialAttempt != null ? String(initialAttempt) : 'all');
  let categoryFilter = $state('all');
  let eventFilter = $state('');

  let attemptNumbers = $derived(timelineAttemptNumbers(events));
  let failedAttempts = $derived(failedTimelineAttempts(events));

  // A preset attempt (deep link) may not exist in the loaded events: fall back to 'all' rather
  // than leaving the select blank over an empty rail.
  $effect(() => {
    if (attemptFilter !== 'all' && events.length > 0 && !attemptNumbers.includes(Number(attemptFilter))) {
      attemptFilter = 'all';
    }
  });
  let filteredEvents = $derived(
    events.filter(
      (evt) =>
        (attemptFilter === 'all' || String(evt.executionNumber ?? 0) === attemptFilter) &&
        matchesTimelineCategory(evt, categoryFilter) &&
        (!eventFilter.trim() || String(evt.eventCode).toLowerCase().includes(eventFilter.trim().toLowerCase()))
    )
  );

  let attempts = $derived(group(filteredEvents));
  // The newest visible event is the rail's one bold moment when it is a settled outcome. Events
  // arrive newest-first, so the head of the filtered list is the chronological tail of the rail
  // (a terminal job.cancelled lives in the Lifecycle group, not the highest attempt).
  let terminalEventId = $derived.by(() => {
    const newest = filteredEvents[0];
    if (!newest) return null;
    const tone = timelinePresentation(newest).tone;
    return tone === 'ok' || tone === 'bad' ? newest.jobEventId : null;
  });

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
        <div class="timeline-eyebrow">
          <span class:flag={failedAttempts.includes(attempt)}>{attempt === 0 ? 'Lifecycle' : 'Attempt ' + displayFormatter.number(attempt)}</span>
        </div>
        <ol class="rail">
          {#each steps as evt (evt.jobEventId)}
            {@const p = timelinePresentation(evt)}
            <li class={'t-' + p.tone}>
              <span class="rail-node" class:terminal={evt.jobEventId === terminalEventId}><Icon name={p.icon} /></span>
              <div class="rail-body">
                <div class="rail-title">
                  {p.title}
                  {#if evt.durationMs != null}<span class="rail-chip">{displayFormatter.milliseconds(evt.durationMs)}</span>{/if}
                </div>
                {#if evt.reasonMessage || evt.reasonCode}
                  <div class="rail-reason">
                    {#if evt.reasonCode}<span class="mono">{evt.reasonCode}</span>{/if}{#if evt.reasonCode && evt.reasonMessage}&nbsp;·&nbsp;{/if}{evt.reasonMessage ?? ''}
                  </div>
                {/if}
                <div class="rail-code">{evt.eventCode}</div>
              </div>
              <span class="rail-when"><RelativeTime value={evt.createdAtUtc} /></span>
            </li>
          {/each}
          {#if nextRunAtUtc && attempt === attempts[attempts.length - 1][0]}
            <li class="t-neutral future">
              <span class="rail-node ghost"><Icon name="clock" /></span>
              <div class="rail-body">
                <div class="rail-title">Next run</div>
                <div class="rail-code">due {displayFormatter.rowTimestamp(nextRunAtUtc)}</div>
              </div>
              <span class="rail-when"><RelativeTime value={nextRunAtUtc} /></span>
            </li>
          {/if}
        </ol>
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
