const INVARIANT_LOCALE = 'en-US-u-ca-gregory-nu-latn';

export interface DisplayFormatter {
  readonly localTimeZone: string;
  number(value: number): string;
  duration(seconds: number | null | undefined): string;
  milliseconds(value: number): string;
  bytes(value: number | null | undefined): string;
  relativeTime(iso: string | null | undefined, nowMs: number): string;
  timestamp(iso: string | null | undefined): string;
  timestampInZone(iso: string | null | undefined, timeZone: string): string;
  rowTimestamp(iso: string | null | undefined): string;
  rowTimestampInZone(iso: string | null | undefined, timeZone: string): string;
  zoneNote(nowMs: number): string;
  typeName(value: string | null | undefined): string;
}

export type UtcDateTimeInputResult =
  | { ok: true; wire: string }
  | { ok: false; error: string };

const numberFormatter = new Intl.NumberFormat(INVARIANT_LOCALE, {
  maximumFractionDigits: 20,
  numberingSystem: 'latn',
  useGrouping: false
});

const oneDecimalNumberFormatter = new Intl.NumberFormat(INVARIANT_LOCALE, {
  minimumFractionDigits: 1,
  maximumFractionDigits: 1,
  numberingSystem: 'latn',
  useGrouping: false
});

const dateTimeOptions: Intl.DateTimeFormatOptions = {
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hourCycle: 'h23'
};

const dateTimeFormatters = new Map<string, Intl.DateTimeFormat>();

function resolveLocalTimeZone(): string {
  try {
    const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    if (timeZone) return timeZone;
  } catch {
    // Fall through to the universal UTC fallback.
  }
  return 'UTC';
}

const localTimeZone = resolveLocalTimeZone();

function createDateTimeFormatter(timeZone: string): Intl.DateTimeFormat {
  return new Intl.DateTimeFormat(INVARIANT_LOCALE, { ...dateTimeOptions, timeZone });
}

function dateTimeFormatter(requestedTimeZone: string): Intl.DateTimeFormat {
  const cached = dateTimeFormatters.get(requestedTimeZone);
  if (cached) return cached;

  let formatter: Intl.DateTimeFormat;
  try {
    formatter = createDateTimeFormatter(requestedTimeZone);
  } catch {
    const localFormatter = dateTimeFormatters.get(localTimeZone);
    if (localFormatter) {
      formatter = localFormatter;
    } else {
      try {
        formatter = createDateTimeFormatter(localTimeZone);
        dateTimeFormatters.set(localTimeZone, formatter);
      } catch {
        formatter = createDateTimeFormatter('UTC');
        dateTimeFormatters.set('UTC', formatter);
      }
    }
  }

  dateTimeFormatters.set(requestedTimeZone, formatter);
  return formatter;
}

function utcWireValue(iso: string): string {
  return /[zZ]$|[+-]\d\d:?\d\d$/.test(iso) ? iso : `${iso}Z`;
}

function parseUtcInstant(iso: string): Date | null {
  const date = new Date(utcWireValue(iso));
  return Number.isNaN(date.getTime()) ? null : date;
}

function datePartValues(formatter: Intl.DateTimeFormat, date: Date): Record<string, string> {
  return Object.fromEntries(formatter.formatToParts(date).map((part) => [part.type, part.value]));
}

function offsetMinutes(date: Date, parts: Record<string, string>): number {
  const wallClock = new Date(0);
  wallClock.setUTCFullYear(Number(parts.year), Number(parts.month) - 1, Number(parts.day));
  wallClock.setUTCHours(
    Number(parts.hour),
    Number(parts.minute),
    Number(parts.second),
    0
  );
  return Math.round((wallClock.getTime() - date.getTime()) / 60000);
}

function formatOffset(minutes: number): string {
  const sign = minutes < 0 ? '-' : '+';
  const absoluteMinutes = Math.abs(minutes);
  const hours = Math.floor(absoluteMinutes / 60).toString().padStart(2, '0');
  const remainder = (absoluteMinutes % 60).toString().padStart(2, '0');
  return `${sign}${hours}:${remainder}`;
}

function formatNumber(value: number): string {
  return numberFormatter.format(value);
}

function formatTimestampInZone(
  iso: string | null | undefined,
  requestedTimeZone: string
): string {
  if (!iso) return '';

  const date = parseUtcInstant(iso);
  if (!date) return 'Invalid date';

  const formatter = dateTimeFormatter(requestedTimeZone);
  const parts = datePartValues(formatter, date);
  const effectiveTimeZone = formatter.resolvedOptions().timeZone || 'UTC';
  const offset = formatOffset(offsetMinutes(date, parts));
  return `${parts.year}-${parts.month}-${parts.day} ${parts.hour}:${parts.minute}:${parts.second} ${offset} [${effectiveTimeZone}]`;
}

function formatTimestamp(iso: string | null | undefined): string {
  return formatTimestampInZone(iso, localTimeZone);
}

// Row form for repeating table cells: the full instant without the per-row offset/zone suffix.
// The zone is stated once per page via zoneNote instead.
function formatRowTimestampInZone(iso: string | null | undefined, timeZone: string): string {
  const full = formatTimestampInZone(iso, timeZone);
  return full === '' || full === 'Invalid date' ? full : full.slice(0, 19);
}

function formatRowTimestamp(iso: string | null | undefined): string {
  return formatRowTimestampInZone(iso, localTimeZone);
}

