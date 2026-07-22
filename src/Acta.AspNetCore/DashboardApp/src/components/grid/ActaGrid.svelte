<script lang="ts" generics="T">
  import { get } from 'svelte/store';
  import { createQuery, createInfiniteQuery, useQueryClient } from '@tanstack/svelte-query';
  import { createVirtualizer } from '@tanstack/svelte-virtual';
  import { api, type Paged } from '../../api';
  import { keys, listPage } from '../../query';
  import {
    current,
    canPrev,
    gridDisplay,
    shouldIncludeTotal,
    createPagingState,
    pagingFor,
    nextPaging,
    previousPaging,
    exactCountPaging,
    type CountMode,
    type PagingState
  } from './paging';
  import type { CellMap, ColumnDef } from './types';
  import { renderedVirtualRows } from './virtualRows';
  import { livePaused, listRefetchInterval } from '../../polling';
  import { appearance, textSizeRowHeight } from '../../theme/appearance';
  import { displayFormatter } from '../../format';
  import Pager from '../Pager.svelte';
  import StateView from '../StateView.svelte';
  import FreshnessIndicator from '../FreshnessIndicator.svelte';

  interface Props<T> {
    endpoint: string;
    columns: ColumnDef<T>[];
    /** Reactive accessor: re-evaluated when the state it reads changes; any change resets paging. */
    filters?: () => Record<string, unknown>;
    mode?: 'pager' | 'feed';
    initialPageSize?: number;
    includeTotal?: boolean;
    /** Exact-count policy. High-volume pages use on-demand; small catalog pages may use always. */
    countMode?: CountMode;
    loadingText?: string;
    emptyText?: string;
    cells?: CellMap<T>;
    rowKey: (row: T) => string | number;
    rowClass?: (row: T) => string;
    /** Client-side row filter applied on top of whatever the server fetched (e.g. a UI-only bucket
     *  the server has no matching query param for). The empty-state and pager visibility are decided
     *  from the RAW server page, not the filtered array (see gridDisplay in paging.ts): a page whose
     *  rows the filter all hides still shows the Next pager (driven by the raw page's hasMore) so the
     *  operator can advance to a later page that holds matching rows, instead of dead-ending on a
     *  false empty state. Paging counts (hasMore/totalCount) reflect the unfiltered server page. */
    rowFilter?: (row: T) => boolean;
    onPageSizeChange?: (size: number) => void;
    /** Feed mode: reports the new-rows count so the page can render the pill itself (e.g. in the
     *  page header, where its appearance causes no layout shift); suppresses the built-in pill. */
    onNewCount?: (count: number, label: string) => void;
    /** Render priority fields as stacked cards at the mobile breakpoint. */
    mobileCards?: boolean;
    tableLabel?: string;
  }

  let {
    endpoint,
    columns,
    filters = () => ({}),
    mode = 'pager',
    initialPageSize = 50,
    includeTotal = false,
    countMode = 'none',
    loadingText = 'Loading...',
    emptyText = 'Nothing here.',
    cells = {},
    rowKey,
    rowClass,
    rowFilter,
    onPageSizeChange,
    onNewCount,
    mobileCards = false,
    tableLabel = `${endpoint} results`
  }: Props<T> = $props();

  // mode and initialPageSize are read once by design: a grid instance never changes mode after
  // mount, and pageSize reseeds only through setPageSize.
  // svelte-ignore state_referenced_locally
  const isFeed = mode === 'feed';
  // svelte-ignore state_referenced_locally
  let pageSize = $state(initialPageSize);

  const queryClient = useQueryClient();
  let ROW_HEIGHT = $derived(textSizeRowHeight($appearance.textSize));
  const VIRTUAL_THRESHOLD = 100; // small pages render whole; spacer churn is not worth it below this
  let paging: PagingState = $state(createPagingState(''));
  let scrollEl: HTMLDivElement | null = $state(null);

  const requestFilters = $derived(filters());
  const filterKey = $derived(JSON.stringify(requestFilters));
  const activePaging = $derived(pagingFor(paging, filterKey));
  const effectiveIncludeTotal = $derived(shouldIncludeTotal(countMode, activePaging.countRequested, includeTotal));

  // Pager mode: one plain query per page; prev pages come straight from the cache.
  const page =
    !isFeed
      ? createQuery(() => ({
          ...listPage<T>(endpoint, requestFilters, {
            pageSize,
            cursor: current(activePaging.stack),
            includeTotal: effectiveIncludeTotal
          }),
          // Read $livePaused inside the reactive options so toggling pause takes effect at once.
          refetchInterval: $livePaused ? false : listRefetchInterval
        }))
      : null;

  // Feed mode: history accumulates downward and is never interval-refetched; only the head query
  // (the plain first page, sharing its cache entry with pager readers) polls for newer rows.
  const feed =
    isFeed
      ? createInfiniteQuery(() => ({
          queryKey: keys.feed(endpoint, { ...requestFilters, pageSize }),
          queryFn: ({ pageParam, signal }: { pageParam: string | null; signal: AbortSignal }) =>
            api<Paged<T>>(endpoint, { ...requestFilters, pageSize, cursor: pageParam }, { signal }),
          initialPageParam: null as string | null,
          getNextPageParam: (last: Paged<T>) => (last.hasMore ? last.nextCursor : null),
          staleTime: Infinity
        }))
      : null;
  const head = isFeed
    ? createQuery(() => ({
        ...listPage<T>(endpoint, requestFilters, { pageSize, cursor: null }),
        refetchInterval: $livePaused ? false : listRefetchInterval
      }))
    : null;

  // Raw is the unfiltered server page; items is what actually renders. gridDisplay decides the
  // empty-state/pager off rawItems so a rowFilter that hides a whole page still leaves Next reachable.
  const rawItems: T[] = $derived(!isFeed ? (page!.data?.items ?? []) : (feed!.data?.pages.flatMap((p) => p.items) ?? []));
  const items: T[] = $derived(rowFilter ? rawItems.filter((row) => rowFilter(row)) : rawItems);
  const pending = $derived(!isFeed ? page!.isPending : feed!.isPending);
  const errorMessage = $derived(!isFeed ? (page!.error?.message ?? null) : (feed!.error?.message ?? null));
  const display = $derived(gridDisplay(pending, !!errorMessage, rawItems.length, items.length));

  // The primary polled query behind the freshness indicator: the plain page, or the polling head feed.
  const freshQuery = $derived(!isFeed ? page! : head!);

  // Feed: how many head rows are newer than the accumulated top row ('N+' when the whole head page is).
  const newCount = $derived.by(() => {
    if (!isFeed || !head?.data || items.length === 0) return 0;
    const topId = rowKey(items[0]);
    const index = head.data.items.findIndex((row) => rowKey(row) === topId);
    return index < 0 ? head.data.items.length : index;
  });
  const newLabel = $derived(
    head?.data && newCount === head.data.items.length && head.data.hasMore ? displayFormatter.number(newCount) + '+' : displayFormatter.number(newCount)
  );

  // Bootstrap an empty feed: with staleTime Infinity the accumulated pages never refetch on their
  // own, so when the polling head sees rows while the feed has none, restart from the fresh head.
  $effect(() => {
    if (isFeed && !pending && items.length === 0 && (head?.data?.items.length ?? 0) > 0) jumpToTop();
  });

  $effect(() => {
    onNewCount?.(newCount, newLabel);
  });

  export function refresh(): void {
    if (!isFeed) page!.refetch();
    else jumpToTop(); // "give me latest" on a feed means restart from the fresh head, not refetch history
  }

  function nextPage(): void {
    const data = page!.data;
    if (data?.hasMore && data.nextCursor) paging = nextPaging(paging, filterKey, data.nextCursor);
  }
  function prevPage(): void {
    paging = previousPaging(paging, filterKey);
  }
  function setPageSize(size: number): void {
    pageSize = size;
    paging = createPagingState(filterKey);
    onPageSizeChange?.(size);
  }
  function jumpToTop(): void {
    scrollEl?.scrollTo({ top: 0 });
    // Drop accumulated history so the feed restarts from the (already fresh) first page.
    queryClient.resetQueries({ queryKey: keys.feed(endpoint, { ...requestFilters, pageSize }) });
  }
  function requestExactCount(): void {
    paging = exactCountPaging(paging, filterKey);
  }
  function onScroll(): void {
    if (!isFeed || !scrollEl || !feed) return;
    const nearBottom = scrollEl.scrollTop + scrollEl.clientHeight >= scrollEl.scrollHeight - 4 * ROW_HEIGHT;
    if (nearBottom && feed.hasNextPage && !feed.isFetchingNextPage) feed.fetchNextPage();
  }

  // Virtualize only long lists; spacer rows keep real <table> semantics for the accessibility pass.
  const virtual = $derived(items.length > VIRTUAL_THRESHOLD);
  const virtualizer = createVirtualizer<HTMLDivElement, HTMLTableRowElement>({
    count: 0,
    getScrollElement: () => scrollEl,
    estimateSize: () => ROW_HEIGHT,
    overscan: 12
  });
  $effect(() => {
    // get() reads without subscribing: setOptions triggers the store, and a tracked read would loop.
    get(virtualizer).setOptions({
      count: virtual ? items.length : 0,
      getScrollElement: () => scrollEl,
      estimateSize: () => ROW_HEIGHT,
      overscan: 12
    });
  });
  const vRows = $derived(virtual ? $virtualizer.getVirtualItems() : []);
  const safeVRows = $derived(renderedVirtualRows(vRows, items, rowKey));
  const padTop = $derived(safeVRows.length > 0 ? safeVRows[0].virtualRow.start : 0);
  const padBottom = $derived(
    safeVRows.length > 0 ? Math.max(0, $virtualizer.getTotalSize() - safeVRows[safeVRows.length - 1].virtualRow.end) : 0
  );
