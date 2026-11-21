<script lang="ts">
  import { createInfiniteQuery, createQuery, keepPreviousData } from '@tanstack/svelte-query';
  import { api, sessionExpired, type EventItem, type JobStepItem, type Paged } from '../../api.ts';
  import { displayFormatter } from '../../format.ts';
  import { now } from '../../time';
  import { keys, listPage } from '../../query.ts';
  import DataTable from '../../components/DataTable.svelte';
  import JobTimeline from '../../components/JobTimeline.svelte';
  import StateView from '../../components/StateView.svelte';
  import TimeCell from '../../components/TimeCell.svelte';
  import { deriveExecutions, placeSteps, withRunningExecution } from './executionsState.ts';
  import { mergeJobEvents } from './jobDetailState.ts';
  import type { JobDetail } from './types.ts';
  import { livePaused, listRefetchInterval } from '../../polling.ts';

  let {
    jobRef,
    job,
    polling = true,
    nextRunAtUtc = null,
    nextRunLabel = 'Next run',
    initialAttempt = null,
    onEventsChange = () => {}
  }: {
    jobRef: string;
    job: Pick<JobDetail, 'status' | 'executionNumber' | 'leasedByWorkerRef' | 'modifiedAtUtc'>;
    polling?: boolean;
    nextRunAtUtc?: string | null;
    nextRunLabel?: string;
    initialAttempt?: number | null;
    onEventsChange?: (events: EventItem[]) => void;
  } = $props();

  // The router remounts this component for a different job ref.
  // svelte-ignore state_referenced_locally
  const endpoint = `jobs/${jobRef}/events`;
  const history = createInfiniteQuery(() => ({
    queryKey: keys.feed(endpoint, { pageSize: 100 }),
    queryFn: ({ pageParam, signal }: { pageParam: string | null; signal: AbortSignal }) =>
      api<Paged<EventItem>>(endpoint, { pageSize: 100, cursor: pageParam }, { signal }),
    initialPageParam: null as string | null,
    getNextPageParam: (last: Paged<EventItem>) => (last.hasMore ? last.nextCursor : null),
    // Loaded history is immutable. Polling this infinite query would refetch every accumulated page.
    staleTime: Infinity
  }));

  // Poll only the newest page, then merge it over the static accumulated history by durable event id.
  const head = createQuery(() => ({
    ...listPage<EventItem>(endpoint, {}, { pageSize: 100, cursor: null }),
    refetchInterval: polling && !$livePaused ? listRefetchInterval : false
  }));

  // A job that turns terminal wrote its last events after the previous poll, so stopping the poll reads once more.
  let wasPolling = false;
  $effect(() => {
    if (wasPolling && !polling) void head.refetch();
    wasPolling = polling;
  });

  let historyPages = $derived(history.data?.pages.map((page) => page.items) ?? []);
  let events: EventItem[] = $derived(mergeJobEvents(head.data?.items ?? [], historyPages));
  $effect(() => onEventsChange(events));

  // The newest page of the job's steps, for each execution's steps line; older steps are left off. It is read with the
  // timeline and again when the job's read says it may have changed, never on a poll of its own, and the lines in view
  // stay until the newer read lands. A read that fails draws no line.
  // svelte-ignore state_referenced_locally
  const stepsEndpoint = `jobs/${jobRef}/steps`;
  const steps = createQuery(() => ({
    queryKey: [...keys.list(stepsEndpoint, { pageSize: 100 }), job.modifiedAtUtc],
    queryFn: ({ signal }: { signal: AbortSignal }) => api<Paged<JobStepItem>>(stepsEndpoint, { pageSize: 100 }, { signal }),
    placeholderData: keepPreviousData,
    staleTime: Infinity
  }));
  let stepLines = $derived(placeSteps(withRunningExecution(deriveExecutions(events), job), steps.data?.items ?? []));

  export function refresh(): void {
    void head.refetch();
  }
</script>

<section class="panel" id="job-timeline" aria-labelledby="job-timeline-heading">
  <div class="panel-heading">
    <h2 id="job-timeline-heading">Timeline</h2>
  </div>

  {#if events.length > 0}
    {#if head.error && !$sessionExpired}<p class="control-message warn">Loaded event history is still visible, but the latest refresh failed: {head.error.message}</p>{/if}
    {#if history.error && !$sessionExpired}<p class="control-message warn">Latest events are still visible, but older history could not be loaded: {history.error.message}</p>{/if}
    <JobTimeline
      {events}
      {stepLines}
      {nextRunAtUtc}
      {nextRunLabel}
      {initialAttempt}
      hasMore={history.hasNextPage}
      loadingMore={history.isFetchingNextPage}
      onLoadMore={() => history.fetchNextPage()} />
  {:else}
    <StateView loading={history.isPending && head.isPending} error={head.error?.message ?? history.error?.message ?? null} emptyText="No events retained for this job." onRetry={refresh} />
  {/if}

  {#if events.length > 0}
    <details class="raw-events">
      <summary>Raw events ({displayFormatter.number(events.length)} loaded) <span class="dim">· {displayFormatter.zoneNote($now)}</span></summary>
      <DataTable>
        <caption class="sr-only">Loaded raw job events</caption>
        <thead><tr><th>Time</th><th>Event</th><th class="col-num">Execution</th><th class="col-num">Duration</th><th>Message</th><th>Actor</th><th>Details</th></tr></thead>
        <tbody>
          {#each events as event (event.eventId)}
            <tr>
              <td><TimeCell value={event.createdAtUtc} /></td><td>{event.eventCode}</td><td class="col-num">{event.executionNumber == null ? '·' : displayFormatter.number(event.executionNumber)}</td>
              <td class="col-num">{event.durationMs != null ? displayFormatter.milliseconds(event.durationMs) : '·'}</td>
              <td class="wrap">{event.message ?? '·'}</td><td class="wrap">{event.actor ?? '·'}</td><td class="wrap mono">{event.detailsText ?? '·'}</td>
            </tr>
          {/each}
        </tbody>
      </DataTable>
    </details>
  {/if}
</section>

<style>
  .panel-heading { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
  .panel-heading h2 { margin: 0; }
  .raw-events { margin-top: 16px; }
</style>
