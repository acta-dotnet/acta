// The weekly-availability definition as the visual editor holds it, and the pure helpers both the editor
// and the preview panel need. The wire carries a definition as JSON text; this module converts that text
// to and from a per-day model and never rebuilds a definition of another kind, so a union or an
// occurrence calendar stays in the JSON editor exactly as the operator typed it. Pure functions, so
// node --test exercises them without the Svelte runtime.
import { jsonError } from '../../lib/rawJson.ts';

export const WEEKDAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as const;
export type Weekday = (typeof WEEKDAYS)[number];

/** One local window of a day: HH:mm to HH:mm, an end before the start meaning the next date. */
export interface LocalWindow {
  from: string;
  to: string;
}

/** A range subtracted last: whole dates, or UTC instants. */
export type ExcludedRange = { from: string; to: string } | { fromUtc: string; toUtc: string };

export interface DateOverride {
  date: string;
  /** The date's windows in place of its weekday's; empty closes the date. */
  windows: LocalWindow[];
}

export interface WeeklyModel {
  timeZone: string;
  description: string;
  days: Record<Weekday, LocalWindow[]>;
  dateOverrides: DateOverride[];
  excludedRanges: ExcludedRange[];
}

const TIME = /^([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

/** Minutes since midnight for an HH:mm or HH:mm:ss time, or null when the text is not one. */
export function minutesOf(time: string): number | null {
  if (!TIME.test(time)) return null;
  const [hours, minutes] = time.split(':');
  return Number(hours) * 60 + Number(minutes);
}

/** Why a window would not compile, in the compiler's words, or null when it would. */
export function windowProblem(window: LocalWindow): string | null {
  const from = minutesOf(window.from);
  const to = minutesOf(window.to);
  if (from === null || to === null) return 'a time is HH:mm or HH:mm:ss.';
  if (from === to) return 'a window ends after it starts; an overnight window ends on the next date.';
  return null;
}

/** The window's minutes of day, or null when a time is malformed or the window is empty, which draws and counts as nothing. */
function span(window: LocalWindow): { from: number; to: number } | null {
  const from = minutesOf(window.from);
  const to = minutesOf(window.to);
  return from === null || to === null || from === to ? null : { from, to };
}

/** The window as percentages of a 24-hour bar: one span, or two when it wraps past midnight. */
export function windowSpans(window: LocalWindow): { left: number; width: number }[] {
  const s = span(window);
  if (s === null) return [];
  const pct = (minutes: number) => (minutes / 1440) * 100;
  if (s.to > s.from) return [{ left: pct(s.from), width: pct(s.to - s.from) }];
  return [
    { left: pct(s.from), width: pct(1440 - s.from) },
    { left: 0, width: pct(s.to) }
  ];
}

/** The day's open minutes, an overnight window counted on the day it starts. */
export function openMinutes(windows: LocalWindow[]): number {
  return windows.reduce((sum, window) => {
    const s = span(window);
    if (s === null) return sum;
    return sum + (s.to > s.from ? s.to - s.from : 1440 - s.from + s.to);
  }, 0);
}

function emptyDays(): Record<Weekday, LocalWindow[]> {
  return Object.fromEntries(WEEKDAYS.map((day) => [day, [] as LocalWindow[]])) as unknown as Record<Weekday, LocalWindow[]>;
}

/** A first draft worth editing: office hours on weekdays in the schedule's zone. */
export function emptyWeekly(): WeeklyModel {
  const days = emptyDays();
  for (const day of WEEKDAYS.slice(0, 5)) days[day] = [{ from: '09:00', to: '17:00' }];
  return { timeZone: 'schedule', description: '', days, dateOverrides: [], excludedRanges: [] };
}

/** The JSON a new family of this role starts from. */
export function definitionTemplate(role: string): string {
  if (role === 'availability') return weeklyToJson(emptyWeekly());
  return JSON.stringify(
    { kind: 'daily-local-time', timeZone: 'schedule', times: ['09:00'], days: [...WEEKDAYS.slice(0, 5)] },
    null,
    2
  );
}

function isWindow(value: unknown): value is [string, string] {
  return Array.isArray(value) && value.length === 2 && typeof value[0] === 'string' && typeof value[1] === 'string';
}

function windowsOf(value: unknown): LocalWindow[] | null {
  if (!Array.isArray(value)) return null;
  const windows: LocalWindow[] = [];
  for (const item of value) {
    if (!isWindow(item)) return null;
    windows.push({ from: item[0], to: item[1] });
  }
  return windows;
}

function isRange(value: unknown): value is ExcludedRange {
  if (value === null || typeof value !== 'object') return false;
  const range = value as Record<string, unknown>;
  if ('fromUtc' in range || 'toUtc' in range) return typeof range.fromUtc === 'string' && typeof range.toUtc === 'string';
  return typeof range.from === 'string' && typeof range.to === 'string';
}

// The editor model from definition text: null for text that is not JSON, not a weekly-availability, or
// shaped in a way the editor would not reproduce, which keeps the JSON editor as the one place such a
// definition is changed. Days a rule shares get the rule's windows appended, as the compiler does.
export function weeklyFromJson(text: string): WeeklyModel | null {
  if (jsonError(text) !== null) return null;
  const root: unknown = JSON.parse(text);
  if (root === null || typeof root !== 'object' || Array.isArray(root)) return null;
  const def = root as Record<string, unknown>;
  if (def.kind !== 'weekly-availability') return null;
  for (const key of Object.keys(def)) {
    if (!['kind', 'description', 'timeZone', 'weekly', 'dateOverrides', 'excludedRanges'].includes(key)) return null;
  }
  if (def.timeZone !== undefined && typeof def.timeZone !== 'string') return null;
  if (def.description !== undefined && typeof def.description !== 'string') return null;

  const days = emptyDays();
  if (def.weekly !== undefined) {
    if (!Array.isArray(def.weekly)) return null;
    for (const rule of def.weekly) {
      if (rule === null || typeof rule !== 'object') return null;
      const { days: ruleDays, windows } = rule as Record<string, unknown>;
      if (!Array.isArray(ruleDays)) return null;
      const parsed = windowsOf(windows);
      if (parsed === null) return null;
      for (const day of ruleDays) {
        if (!WEEKDAYS.includes(day as Weekday)) return null;
        days[day as Weekday] = [...days[day as Weekday], ...parsed];
      }
    }
  }

  const dateOverrides: DateOverride[] = [];
  if (def.dateOverrides !== undefined) {
    if (def.dateOverrides === null || typeof def.dateOverrides !== 'object' || Array.isArray(def.dateOverrides)) return null;
    for (const [date, windows] of Object.entries(def.dateOverrides)) {
      const parsed = windowsOf(windows);
      if (parsed === null) return null;
      dateOverrides.push({ date, windows: parsed });
    }
  }

  const excludedRanges: ExcludedRange[] = [];
  if (def.excludedRanges !== undefined) {
    if (!Array.isArray(def.excludedRanges)) return null;
    for (const range of def.excludedRanges) {
      if (!isRange(range)) return null;
      excludedRanges.push('fromUtc' in range ? { fromUtc: range.fromUtc, toUtc: range.toUtc } : { from: range.from, to: range.to });
    }
  }

  return {
    timeZone: typeof def.timeZone === 'string' ? def.timeZone : 'schedule',
    description: typeof def.description === 'string' ? def.description : '',
    days,
    dateOverrides,
    excludedRanges
  };
}

// Definition text from the model: days with the same windows share one rule, in weekday order, so the
// JSON reads the way an operator would write it (Monday to Friday, 09:00 to 17:00). Closed days are
// left out; the compiler needs no rule for them.
export function weeklyToJson(model: WeeklyModel): string {
  const rules: { days: Weekday[]; windows: [string, string][] }[] = [];
  for (const day of WEEKDAYS) {
    const windows = model.days[day];
    if (windows.length === 0) continue;
    const signature = JSON.stringify(windows.map((window) => [window.from, window.to]));
    const rule = rules.find((candidate) => JSON.stringify(candidate.windows) === signature);
    if (rule) rule.days.push(day);
    else rules.push({ days: [day], windows: windows.map((window) => [window.from, window.to]) });
  }

  const def: Record<string, unknown> = { kind: 'weekly-availability' };
  if (model.description.trim()) def.description = model.description.trim();
  def.timeZone = model.timeZone.trim() || 'schedule';
  def.weekly = rules;
  if (model.dateOverrides.length > 0) {
    def.dateOverrides = Object.fromEntries(
      model.dateOverrides.map((override) => [override.date, override.windows.map((window) => [window.from, window.to])])
    );
  }
  if (model.excludedRanges.length > 0) def.excludedRanges = model.excludedRanges;
  return JSON.stringify(def, null, 2);
}

/** The model's own problems before a save, each naming the place the compiler would. */
export function weeklyProblems(model: WeeklyModel): string[] {
  const problems: string[] = [];
  let open = 0;
  for (const day of WEEKDAYS) {
    model.days[day].forEach((window, index) => {
      const problem = windowProblem(window);
      if (problem) problems.push(`${day} window ${index + 1}: ${problem}`);
    });
    open += model.days[day].length;
  }
  model.dateOverrides.forEach((override, index) => {
    if (!DATE.test(override.date)) problems.push(`dateOverrides[${index}]: a date is yyyy-MM-dd.`);
    override.windows.forEach((window, windowIndex) => {
      const problem = windowProblem(window);
      if (problem) problems.push(`dateOverrides['${override.date}'] window ${windowIndex + 1}: ${problem}`);
    });
  });
  model.excludedRanges.forEach((range, index) => {
    if ('fromUtc' in range) {
      if (Number.isNaN(Date.parse(range.fromUtc)) || Number.isNaN(Date.parse(range.toUtc))) {
        problems.push(`excludedRanges[${index}]: an instant is yyyy-MM-ddTHH:mm:ssZ.`);
      } else if (Date.parse(range.toUtc) <= Date.parse(range.fromUtc)) {
        problems.push(`excludedRanges[${index}]: toUtc must be after fromUtc.`);
      }
    } else if (!DATE.test(range.from) || !DATE.test(range.to)) {
      problems.push(`excludedRanges[${index}]: a date is yyyy-MM-dd.`);
    } else if (range.to < range.from) {
      problems.push(`excludedRanges[${index}]: to must not be before from.`);
    }
  });
  if (open === 0 && model.dateOverrides.length === 0) problems.push('a weekly-availability needs weekly rules or date overrides.');
  return problems;
}

/** What a publication would add and remove: the candidate's entries the current lacks, and the reverse. */
export function previewDiff(candidate: readonly string[], current: readonly string[]): { added: Set<string>; removed: Set<string> } {
  const candidateSet = new Set(candidate);
  const currentSet = new Set(current);
  return {
    added: new Set(candidate.filter((entry) => !currentSet.has(entry))),
    removed: new Set(current.filter((entry) => !candidateSet.has(entry)))
  };
}

/** One comparable key per preview window, so the diff treats an open-ended window as its own entry. */
export function windowKey(window: { startUtc: string; endUtc: string | null }): string {
  return `${window.startUtc}/${window.endUtc ?? 'open'}`;
}
