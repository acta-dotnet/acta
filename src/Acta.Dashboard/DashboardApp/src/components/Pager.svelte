<script>
  import Icon from './Icon.svelte';
  import { displayFormatter } from '../format';
  import { PAGE_SIZES } from './grid/paging';
  let {
    canPrev = false,
    hasMore = false,
    pageSize = 25,
    totalCount = null,
    visibleCount = 0,
    firstPage = true,
    hasExactCountAction = false,
    countLoading = false,
    onPrev = () => {},
    onNext = () => {},
    onExactCount = () => {},
    onPageSize = (size) => {}
  } = $props();
</script>

<div class="pager">
  <button class="iconly" disabled={!canPrev} onclick={() => onPrev()} title="Previous page" aria-label="Previous page"><Icon name="chevron-left" /></button>
  <button class="iconly" disabled={!hasMore} onclick={() => onNext()} title="Next page" aria-label="Next page"><Icon name="chevron-right" /></button>
  <label>
    Page size
    <select value={String(pageSize)} onchange={(e) => onPageSize(Number(e.currentTarget.value))}>
      {#each PAGE_SIZES as size}
        <option value={String(size)}>{displayFormatter.number(size)}</option>
      {/each}
    </select>
  </label>
  {#if totalCount !== null}
    <span class="total">{displayFormatter.number(totalCount)} total</span>
  {:else}
    <span class="total">
      {firstPage ? 'Showing first ' + displayFormatter.number(visibleCount) : 'Showing ' + displayFormatter.number(visibleCount) + ' on this page'}{hasMore ? ' · more available' : ''}
    </span>
    {#if hasExactCountAction}
      <button onclick={() => onExactCount()}>Calculate exact count</button>
    {:else if countLoading}
      <span class="dim" role="status">Calculating exact count...</span>
    {/if}
  {/if}
</div>
