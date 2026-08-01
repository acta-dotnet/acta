<script lang="ts">
  import { createInfiniteQuery, createQuery } from '@tanstack/svelte-query';
  import { api, type Paged } from '../../api.ts';
  import { displayFormatter } from '../../format.ts';
  import { now } from '../../time';
  import { keys, listPage } from '../../query.ts';
  import DataTable from '../../components/DataTable.svelte';
  import JobTimeline from '../../components/JobTimeline.svelte';
  import StateView from '../../components/StateView.svelte';
  import type { JobEvent } from './types.ts';
  import { mergeJobEvents } from './jobDetailState.ts';
  import { livePaused, listRefetchInterval } from '../../polling.ts';

  let {
    jobRef,
    enabled = true,
    polling = true,
    nextRunAtUtc = null,
    onEventsChange = () => {}
  }: {
    jobRef: string;
    enabled?: boolean;
    polling?: boolean;
    nextRunAtUtc?: string | null;
    onEventsChange?: (events: JobEvent[]) => void;
  } = $props();

  // The router remounts this component for a different job ref.
  // svelte-ignore state_referenced_locally
  const endpoint = `jobs/${jobRef}/events`;
  const history = createInfiniteQuery(() => ({
    queryKey: keys.feed(endpoint, { pageSize: 100 }),
    queryFn: ({ pageParam, signal }: { pageParam: string | null; signal: AbortSignal }) =>
      api<Paged<JobEvent>>(endpoint, { pageSize: 100, cursor: pageParam }, { signal }),
    initialPageParam: null as string | null,
    getNextPageParam: (last: Paged<JobEvent>) => (last.hasMore ? last.nextCursor : null),
    enabled,
    // Loaded history is immutable. Polling this infinite query would refetch every accumulated page.
    staleTime: Infinity
  }));

  // Poll only the newest page, then merge it over the static accumulated history by durable event id.
  const head = createQuery(() => ({
    ...listPage<JobEvent>(endpoint, {}, { pageSize: 100, cursor: null }),
    enabled,
    refetchInterval: polling && !$livePaused ? listRefetchInterval : false
  }));

  let historyPages = $derived(history.data?.pages.map((page) => page.items) ?? []);
  let events: JobEvent[] = $derived(mergeJobEvents(head.data?.items ?? [], historyPages));
  $effect(() => onEventsChange(events));

  export function refresh(): void {
    void head.refetch();
  }
</script>

<section class="panel" id="job-timeline" aria-labelledby="job-timeline-heading">
  <div class="panel-heading">
    <h2 id="job-timeline-heading">Timeline</h2>
  </div>

  {#if events.length > 0}
    {#if head.error}<p class="control-message warn">Loaded event history is still visible, but the latest refresh failed: {head.error.message}</p>{/if}
    {#if history.error}<p class="control-message warn">Latest events are still visible, but older history could not be loaded: {history.error.message}</p>{/if}
    <JobTimeline
      {events}
      {nextRunAtUtc}
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
        <thead><tr><th>Time</th><th>Event</th><th>Attempt</th><th>From</th><th>To</th><th>Outcome</th><th>Duration</th><th>Reason</th></tr></thead>
        <tbody>
          {#each events as event (event.jobEventId)}
            <tr>
              <td>{displayFormatter.rowTimestamp(event.createdAtUtc)}</td><td>{event.eventCode}</td><td>{event.executionNumber == null ? '·' : displayFormatter.number(event.executionNumber)}</td>
              <td>{event.fromStatus ?? '·'}</td><td>{event.toStatus ?? '·'}</td><td>{event.executionStatus ?? '·'}</td>
              <td>{event.durationMs != null ? displayFormatter.milliseconds(event.durationMs) : '·'}</td><td>{event.reasonMessage ?? event.reasonCode ?? '·'}</td>
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