function formatZoneNote(nowMs: number): string {
  const formatter = dateTimeFormatter(localTimeZone);
  const date = new Date(nowMs);
  const offset = formatOffset(offsetMinutes(date, datePartValues(formatter, date)));
  return `Times in ${formatter.resolvedOptions().timeZone || 'UTC'} (${offset})`;
}

function formatRelativeTime(iso: string | null | undefined, nowMs: number): string {
  if (!iso) return '';

  const date = parseUtcInstant(iso);
  if (!date) return 'Invalid date';

  const diff = date.getTime() - nowMs;
  const absoluteDifference = Math.abs(diff);
  const units: [number, string][] = [
    [86400000, 'd'],
    [3600000, 'h'],
    [60000, 'm'],
    [1000, 's']
  ];
  for (const [size, label] of units) {
    if (absoluteDifference >= size) {
      const value = formatNumber(Math.floor(absoluteDifference / size));
      return diff < 0 ? `${value}${label} ago` : `in ${value}${label}`;
    }
  }
  return 'now';
}

function formatDuration(totalSeconds: number | null | undefined): string {
  if (totalSeconds == null) return '-';

  const seconds = Math.max(0, Math.floor(totalSeconds));
  if (seconds < 60) return `${formatNumber(seconds)}s`;
  if (seconds < 3600) {
    return `${formatNumber(Math.floor(seconds / 60))}m ${formatNumber(seconds % 60)}s`;
  }
  if (seconds < 86400) {
    return `${formatNumber(Math.floor(seconds / 3600))}h ${formatNumber(Math.floor((seconds % 3600) / 60))}m`;
  }
  return `${formatNumber(Math.floor(seconds / 86400))}d ${formatNumber(Math.floor((seconds % 86400) / 3600))}h`;
}

function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null) return '-';
  if (bytes < 1024) return `${formatNumber(bytes)} B`;
  if (bytes < 1048576) return `${oneDecimalNumberFormatter.format(bytes / 1024)} KB`;
  return `${oneDecimalNumberFormatter.format(bytes / 1048576)} MB`;
}

function formatMilliseconds(milliseconds: number): string {
  return `${formatNumber(milliseconds)} ms`;
}

function formatTypeName(typeName: string | null | undefined): string {
  if (!typeName) return 'none';

  const noAssembly = typeName.split(',')[0];
  const lastDot = noAssembly.lastIndexOf('.');
  return lastDot < 0 ? noAssembly : noAssembly.slice(lastDot + 1);
}

export const displayFormatter: DisplayFormatter = Object.freeze({
  localTimeZone,
  number: formatNumber,
  duration: formatDuration,
  milliseconds: formatMilliseconds,
  bytes: formatBytes,
  relativeTime: formatRelativeTime,
  timestamp: formatTimestamp,
  timestampInZone: formatTimestampInZone,
  rowTimestamp: formatRowTimestamp,
  rowTimestampInZone: formatRowTimestampInZone,
  zoneNote: formatZoneNote,
  typeName: formatTypeName
});

export function parseUtcDateTimeInput(value: string): UtcDateTimeInputResult {
  const input = value.trim();
  if (!input) return { ok: false, error: 'Enter a UTC date and time.' };

  const match = /^(\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2})(?::(\d{2}))?$/.exec(input);
  if (!match) {
    return { ok: false, error: 'Use YYYY-MM-DD HH:mm or YYYY-MM-DD HH:mm:ss.' };
  }

  const [, yearText, monthText, dayText, hourText, minuteText, secondText = '00'] = match;
  const [year, month, day, hour, minute, second] = [
    yearText,
    monthText,
    dayText,
    hourText,
    minuteText,
    secondText
  ].map(Number);
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  date.setUTCHours(hour, minute, second, 0);

  if (
    year < 1 ||
    date.getUTCFullYear() !== year ||
    date.getUTCMonth() !== month - 1 ||
    date.getUTCDate() !== day ||
    date.getUTCHours() !== hour ||
    date.getUTCMinutes() !== minute ||
    date.getUTCSeconds() !== second
  ) {
    return { ok: false, error: 'Enter a valid UTC date and time.' };
  }

  return { ok: true, wire: date.toISOString() };
}

// Code-family enums arrive kebab-case on the wire ('ready', 'retry-after', 'critical').
export function statusClass(status: string): string {
  switch (status) {
    case 'succeeded':
    case 'active':
    case 'delivered':
      return 'ok';
    case 'failed':
    case 'cancelled':
    case 'dead':
    case 'error':
    case 'critical':
      return 'bad';
    case 'executing':
    case 'dispatched':
    case 'delivering':
      return 'run';
    case 'paused':
    case 'suspended':
    case 'draining':
    case 'warning':
    case 'retry-after':
      return 'warn';
    default:
      return '';
  }
}

// Icon name (see Icon.svelte) for a status/severity. '' -> no icon (badge keeps its dot).
export function statusIcon(status: string): string {
  switch (status) {
    case 'succeeded':
    case 'active':
    case 'delivered':
      return 'check-circle';
    case 'failed':
    case 'dead':
    case 'error':
    case 'critical':
      return 'x-circle';
    case 'cancelled':
      return 'x';
    case 'executing':
    case 'dispatched':
    case 'delivering':
      return 'play';
    case 'paused':
    case 'suspended':
    case 'draining':
      return 'pause';
    case 'warning':
    case 'retry-after':
      return 'warn';
    case 'ready':
    case 'pending':
    case 'scheduled':
      return 'clock';
    case 'orphaned':
      return 'minus-circle';
    default:
      return '';
  }
}

export const TERMINAL_STATUSES = ['succeeded', 'failed', 'cancelled'];
