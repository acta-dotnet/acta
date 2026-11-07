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

export interface PreviewSchedulesLike {
  schedules: { status: string }[];
  schedulesTotal: number;
}

export interface PreviewImpactLike extends PreviewSchedulesLike {
  overdueTotal: number;
}

/**
 * What publishing would meet: at most the bound schedules it retimes, and the ones holding an overdue occurrence. A publication
 * skips an orphaned schedule, and one whose other calendar resolves to nothing, which the preview cannot tell, so the retimed
 * count is a ceiling; the schedules past the preview's cap count toward it.
 */
export function publishImpact(preview: PreviewImpactLike | null | undefined): { retimed: number; overdue: number } {
  if (!preview) return { retimed: 0, overdue: 0 };
  const live = preview.schedules.filter((schedule) => schedule.status !== 'orphaned').length;
  return { retimed: live + preview.schedulesTotal - preview.schedules.length, overdue: preview.overdueTotal };
}

/** The schedules a publication retimes, as the line beside its button; null until the preview has counted them. */
export function publishImpactText(preview: PreviewImpactLike | null | undefined): string | null {
  if (!preview) return null;
  const { retimed } = publishImpact(preview);
  if (retimed === 0) return 'No live schedule binds this calendar, so publishing retimes nothing.';
  return `Retimes up to ${retimed} bound ${retimed === 1 ? 'schedule' : 'schedules'} to this version.`;
}

/**
 * The schedules that refuse an archive, from the preview's bound schedules, which leave out a retired definition's: every
 * one not orphaned. The preview lists at most its cap, so past it the count is a floor, and a floor of 0 leaves the refusal
 * to the server. Null until the preview loads.
 */
export function archiveBlockers(preview: PreviewSchedulesLike | null | undefined): { count: number; atLeast: boolean } | null {
  if (!preview) return null;
  return {
    count: preview.schedules.filter((schedule) => schedule.status !== 'orphaned').length,
    atLeast: preview.schedulesTotal > preview.schedules.length
  };
}

export function archiveBlockedText(blockers: { count: number; atLeast: boolean }): string {
  const count = blockers.count === 1 && !blockers.atLeast ? '1 schedule binds' : `${blockers.atLeast ? 'At least ' : ''}${blockers.count} schedules bind`;
  return `${count} this calendar.`;
}

export interface CalendarRowLike {
  currentVersion: number | null;
  latestVersion: number;
  latestStatus: string;
  dependentCount: number;
}

/**
 * What the list flags on a family's row: the rail's class, and the words its tooltip and a screen reader give it. A family
 * that schedules bind but that has no current version is an error; a draft waiting to be published is pending.
 */
export function calendarRowState(row: CalendarRowLike): { rail: '' | 'due' | 'trouble'; hint: string | null } {
  if (row.currentVersion === null && row.dependentCount > 0) {
    const bound = row.dependentCount === 1 ? '1 bound schedule reports' : `${row.dependentCount} bound schedules report`;
    return {
      rail: 'trouble',
      hint: `No current version: ${bound} evaluation errors, and a deploy that binds this calendar is refused. Publish a version to clear both.`
    };
  }
  if (row.latestStatus === 'draft') {
    return {
      rail: 'due',
      hint:
        row.currentVersion === null
          ? `Draft v${row.latestVersion} waits to be published; no version is current yet.`
          : `Draft v${row.latestVersion} is newer than current v${row.currentVersion} and waits to be published.`
    };
  }
  return { rail: '', hint: null };
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
    return `Calendar archived; version ${detail.version} is retired and no version is current.`;
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
