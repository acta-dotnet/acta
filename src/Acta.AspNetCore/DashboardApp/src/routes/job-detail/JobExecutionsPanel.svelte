<script lang="ts">
  import { tick } from 'svelte';
  import { createInfiniteQuery, createQuery } from '@tanstack/svelte-query';
  import { api, type Paged } from '../../api.ts';
  import { displayFormatter } from '../../format.ts';
  import { keys, listPage } from '../../query.ts';
  import { livePaused, listRefetchInterval } from '../../polling.ts';
  import DataTable from '../../components/DataTable.svelte';
  import Icon from '../../components/Icon.svelte';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import StateView from '../../components/StateView.svelte';
  import { routes } from '../../routes.ts';
  import type { JobEvent, JobDetail } from './types.ts';
  import { mergeJobEvents } from './jobDetailState.ts';
  import { deriveExecutions, executionGapSummary, executionPresentation, type ExecutionSummary } from './executionsState.ts';

  let {
    jobRef,
    job,
    polling = true,
    focusExecution = null,
    onViewInTimeline = () => {}
  }: {
    jobRef: string;
    job: JobDetail;
    polling?: boolean;
    focusExecution?: number | null;
    onViewInTimeline?: (executionNumber: number) => void;
  } = $props();

  // Same endpoint, page size, and query keys as JobEventsPanel: the tanstack cache is shared, so
  // flipping between Details and Executions costs no extra requests.
  // svelte-ignore state_referenced_locally
  const endpoint = `jobs/${jobRef}/events`;
  const history = createInfiniteQuery(() => ({
    queryKey: keys.feed(endpoint, { pageSize: 100 }),
    queryFn: ({ pageParam, signal }: { pageParam: string | null; signal: AbortSignal }) =>
      api<Paged<JobEvent>>(endpoint, { pageSize: 100, cursor: pageParam }, { signal }),
    initialPageParam: null as string | null,
    getNextPageParam: (last: Paged<JobEvent>) => (last.hasMore ? last.nextCursor : null),
    staleTime: Infinity
  }));
  const head = createQuery(() => ({
    ...listPage<JobEvent>(endpoint, {}, { pageSize: 100, cursor: null }),
    refetchInterval: polling && !$livePaused ? listRefetchInterval : false
  }));

  let historyPages = $derived(history.data?.pages.map((page) => page.items) ?? []);
  let events: JobEvent[] = $derived(mergeJobEvents(head.data?.items ?? [], historyPages));
  let executions = $derived(deriveExecutions(events));
  let gap = $derived(executionGapSummary(job.executionNumber, executions));

  // Deep link (?execution=N): scroll to the row once present, paging older history until the
  // target appears or the history is exhausted.
  let focused = $state(false);
  $effect(() => {
    if (focused || focusExecution == null) return;
    if (executions.some((execution) => execution.executionNumber === focusExecution)) {
      focused = true;
      void tick().then(() => document.getElementById('execution-' + focusExecution)?.scrollIntoView({ block: 'center' }));
      return;
    }
    if (!history.isPending && history.hasNextPage && !history.isFetchingNextPage) {
      void history.fetchNextPage();
    }
  });

  export function refresh(): void {
    void head.refetch();
  }

  function railClass(execution: ExecutionSummary): string {
    switch (execution.outcome) {
      case 'failed':
      case 'cancelled':
        return 'trouble';
      case 'orphaned':
        return 'stale';
      case 'paused':
      case 'suspended':
        return 'held';
      default:
        return '';
    }
  }
</script>