</script>

{#snippet row(item: T, prev: T | null)}
  <tr class={rowClass?.(item) ?? ''}>
    {#each columns as col}
      {@const cell = cells[String(col.key)]}
      {@const repeat = col.dimRepeats && prev !== null &&
        (item as Record<string, unknown>)[String(col.key)] === (prev as Record<string, unknown>)[String(col.key)]}
      <td class="{col.class ?? ''}{col.align === 'right' ? ' col-num' : ''}{repeat ? ' cell-repeat' : ''}" data-label={col.header}>
        {#if cell}{@render cell(item)}{:else}{(item as Record<string, unknown>)[String(col.key)] ?? '·'}{/if}
      </td>
    {/each}
  </tr>
{/snippet}

<div class="grid-freshness">
  <FreshnessIndicator
    dataUpdatedAt={freshQuery.dataUpdatedAt}
    isFetching={freshQuery.isFetching}
    isError={!!freshQuery.error}
    onRefresh={() => refresh()} />
</div>

{#if display.showState}
  <StateView loading={pending} error={errorMessage} {loadingText} {emptyText} onRetry={() => refresh()} />
{/if}
{#if display.showTable}
  {#if isFeed && newCount > 0 && !onNewCount}
    <button class="chip feed-new" onclick={jumpToTop}>{newLabel} new: jump to top</button>
  {/if}
  <div class="table-wrap" bind:this={scrollEl} onscroll={onScroll}>
    <table class="data" class:mobile-cards={mobileCards}>
      <caption class="sr-only">{tableLabel}</caption>
      <thead>
        <tr>
          {#each columns as col}
            <th class="{col.class ?? ''}{col.align === 'right' ? ' col-num' : ''}">{col.header}</th>
          {/each}
        </tr>
      </thead>
      <tbody>
        {#if virtual}
          {#if padTop > 0}<tr aria-hidden="true"><td colspan={columns.length} style="height: {padTop}px; padding: 0; border: none;"></td></tr>{/if}
          {#each safeVRows as entry (entry.key)}
            {@render row(entry.item, items[entry.virtualRow.index - 1] ?? null)}
          {/each}
          {#if padBottom > 0}<tr aria-hidden="true"><td colspan={columns.length} style="height: {padBottom}px; padding: 0; border: none;"></td></tr>{/if}
        {:else}
          {#each items as item, i (rowKey(item))}
            {@render row(item, items[i - 1] ?? null)}
          {/each}
        {/if}
      </tbody>
    </table>
  </div>
{/if}
{#if !isFeed && display.showPager}
  <Pager
    canPrev={canPrev(activePaging.stack)}
    hasMore={page!.data?.hasMore ?? false}
    {pageSize}
    totalCount={page!.data?.totalCount ?? null}
    visibleCount={rawItems.length}
    firstPage={!canPrev(activePaging.stack)}
    hasExactCountAction={countMode === 'on-demand' && !effectiveIncludeTotal}
    countLoading={countMode === 'on-demand' && effectiveIncludeTotal && page!.data?.totalCount == null && page!.isFetching}
    onPrev={prevPage}
    onNext={nextPage}
    onExactCount={requestExactCount}
    onPageSize={setPageSize} />
{:else if isFeed && display.showTable}
  {#if feed?.isFetchingNextPage}
    <div class="state">Loading more...</div>
  {:else if !feed?.hasNextPage && items.length > 0}
    <div class="state dim">· end of results ·</div>
  {/if}
{/if}

<style>
  /* Freshness strip sits top-right above the grid so "is this current?" is answerable at a glance
     without competing with the page heading. */
  .grid-freshness {
    display: flex;
    justify-content: flex-end;
    margin-bottom: 8px;
  }
</style>
