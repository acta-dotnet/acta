// Pure derivations behind the calendar detail page: which actions the shown version offers, what a
// publication would meet, and the wording of the three catalog verbs' outcomes. The server stays
// authoritative on legality; this only decides what to show.

export interface FamilyLike {
  currentVersion: number | null;
  latestVersion: number;
  shownVersion: number;
  shownStatus: string;
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
  const { latestVersion, shownStatus } = family;
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

export interface PreviewTotalsLike {
  schedulesTotal: number;
  overdueTotal: number;
}

/** What publishing would meet, from the preview's totals since its list is capped: the schedules it retimes and the ones holding an overdue occurrence. */
export function publishImpact(preview: PreviewTotalsLike | null | undefined): { retimed: number; overdue: number } {
  return { retimed: preview?.schedulesTotal ?? 0, overdue: preview?.overdueTotal ?? 0 };
}

export type CalendarVerb = 'draft' | 'publish' | 'archive';

/** Whether a catalog verb left the family as asked: applied, or already so (a draft the latest version holds, a publication of the current version). */
export function calendarVerbSucceeded(action: string): boolean {
  return action === 'applied' || action === 'already-in-state';
}

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
  if (action === 'already-in-state') {
    if (verb === 'draft') return `Version ${detail.version} already holds this definition; nothing was saved.`;
    return `Version ${detail.version} is already current; nothing was retimed.`;
  }
  if (action === 'version-conflict') return 'The calendar changed since you loaded it (version conflict). Reload and retry.';
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
