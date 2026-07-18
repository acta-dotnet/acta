// Cursor stack for keyset prev/next paging: the top of the stack is the current page's cursor
// (null = first page). Pure and immutable so it is node-testable without a component harness and
// drops straight into $state reassignment.
export type CursorStack = readonly (string | null)[];

export const first: CursorStack = [null];

export type CountMode = 'none' | 'on-demand' | 'always';

export interface PagingState {
  filterKey: string;
  stack: CursorStack;
  countRequested: boolean;
}

export function createPagingState(filterKey: string): PagingState {
  return { filterKey, stack: first, countRequested: false };
}

export function pagingFor(state: PagingState, filterKey: string): PagingState {
  return state.filterKey === filterKey ? state : createPagingState(filterKey);
}

export function nextPaging(state: PagingState, filterKey: string, nextCursor: string | null): PagingState {
  const active = pagingFor(state, filterKey);
  return { ...active, stack: push(active.stack, nextCursor) };
}

export function previousPaging(state: PagingState, filterKey: string): PagingState {
  const active = pagingFor(state, filterKey);
  return { ...active, stack: pop(active.stack) };
}

export function exactCountPaging(state: PagingState, filterKey: string): PagingState {
  return { ...pagingFor(state, filterKey), countRequested: true };
}

export function shouldIncludeTotal(mode: CountMode, requested: boolean, legacyIncludeTotal = false): boolean {
  return legacyIncludeTotal || mode === 'always' || (mode === 'on-demand' && requested);
}

export function current(stack: CursorStack): string | null {
  return stack[stack.length - 1];
}

export function canPrev(stack: CursorStack): boolean {
  return stack.length > 1;
}

export function push(stack: CursorStack, nextCursor: string | null): CursorStack {
  return nextCursor ? [...stack, nextCursor] : stack;
}

export function pop(stack: CursorStack): CursorStack {
  return stack.length > 1 ? stack.slice(0, -1) : stack;
}

// What a pager-mode grid renders, decided from the RAW server page (rawCount), not the post-rowFilter
// array (filteredCount). A client-side rowFilter can empty the visible rows while the raw server page
// still reports hasMore, so the pager must stay reachable off the raw page - otherwise a sparse bucket
// (e.g. "resolved" alerts, a minority on any recent-first page) dead-ends on a false empty state with
// no way to advance to a later page that does hold matching rows.
// - loading/error, or a genuinely empty raw page: show only the StateView (no pager).
// - raw page has rows but the filter hid them all: show the StateView's empty text AND the pager, so
//   Next can page forward to where matches live.
// - filter kept rows: show the table and the pager.
export interface GridDisplay {
  showState: boolean;
  showTable: boolean;
  showPager: boolean;
}

// `pending`/`error` decide the state card's CONTENT (loading vs error vs empty) only when there is
// nothing cached to show. Whenever rows are present - a background refetch or a failed poll over
// placeholder data - the table stays up and the freshness indicator carries the updating/failed
// state, so a populated grid is never replaced by a spinner or a full error card.
export function gridDisplay(pending: boolean, error: boolean, rawCount: number, filteredCount: number): GridDisplay {
  if (rawCount === 0) return { showState: true, showTable: false, showPager: false };
  if (filteredCount === 0) return { showState: true, showTable: false, showPager: true };
  return { showState: false, showTable: true, showPager: true };
}
