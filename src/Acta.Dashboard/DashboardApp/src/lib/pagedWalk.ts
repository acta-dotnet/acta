// Reads every page of a keyset list through one fetch, following the cursor to the last page. A page that
// claims more without a cursor, or hands back the cursor it was asked with, is a walk that would never end,
// so it is an error, and the bound stops a misbehaving server at 100 pages. The caller's signal rides on
// every request through the fetch it passes. The answer is the whole list or an error, never a partial
// catalog presented as complete. Pure, so node --test runs it with a scripted fetch.
import type { Paged } from '../api.ts';

export const PAGE_WALK_BOUND = 100;

export async function readAllPages<T>(fetchPage: (cursor: string | undefined) => Promise<Paged<T>>): Promise<T[]> {
  const items: T[] = [];
  let cursor: string | undefined;
  for (let page = 1; page <= PAGE_WALK_BOUND; page++) {
    const result = await fetchPage(cursor);
    items.push(...result.items);
    if (!result.hasMore) return items;
    if (!result.nextCursor) throw new Error(`The list claims more after page ${page} but gave no cursor.`);
    if (result.nextCursor === cursor) throw new Error(`The list repeated its cursor after page ${page}; the walk would never end.`);
    cursor = result.nextCursor;
  }
  throw new Error(`The list did not end within ${PAGE_WALK_BOUND} pages.`);
}
