import { navigationGroups, navigationHref } from '../routes.ts';

// The palette's input parser. One free-text box accepts pasted identifiers and typed fragments;
// the shape decides the action. Reserved prefixes (id:, corr:, key:) win over the generic tag
// token so `id:42` never reads as a tag named "id".

export type Recognition =
  | { kind: 'jobRef'; ref: string }
  | { kind: 'workerRef'; ref: string }
  | { kind: 'alertRef'; ref: string }
  | { kind: 'jobId'; id: string }
  | { kind: 'correlation'; key: string }
  | { kind: 'dedupKey'; key: string }
  | { kind: 'scope'; name: string }
  | { kind: 'tag'; token: string }
  | { kind: 'text'; folded: string; raw: string };

// Type tag + 26-char Crockford ULID, same shape JobRef renders. One regex per entity so a pasted
// ref routes to its own detail screen rather than being probed as a name fragment.
const JOB_REF = /^job_[0-9a-hjkmnp-tv-z]{26}$/i;
const WORKER_REF = /^wrk_[0-9a-hjkmnp-tv-z]{26}$/i;
const ALERT_REF = /^alr_[0-9a-hjkmnp-tv-z]{26}$/i;
const JOB_ID = /^(?:id:|#)(\d+)$/i;
// One token, name:value, no whitespace; the server canonicalizes tag names.
const TAG_TOKEN = /^[^\s:]+:[^\s:]+$/;

export function parseQuery(input: string): Recognition | null {
  const value = input.trim();
  if (!value) return null;
  if (JOB_REF.test(value)) return { kind: 'jobRef', ref: value.toLowerCase() };
  if (WORKER_REF.test(value)) return { kind: 'workerRef', ref: value.toLowerCase() };
  if (ALERT_REF.test(value)) return { kind: 'alertRef', ref: value.toLowerCase() };
  const id = value.match(JOB_ID);
  if (id) return { kind: 'jobId', id: id[1] };
  const lower = value.toLowerCase();
  if (lower.startsWith('corr:') && value.length > 5) return { kind: 'correlation', key: value.slice(5) };
  if (lower.startsWith('key:') && value.length > 4) return { kind: 'dedupKey', key: value.slice(4) };
  // Namespace names are lowercase by convention, so the scope target folds.
  if (lower.startsWith('ns:') && value.length > 3) return { kind: 'scope', name: lower.slice(3) };
  // tag: makes bare tag names (no value) reachable; without a prefix they read as name fragments.
  if (lower.startsWith('tag:') && value.length > 4) return { kind: 'tag', token: value.slice(4) };
  if (TAG_TOKEN.test(value)) return { kind: 'tag', token: value };
  // Acta-owned names (definitions, namespaces, schedules) are lowercase by convention, so the
  // name-domain probe folds; the raw form is kept for case-preserved domains (tenant search).
  return { kind: 'text', folded: lower, raw: value };
}

export interface PageHit {
  name: string;
  label: string;
  icon?: string;
  href: string;
}

// Sidebar pages (including the recurring/history aliases) as palette commands.
export function matchPages(folded: string, scope: string | null): PageHit[] {
  const hits: PageHit[] = [];
  for (const group of navigationGroups) {
    for (const route of group.routes) {
      if (!route.label.toLowerCase().includes(folded)) continue;
      hits.push({ name: route.name, label: route.label, icon: route.icon, href: navigationHref(route, scope) });
    }
  }
  return hits;
}

export interface RecentItem {
  href: string;
  label: string;
  icon?: string;
  at: number;
}

const RECENTS_KEY = 'acta-recents-v1';
const RECENTS_CAP = 8;

function safeGet(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function safeSet(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Recents just reset next session when storage is unavailable.
  }
}

export function loadRecents(): RecentItem[] {
  const raw = safeGet(RECENTS_KEY);
  if (!raw) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    if (
      typeof parsed === 'object'
      && parsed !== null
      && 'version' in parsed
      && parsed.version === 1
      && 'items' in parsed
      && Array.isArray(parsed.items)
    ) {
      return parsed.items.filter(
        (item): item is RecentItem =>
          typeof item === 'object'
          && item !== null
          && typeof (item as RecentItem).href === 'string'
          && typeof (item as RecentItem).label === 'string'
          && typeof (item as RecentItem).at === 'number'
      );
    }
  } catch {
    // Corrupt payload: fall through to empty.
  }
  return [];
}

export function pushRecent(item: Omit<RecentItem, 'at'>, at: number): RecentItem[] {
  const next = [{ ...item, at }, ...loadRecents().filter((existing) => existing.href !== item.href)].slice(0, RECENTS_CAP);
  safeSet(RECENTS_KEY, JSON.stringify({ version: 1, items: next }));
  return next;
}
