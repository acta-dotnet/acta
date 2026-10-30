// Pure derivations behind the calendar detail page: which actions the shown version offers, what a
// publication would meet, and the wording of the three catalog verbs' outcomes. The server stays
// authoritative on legality; this only decides what to show.

export interface FamilyVersionLike {
  version: number;
  status: string;
}

export interface FamilyLike {
  currentVersion: number | null;
  versions: readonly FamilyVersionLike[];
  shownVersion: number;
}

export interface FamilyState {
  latestVersion: number;
  shownStatus: string;
  shownIsDraft: boolean;
  shownIsCurrent: boolean;
  canPublish: boolean;
  canArchive: boolean;
}

export function familyState(family: FamilyLike, controls: boolean): FamilyState {
  const latestVersion = family.versions.reduce((max, candidate) => Math.max(max, candidate.version), 0);
  const shown = family.versions.find((candidate) => candidate.version === family.shownVersion);
  const shownStatus = shown?.status ?? 'unknown';
  const shownIsDraft = shownStatus === 'draft';
  const shownIsCurrent = family.currentVersion !== null && family.shownVersion === family.currentVersion;
  return {
    latestVersion,
    shownStatus,
    shownIsDraft,
    shownIsCurrent,
    canPublish: controls && shownIsDraft,
    // Archiving retires the current version, whichever version the page shows.
    canArchive: controls && family.currentVersion !== null
  };
}

export interface PreviewScheduleLike {
  overdue: boolean;
}

/** What publishing would meet: the schedules it retimes and the ones holding an overdue occurrence. */
export function publishImpact(schedules: readonly PreviewScheduleLike[] | null | undefined): { retimed: number; overdue: number } {
  const list = schedules ?? [];
  return { retimed: list.length, overdue: list.filter((schedule) => schedule.overdue).length };
}

export type CalendarVerb = 'draft' | 'publish' | 'archive';

// Catalog verbs at calendars/{ns}/{name}/{drafts|publish|archive}. Every one is version-guarded, so a
// version conflict means the family changed since the page loaded it.
export function calendarControlMessage(
  verb: CalendarVerb,
  action: string,
  detail: { version?: number | null; retimed?: number; dependents?: number; reason?: string | null } = {}
): string {
  if (action === 'applied') {
    if (verb === 'draft') return `Draft saved as version ${detail.version}.`;
    if (verb === 'publish') return `Version ${detail.version} published; ${plural(detail.retimed ?? 0, 'schedule')} retimed.`;
    return `Version ${detail.version} archived; the family has no current version.`;
  }
  if (action === 'versionConflict') return 'The calendar changed since you loaded it (version conflict). Reload and retry.';
  if (action === 'rejected') {
    if (verb === 'draft') return 'Draft rejected: the definition has problems.';
    if (verb === 'publish') return `Publish rejected: ${detail.reason ?? 'the version is not a draft.'}`;
    return `Archive rejected: ${plural(detail.dependents ?? 0, 'live schedule')} still bind this calendar.`;
  }
  return verb === 'publish' ? "That version is not one of this calendar's." : 'Calendar not found.';
}

function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? '' : 's'}`;
}