<section class="detail-panel" aria-label="Executions">
  <h2>Executions</h2>
  <p class="detail-help">
    One row per handler invocation, derived from the event ledger; open a row to walk its events in
    the timeline. Showing {displayFormatter.number(gap.shown)} of {displayFormatter.number(gap.total)} executions.
  </p>

  {#if executions.length === 0}
    <StateView
      loading={history.isPending && head.isPending}
      error={head.error?.message ?? history.error?.message ?? null}
      emptyText={gap.total === 0
        ? 'This job has not been claimed yet.'
        : history.hasNextPage
          ? 'No execution events in the loaded history yet; load older history below.'
          : 'No execution events retained for this job (audit level or event retention).'}
      onRetry={() => head.refetch()} />
  {:else}
    <DataTable>
      <caption class="sr-only">Executions</caption>
      <thead>
        <tr><th>Execution</th><th>Outcome</th><th>Started</th><th class="col-num">Duration</th><th>Worker</th><th>Reason</th><th class="col-open"></th></tr>
      </thead>
      <tbody>
        {#each executions as execution, index (execution.executionNumber)}
          {@const look = executionPresentation(execution.outcome)}
          {@const previous = executions[index + 1]}
          {@const previousSettled = previous?.finishedAtUtc ?? previous?.startedAtUtc}
          {@const retryGapSeconds = execution.startedAtUtc && previousSettled
            ? Math.round((Date.parse(execution.startedAtUtc) - Date.parse(previousSettled)) / 1000)
            : null}
          {@const repeatReason = !!execution.reasonMessage && execution.reasonMessage === previous?.reasonMessage}
          <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_noninteractive_element_interactions -->
          <tr
            class="{railClass(execution)} row-walk"
            id={'execution-' + execution.executionNumber}
            onclick={(event) => {
              if (event.target instanceof HTMLElement && event.target.closest('a, button')) return;
              onViewInTimeline(execution.executionNumber);
            }}>
            <td class="mono">#{execution.executionNumber}</td>
            <td><span class="badge {look.tone === 'run' ? 'run' : look.tone === 'neutral' ? '' : look.tone}"><Icon name={look.icon} />{look.label}</span></td>
            <td>
              <RelativeTime value={execution.startedAtUtc} />
              {#if retryGapSeconds != null && retryGapSeconds > 0}<span class="dim retry-gap">+{displayFormatter.duration(retryGapSeconds)}</span>{/if}
            </td>
            <td class="col-num">{execution.durationMs != null ? (execution.durationMs === 0 ? '<1 ms' : displayFormatter.milliseconds(execution.durationMs)) : '·'}</td>
            <td>
              {#if execution.workerId != null}
                <a class="mono" href={routes.worker(execution.workerId, { namespace: job.jobNamespace })}>worker-{execution.workerId}</a>
              {:else}<span class="dim">·</span>{/if}
            </td>
            <td class="wrap" class:cell-repeat={repeatReason}>
              {#if execution.reasonCode}<span class="mono dim">{execution.reasonCode}</span>{/if}
              {#if repeatReason}<span class="dim">same as below</span>{:else}{execution.reasonMessage ?? (execution.reasonCode ? '' : '·')}{/if}
            </td>
            <td class="col-open">
              <button
                class="row-open"
                title={'Walk execution ' + execution.executionNumber + ' in the timeline'}
                aria-label={'Walk execution ' + execution.executionNumber + ' in the timeline'}
                onclick={() => onViewInTimeline(execution.executionNumber)}>
                <Icon name="chevron-right" />
              </button>
            </td>
          </tr>
        {/each}
        {#if gap.missing > 0 && !history.hasNextPage}
          <!-- Only once the full history is loaded may the gap be blamed on retention: before
               that, missing executions are usually just on unloaded pages. -->
          <tr>
            <td colspan="7" class="dim wrap">
              {displayFormatter.number(gap.missing)} earlier executions have no retained events (audit level or event retention).
            </td>
          </tr>
        {/if}
      </tbody>
    </DataTable>
  {/if}
  {#if history.hasNextPage}
    <div class="timeline-more">
      <button onclick={() => history.fetchNextPage()} disabled={history.isFetchingNextPage}>
        {history.isFetchingNextPage ? 'Loading...' : 'Load older executions'}
      </button>
    </div>
  {/if}
</section>

<style>
  /* Buttoned twin of the a.icon-action drill-in chevron the list pages use. */
  .row-open {
    display: inline-flex; align-items: center; justify-content: center;
    padding: 5px 7px; border: 1px solid transparent; border-radius: var(--radius-control);
    background: none; color: var(--muted); cursor: pointer;
  }
  .row-open:hover:not(:disabled) { border-color: var(--accent); color: var(--accent); }
  .row-walk { cursor: pointer; }
  .retry-gap { margin-left: 6px; font-size: var(--text-xs); }
</style>
