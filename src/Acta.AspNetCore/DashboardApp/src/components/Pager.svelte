<script>
  import Icon from './Icon.svelte';
  import { displayFormatter } from '../format';
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
      <option value="25">{displayFormatter.number(25)}</option>
      <option value="50">{displayFormatter.number(50)}</option>
      <option value="100">{displayFormatter.number(100)}</option>
      <option value="200">{displayFormatter.number(200)}</option>
      <option value="500">{displayFormatter.number(500)}</option>
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
